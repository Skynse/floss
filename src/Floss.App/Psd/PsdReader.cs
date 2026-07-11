using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace Floss.App.Psd;

// ── Public document model ────────────────────────────────────────────────────

public sealed class PsdDocument
{
    public int Width { get; init; }
    public int Height { get; init; }
    public List<PsdNode> Layers { get; } = new();
}

public abstract class PsdNode
{
    public string Name { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public byte Opacity { get; set; } = 255;
    public bool Clipping { get; set; }
    public string BlendMode { get; set; } = "norm"; // 4-char PSD key
}

public sealed class PsdLayerNode : PsdNode
{
    public int Top { get; set; }
    public int Left { get; set; }
    public int Bottom { get; set; }
    public int Right { get; set; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public byte[]? Bgra { get; set; } // null = empty / transparent layer
    public byte[]? MaskPlane { get; set; }
    public int MaskTop { get; set; }
    public int MaskLeft { get; set; }
    public int MaskBottom { get; set; }
    public int MaskRight { get; set; }
    public bool MaskDisabled { get; set; }
}

public sealed class PsdGroupNode : PsdNode
{
    public bool IsOpen { get; set; } = true;
    public List<PsdNode> Children { get; } = new();
}

// ── Reader ───────────────────────────────────────────────────────────────────

public static class PsdReader
{
    public static PsdDocument Read(Stream stream)
    {
        var r = new BEReader(stream);

        // ── Header ──────────────────────────────────────────────────────────
        var sig = new byte[4];
        r.ReadExact(sig);
        if (sig[0] != '8' || sig[1] != 'B' || sig[2] != 'P' || sig[3] != 'S')
            throw new InvalidDataException("Not a PSD file.");

        var version = r.U16();
        if (version != 1)
            throw new NotSupportedException("PSB (Large Document) format is not supported.");

        r.Skip(6);                      // reserved
        r.U16();                        // channel count (ignored; we read per-layer)
        var height = (int)r.U32();
        var width = (int)r.U32();
        var bitDepth = r.U16();        // 8, 16, or 32
        var colorMode = r.U16();        // 3 = RGB

        // ── Skippable sections ───────────────────────────────────────────────
        r.Skip((int)r.U32()); // color mode data
        r.Skip((int)r.U32()); // image resources

        // ── Layer and mask info ──────────────────────────────────────────────
        var layerMaskLen = r.U32();
        if (layerMaskLen == 0)
            return new PsdDocument { Width = width, Height = height };

        var layerMaskEnd = stream.Position + layerMaskLen;

        var layerInfoLen = r.U32();
        if (layerInfoLen == 0)
        {
            stream.Position = layerMaskEnd;
            return new PsdDocument { Width = width, Height = height };
        }

        var layerInfoEnd = stream.Position + layerInfoLen;

        var layerCount = Math.Abs(r.I16()); // negative => merged alpha present, abs = count

        // ── Layer records ────────────────────────────────────────────────────
        var records = new LayerRecord[layerCount];

        Span<byte> buf4 = stackalloc byte[4];

        for (int i = 0; i < layerCount; i++)
        {
            var rec = new LayerRecord();
            rec.Top = r.I32();
            rec.Left = r.I32();
            rec.Bottom = r.I32();
            rec.Right = r.I32();

            var chanCount = r.U16();
            rec.ChannelIds = new short[chanCount];
            rec.ChannelDataLens = new long[chanCount];
            for (int c = 0; c < chanCount; c++)
            {
                rec.ChannelIds[c] = r.I16();
                rec.ChannelDataLens[c] = r.U32(); // 4 bytes for PSD (8 for PSB)
            }

            r.ReadExact(buf4); // "8BIM"
            r.ReadExact(buf4);
            rec.BlendMode = Encoding.ASCII.GetString(buf4);

            rec.Opacity = r.Byte();
            rec.Clipping = r.Byte() != 0;
            var flags = r.Byte();
            rec.IsVisible = (flags & 2) == 0; // bit 1: visibility bit (0 = visible)
            r.Skip(1); // filler

            var extraLen = r.U32();
            var extraEnd = stream.Position + extraLen;

            // layer mask data
            var maskDataLen = (int)r.U32();
            if (maskDataLen > 0)
            {
                var maskDataEnd = stream.Position + maskDataLen;
                rec.MaskTop = r.I32();
                rec.MaskLeft = r.I32();
                rec.MaskBottom = r.I32();
                rec.MaskRight = r.I32();
                rec.MaskDefault = r.Byte();
                var maskFlags = r.Byte();
                rec.MaskDisabled = (maskFlags & 2) != 0;
                rec.HasMaskData = true;
                stream.Position = maskDataEnd;
            }
            // layer blending ranges
            r.Skip((int)r.U32());

            // layer name (Pascal string padded to 4-byte boundary)
            var nameLen = r.Byte();
            var nameData = new byte[nameLen];
            r.ReadExact(nameData);
            rec.Name = Encoding.UTF8.GetString(nameData);
            var namePad = (4 - ((nameLen + 1) & 3)) & 3;
            r.Skip(namePad);

            // additional layer info blocks
            while (stream.Position < extraEnd - 11)
            {
                r.ReadExact(buf4);
                // accept "8BIM" or "8B64"
                if (buf4[0] != '8' || buf4[1] != 'B')
                    break;

                r.ReadExact(buf4);
                var aiKey = Encoding.ASCII.GetString(buf4);
                var aiLen = r.U32();
                var aiEnd = stream.Position + aiLen;

                if (aiKey is "lsct" or "lsdk")
                {
                    rec.SectionType = (int)r.U32();
                    if (aiLen >= 12)
                    {
                        r.Skip(4); // "8BIM"
                        r.ReadExact(buf4);
                        rec.DividerBlendMode = Encoding.ASCII.GetString(buf4);
                    }
                }

                stream.Position = aiEnd;
            }

            stream.Position = extraEnd;
            records[i] = rec;
        }

        // ── Channel pixel data — buffer sequentially, decode in parallel ─────
        // PSD channel data must be read in stream order (sequential), but
        // decompression is CPU-bound and independent per layer. Buffer every
        // compressed channel block first, then decode all layers in parallel.
        for (int i = 0; i < layerCount; i++)
        {
            var rec = records[i];
            var layerW = rec.Right - rec.Left;
            var layerH = rec.Bottom - rec.Top;

            for (int c = 0; c < rec.ChannelIds.Length; c++)
            {
                var chanId = rec.ChannelIds[c];
                var chanLen = (int)rec.ChannelDataLens[c];
                var chanEnd = stream.Position + chanLen;

                if (layerW > 0 && layerH > 0 && chanLen > 0)
                {
                    var raw = new byte[chanLen];
                    r.ReadExact(raw);
                    rec.ChannelRaw[chanId] = raw;
                }
                else
                {
                    stream.Position = chanEnd;
                }
            }
        }

        stream.Position = layerMaskEnd;

        // Decode channels and assemble BGRA in parallel (each record is independent).
        Parallel.ForEach(records, rec =>
        {
            var layerW = rec.Right - rec.Left;
            var layerH = rec.Bottom - rec.Top;

            foreach (var (chanId, raw) in rec.ChannelRaw)
            {
                if (raw.Length < 2) continue;
                var compression = (ushort)((raw[0] << 8) | raw[1]);
                int decodeW = layerW, decodeH = layerH;
                if (chanId == -2 && rec.HasMaskData)
                {
                    decodeW = rec.MaskRight - rec.MaskLeft;
                    decodeH = rec.MaskBottom - rec.MaskTop;
                }
                var plane = DecodeBufferedPlane(raw, 2, compression, decodeW, decodeH, bitDepth);
                if (plane != null)
                    rec.Channels[chanId] = plane;
            }
            rec.ChannelRaw.Clear();

            if (rec.SectionType == 0 && rec.Channels.Count > 0 && layerW > 0 && layerH > 0)
            {
                rec.Channels.TryGetValue(-2, out rec.MaskPlane);
                rec.PrecomputedBgra = AssembleBgra(rec, layerW, layerH);
                rec.Channels.Clear();
            }
        });

        // ── Build layer tree ─────────────────────────────────────────────────
        var doc = new PsdDocument { Width = width, Height = height };
        BuildTree(records, doc.Layers);
        return doc;
    }

