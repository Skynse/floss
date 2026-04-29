using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Floss.App.Psd;

// ── Public document model ────────────────────────────────────────────────────

public sealed class PsdDocument
{
    public int Width  { get; init; }
    public int Height { get; init; }
    public List<PsdNode> Layers { get; } = new();
}

public abstract class PsdNode
{
    public string Name      { get; set; } = "";
    public bool   IsVisible { get; set; } = true;
    public byte   Opacity   { get; set; } = 255;
    public bool   Clipping  { get; set; }
    public string BlendMode { get; set; } = "norm"; // 4-char PSD key
}

public sealed class PsdLayerNode : PsdNode
{
    public int    Top    { get; set; }
    public int    Left   { get; set; }
    public int    Bottom { get; set; }
    public int    Right  { get; set; }
    public int    Width  => Right - Left;
    public int    Height => Bottom - Top;
    public byte[]? Bgra  { get; set; } // null = empty / transparent layer
}

public sealed class PsdGroupNode : PsdNode
{
    public bool          IsOpen   { get; set; } = true;
    public List<PsdNode> Children { get; }      = new();
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
        var height    = (int)r.U32();
        var width     = (int)r.U32();
        var bitDepth  = r.U16();        // 8, 16, or 32
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
            rec.Top    = r.I32();
            rec.Left   = r.I32();
            rec.Bottom = r.I32();
            rec.Right  = r.I32();

            var chanCount = r.U16();
            rec.ChannelIds      = new short[chanCount];
            rec.ChannelDataLens = new long [chanCount];
            for (int c = 0; c < chanCount; c++)
            {
                rec.ChannelIds[c]      = r.I16();
                rec.ChannelDataLens[c] = r.U32(); // 4 bytes for PSD (8 for PSB)
            }

            r.ReadExact(buf4); // "8BIM"
            r.ReadExact(buf4);
            rec.BlendMode = Encoding.ASCII.GetString(buf4);

            rec.Opacity   = r.Byte();
            rec.Clipping  = r.Byte() != 0;
            var flags     = r.Byte();
            rec.IsVisible = (flags & 2) == 0; // bit 1: visibility bit (0 = visible)
            r.Skip(1); // filler

            var extraLen = r.U32();
            var extraEnd = stream.Position + extraLen;

            // layer mask data
            r.Skip((int)r.U32());
            // layer blending ranges
            r.Skip((int)r.U32());

            // layer name (Pascal string padded to 4-byte boundary)
            var nameLen  = r.Byte();
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

        // ── Channel pixel data ───────────────────────────────────────────────
        // One block per layer (same order as records), one compression+data block per channel.
        for (int i = 0; i < layerCount; i++)
        {
            var rec    = records[i];
            var layerW = rec.Right  - rec.Left;
            var layerH = rec.Bottom - rec.Top;

            for (int c = 0; c < rec.ChannelIds.Length; c++)
            {
                var chanId  = rec.ChannelIds[c];
                var chanLen = rec.ChannelDataLens[c];
                var chanEnd = stream.Position + chanLen;

                if (layerW > 0 && layerH > 0)
                {
                    var compression = r.U16();
                    var plane = ReadPlane(r, stream, compression, layerW, layerH, bitDepth);
                    if (plane != null)
                        rec.Channels[chanId] = plane;
                }

                stream.Position = chanEnd;
            }
        }

        stream.Position = layerMaskEnd;