    // In the PSDs this importer targets, records arrive bottom-to-top. Groups
    // are encoded as:
    // [section divider, type=3] bottom/bounding marker
    // [child layers...] group contents, bottom-to-top
    // [folder layer, type=1/2] top/header with group metadata
    // Forward scan therefore produces the compositor's bottom-to-top order.
    private static void BuildTree(LayerRecord[] records, List<PsdNode> root)
    {
        var stack = new Stack<(PsdGroupNode group, List<PsdNode> parent)>();
        var current = root;

        for (int i = 0; i < records.Length; i++)
        {
            var rec = records[i];

            if (rec.SectionType == 3)
            {
                var group = new PsdGroupNode();
                stack.Push((group, current));
                current = group.Children;
            }
            else if (rec.SectionType == 1 || rec.SectionType == 2)
            {
                if (stack.Count > 0)
                {
                    var (group, parent) = stack.Pop();
                    group.Name = rec.Name;
                    group.IsVisible = rec.IsVisible;
                    group.Opacity = rec.Opacity;
                    group.Clipping = rec.Clipping;
                    group.BlendMode = string.IsNullOrEmpty(rec.DividerBlendMode) ? rec.BlendMode : rec.DividerBlendMode;
                    group.IsOpen = rec.SectionType == 1;
                    parent.Add(group);
                    current = parent;
                }
            }
            else
            {
                var layerW = rec.Right - rec.Left;
                var layerH = rec.Bottom - rec.Top;
                var bgra = rec.PrecomputedBgra;
                rec.PrecomputedBgra = null;

                current.Add(new PsdLayerNode
                {
                    Name = rec.Name,
                    IsVisible = rec.IsVisible,
                    Opacity = rec.Opacity,
                    Clipping = rec.Clipping,
                    BlendMode = rec.BlendMode,
                    Top = rec.Top,
                    Left = rec.Left,
                    Bottom = rec.Bottom,
                    Right = rec.Right,
                    Bgra = bgra,
                    MaskPlane = rec.MaskPlane,
                    MaskTop = rec.MaskTop,
                    MaskLeft = rec.MaskLeft,
                    MaskBottom = rec.MaskBottom,
                    MaskRight = rec.MaskRight,
                    MaskDisabled = rec.MaskDisabled,
                });
            }
        }

        // Flush unclosed groups (malformed PSDs)
        while (stack.Count > 0)
        {
            var (group, parent) = stack.Pop();
            parent.Add(group);
        }
    }

    private static byte[] AssembleBgra(LayerRecord rec, int w, int h)
    {
        rec.Channels.TryGetValue(0, out var rPlane);
        rec.Channels.TryGetValue(1, out var gPlane);
        rec.Channels.TryGetValue(2, out var bPlane);
        rec.Channels.TryGetValue(-1, out var aPlane);

        var bgra = new byte[w * h * 4];
        var pixels = w * h;

        for (int p = 0; p < pixels; p++)
        {
            bgra[p * 4 + 0] = bPlane != null ? bPlane[p] : (byte)0;
            bgra[p * 4 + 1] = gPlane != null ? gPlane[p] : (byte)0;
            bgra[p * 4 + 2] = rPlane != null ? rPlane[p] : (byte)0;
            bgra[p * 4 + 3] = aPlane != null ? aPlane[p] : (byte)255;
        }

        return bgra;
    }

    // Dispatch to the appropriate decoder based on the compression type embedded
    // at the start of the raw channel buffer (first 2 bytes = big-endian ushort).
    private static byte[]? DecodeBufferedPlane(byte[] raw, int dataOffset, ushort compression, int w, int h, int bitDepth)
    {
        return compression switch
        {
            0 => DecodeRaw(raw.AsSpan(dataOffset), w, h, bitDepth),
            1 => DecodePackBits(raw.AsSpan(dataOffset), w, h, bitDepth),
            2 => DecodeZip(raw, dataOffset, raw.Length - dataOffset, w, h, bitDepth, delta: false),
            3 => DecodeZip(raw, dataOffset, raw.Length - dataOffset, w, h, bitDepth, delta: true),
            _ => null
        };
    }

    private static byte[] DecodeRaw(ReadOnlySpan<byte> span, int w, int h, int bitDepth)
    {
        var plane = new byte[w * h];
        if (bitDepth == 8)
        {
            span[..(w * h)].CopyTo(plane);
        }
        else if (bitDepth == 16)
        {
            // PSD 16-bit raw: big-endian u16 samples — take the high byte (stride 2).
            for (int i = 0; i < w * h; i++)
                plane[i] = span[i * 2];
        }
        // 32-bit: return transparent zeros (not supported for display)
        return plane;
    }