        // ── Build layer tree ─────────────────────────────────────────────────
        // This reader normalizes PSD layer records into bottom-to-top order,
        // which is the order expected by the compositor and layer panel.
        var doc = new PsdDocument { Width = width, Height = height };
        BuildTree(records, doc.Layers);
        return doc;
    }

    // In the PSDs this importer targets, records arrive bottom-to-top. Groups
    // are encoded as:
    //   [section divider, type=3]      bottom/bounding marker
    //   [child layers...]              group contents, bottom-to-top
    //   [folder layer, type=1/2]       top/header with group metadata
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
                // Bounding section divider: opens a group scope.
                // Properties are set later when we find the matching folder (type 1/2).
                var group = new PsdGroupNode();
                stack.Push((group, current));
                current = group.Children;
            }
            else if (rec.SectionType == 1 || rec.SectionType == 2)
            {
                // Group folder: supplies the name/blend for the most-recently-opened group.
                if (stack.Count > 0)
                {
                    var (group, parent) = stack.Pop();
                    group.Name      = rec.Name;
                    group.IsVisible = rec.IsVisible;
                    group.Opacity   = rec.Opacity;
                    group.Clipping  = rec.Clipping;
                    group.BlendMode = string.IsNullOrEmpty(rec.DividerBlendMode) ? rec.BlendMode : rec.DividerBlendMode;
                    group.IsOpen    = rec.SectionType == 1;
                    parent.Add(group);
                    current = parent;
                }
                // else: orphaned folder record, ignore
            }
            else
            {
                // Normal layer
                var layerW = rec.Right - rec.Left;
                var layerH = rec.Bottom - rec.Top;
                byte[]? bgra = null;

                if (layerW > 0 && layerH > 0 && rec.Channels.Count > 0)
                    bgra = AssembleBgra(rec, layerW, layerH);

                current.Add(new PsdLayerNode
                {
                    Name      = rec.Name,
                    IsVisible = rec.IsVisible,
                    Opacity   = rec.Opacity,
                    Clipping  = rec.Clipping,
                    BlendMode = rec.BlendMode,
                    Top       = rec.Top,
                    Left      = rec.Left,
                    Bottom    = rec.Bottom,
                    Right     = rec.Right,
                    Bgra      = bgra,
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
        rec.Channels.TryGetValue(0,  out var rPlane);
        rec.Channels.TryGetValue(1,  out var gPlane);
        rec.Channels.TryGetValue(2,  out var bPlane);
        rec.Channels.TryGetValue(-1, out var aPlane);

        var bgra   = new byte[w * h * 4];
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

    // Returns an 8-bit-per-sample plane of width×height bytes, or null on failure.
    private static byte[]? ReadPlane(BEReader r, Stream stream, ushort compression, int w, int h, int bitDepth)
    {
        return compression switch
        {
            0 => ReadRaw(r, w, h, bitDepth),
            1 => ReadPackBits(r, w, h, bitDepth),
            2 => ReadZip(stream, w, h, bitDepth, delta: false),
            3 => ReadZip(stream, w, h, bitDepth, delta: true),
            _ => null
        };
    }

    private static byte[] ReadRaw(BEReader r, int w, int h, int bitDepth)
    {
        if (bitDepth == 8)
        {
            var plane = new byte[w * h];
            r.ReadExact(plane);
            return plane;
        }
        if (bitDepth == 16)
        {
            var plane = new byte[w * h];
            for (int i = 0; i < plane.Length; i++)
                plane[i] = r.Byte(); // high byte of 16-bit sample
            r.Skip(w * h); // skip low bytes
            return plane;
        }
        // 32-bit float — skip for now
        r.Skip(w * h * (bitDepth / 8));
        return new byte[w * h]; // transparent
    }

    private static byte[] ReadPackBits(BEReader r, int w, int h, int bitDepth)
    {
        // Row byte-count table (2 bytes per row, for each channel row)
        var rowLens = new ushort[h];
        for (int y = 0; y < h; y++)
            rowLens[y] = r.U16();

        var plane = new byte[w * h];
        var pixPerRow = bitDepth == 8 ? w : w * (bitDepth / 8);
        var rowOut = new byte[pixPerRow];

        for (int y = 0; y < h; y++)
        {
            var compressed = new byte[rowLens[y]];
            r.ReadExact(compressed);
            UnpackBits(compressed, rowOut, pixPerRow);

            if (bitDepth == 8)
            {
                Buffer.BlockCopy(rowOut, 0, plane, y * w, w);
            }
            else if (bitDepth == 16)
            {
                for (int x = 0; x < w; x++)
                    plane[y * w + x] = rowOut[x * 2]; // high byte
            }
        }

        return plane;
    }

    private static void UnpackBits(byte[] src, byte[] dst, int dstLen)
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
                var val   = src[si++];
                while (count-- > 0 && di < dstLen)
                    dst[di++] = val;
            }
        }
    }

    private static byte[]? ReadZip(Stream stream, int w, int h, int bitDepth, bool delta)
    {
        try
        {
            using var deflate = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            var bytesPerSample = bitDepth / 8;
            var rawLen = w * h * bytesPerSample;
            var raw = new byte[rawLen];

            int read = 0;
            while (read < rawLen)
            {
                var n = deflate.Read(raw, read, rawLen - read);
                if (n == 0) break;
                read += n;
            }

            if (delta)
                ApplyDeltaDecode(raw, w, h, bitDepth);

            if (bitDepth == 8)
                return raw;

            var plane = new byte[w * h];
            for (int i = 0; i < w * h; i++)
                plane[i] = raw[i * bytesPerSample]; // high byte
            return plane;
        }
        catch
        {
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
        public int    Top, Left, Bottom, Right;
        public short[] ChannelIds      = [];
        public long[]  ChannelDataLens = [];
        public Dictionary<short, byte[]> Channels = new();
        public string BlendMode        = "norm";
        public string DividerBlendMode = "";
        public byte   Opacity          = 255;
        public bool   Clipping;
        public bool   IsVisible        = true;
        public string Name             = "";
        public int    SectionType;     // 0=normal, 1=openGroup, 2=closedGroup, 3=boundingDivider
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