    private static byte[] DecodePackBits(ReadOnlySpan<byte> span, int w, int h, int bitDepth)
    {
        var plane = new byte[w * h];
        var pixPerRow = bitDepth == 8 ? w : w * (bitDepth / 8);
        var rowBuf = ArrayPool<byte>.Shared.Rent(pixPerRow);

        try
        {
            // First h*2 bytes: big-endian per-row compressed lengths.
            var dataOffset = h * 2;

            for (int y = 0; y < h; y++)
            {
                var rowLen = (ushort)((span[y * 2] << 8) | span[y * 2 + 1]);
                UnpackBits(span.Slice(dataOffset, rowLen), rowBuf, pixPerRow);
                dataOffset += rowLen;

                if (bitDepth == 8)
                {
                    rowBuf.AsSpan(0, w).CopyTo(plane.AsSpan(y * w, w));
                }
                else if (bitDepth == 16)
                {
                    for (int x = 0; x < w; x++)
                        plane[y * w + x] = rowBuf[x * 2]; // high byte of big-endian u16
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuf);
        }

        return plane;
    }

    private static void UnpackBits(ReadOnlySpan<byte> src, byte[] dst, int dstLen)
    {
        int si = 0, di = 0;
        while (si < src.Length && di < dstLen)
        {
            var n = (sbyte)src[si++];
            if (n >= 0)
            {
                var count = n + 1;
                while (count-- > 0 && di < dstLen && si < src.Length)
                    dst[di++] = src[si++];
            }
            else if (n != -128)
            {
                var count = 1 - n;
                var val = src[si++];
                while (count-- > 0 && di < dstLen)
                    dst[di++] = val;
            }
        }
    }

    private static byte[]? DecodeZip(byte[] raw, int offset, int length, int w, int h, int bitDepth, bool delta)
    {
        try
        {
            using var ms = new MemoryStream(raw, offset, length, writable: false);
            using var deflate = new ZLibStream(ms, CompressionMode.Decompress);
            var bytesPerSample = bitDepth / 8;
            var buf = new byte[w * h * bytesPerSample];

            int read = 0;
            while (read < buf.Length)
            {
                var n = deflate.Read(buf, read, buf.Length - read);
                if (n == 0) break;
                read += n;
            }

            if (delta)
                ApplyDeltaDecode(buf, w, h, bitDepth);

            if (bitDepth == 8)
                return buf;

            var plane = new byte[w * h];
            for (int i = 0; i < w * h; i++)
                plane[i] = buf[i * bytesPerSample]; // high byte
            return plane;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "PsdReader.DecompressPlane");
            Console.Error.WriteLine($"[Floss] PSD Zlib decompression failed: {ex.Message}");
            return null;
        }
    }

    private static void ApplyDeltaDecode(byte[] data, int w, int h, int bitDepth)
    {
        if (bitDepth == 8)
        {
            for (int y = 0; y < h; y++)
            {
                var off = y * w;
                for (int x = 1; x < w; x++)
                    data[off + x] = (byte)(data[off + x] + data[off + x - 1]);
            }
        }
        else if (bitDepth == 16)
        {
            var rowBytes = w * 2;
            for (int y = 0; y < h; y++)
            {
                var off = y * rowBytes;
                for (int x = 2; x < rowBytes; x++)
                    data[off + x] = (byte)(data[off + x] + data[off + x - 2]);
            }
        }
    }

    // ── Internal record type ─────────────────────────────────────────────────

    private sealed class LayerRecord
    {
        public int Top, Left, Bottom, Right;
        public short[] ChannelIds = [];
        public long[] ChannelDataLens = [];
        // Raw compressed bytes per channel, buffered during sequential stream read.
        public Dictionary<short, byte[]> ChannelRaw = new();
        // Decoded 8-bit planes, populated during parallel decode pass.
        public Dictionary<short, byte[]> Channels = new();
        // Pre-assembled BGRA, set during parallel pass so BuildTree just grabs it.
        public byte[]? PrecomputedBgra;
        // Decoded mask plane (channel id -2), if present.
        public byte[]? MaskPlane;
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public byte MaskDefault = 255;
        public bool MaskDisabled;
        public bool HasMaskData;
        public string BlendMode = "norm";
        public string DividerBlendMode = "";
        public byte Opacity = 255;
        public bool Clipping;
        public bool IsVisible = true;
        public string Name = "";
        public int SectionType;     // 0=normal, 1=openGroup, 2=closedGroup, 3=boundingDivider
    }
}

// ── Big-endian stream reader ─────────────────────────────────────────────────

internal sealed class BEReader
{
    private readonly Stream _s;
    private readonly byte[] _buf = new byte[8];

    public BEReader(Stream s) => _s = s;

    public byte Byte()
    {
        var b = _s.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public short I16()
    {
        ReadExact(2);
        return BinaryPrimitives.ReadInt16BigEndian(_buf);
    }

    public ushort U16()
    {
        ReadExact(2);
        return BinaryPrimitives.ReadUInt16BigEndian(_buf);
    }

    public int I32()
    {
        ReadExact(4);
        return BinaryPrimitives.ReadInt32BigEndian(_buf);
    }

    public uint U32()
    {
        ReadExact(4);
        return BinaryPrimitives.ReadUInt32BigEndian(_buf);
    }

    public void ReadExact(Span<byte> dst)
    {
        int read = 0;
        while (read < dst.Length)
        {
            var n = _s.Read(dst[read..]);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void ReadExact(byte[] dst) => ReadExact(dst.AsSpan());

    private void ReadExact(int count) => ReadExact(_buf.AsSpan(0, count));

    public void Skip(int bytes)
    {
        if (bytes <= 0) return;
        if (_s.CanSeek) { _s.Seek(bytes, SeekOrigin.Current); return; }
        Span<byte> tmp = stackalloc byte[Math.Min(bytes, 4096)];
        while (bytes > 0)
        {
            var n = _s.Read(tmp[..Math.Min(bytes, tmp.Length)]);
            if (n == 0) throw new EndOfStreamException();
            bytes -= n;
        }
    }
}
