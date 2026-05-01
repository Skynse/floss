using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia.Media;
using SkiaSharp;

namespace Floss.App.Brushes;

public static class AbrImporter
{
    public static List<BrushAsset> Import(Stream stream, out string diagnostic)
    {
        var results = new List<BrushAsset>();
        var r = new AbrReader(stream);

        var version = r.U16();
        var extra = r.U16();

        if (version is 1 or 2)
        {
            var count = (int)extra;
            for (var i = 0; i < count; i++)
            {
                try { TryReadV12(r, version, results); }
                catch { /* skip corrupt brush */ }
            }
            diagnostic = $"v{version}, {count} entries declared";
        }
        else if (version is 6 or 7)
        {
            var subVersion = (int)extra;
            var errors = 0;
            while (stream.Position < stream.Length - 8)
            {
                try
                {
                    if (!TryReadV6(r, subVersion, results)) break;
                }
                catch (Exception ex)
                {
                    errors++;
                    if (errors > 5) break;
                    _ = ex;
                }
            }
            diagnostic = $"v{version} sub{subVersion}, {results.Count} imported, {errors} errors";
        }
        else if (version == 10)
        {
            var errors = 0;
            try { ReadV10(stream, results, ref errors); }
            catch { errors++; }
            diagnostic = $"v10 sub{extra}, {results.Count} imported, {errors} errors";
        }
        else
        {
            diagnostic = $"unsupported ABR version {version} (sub {extra})";
        }

        return results;
    }

    // ── v10 ──────────────────────────────────────────────────────────────────

    private static void ReadV10(Stream stream, List<BrushAsset> results, ref int errors)
    {
        var hdr = new byte[8];
        var lenBuf = new byte[4];

        while (stream.Position <= stream.Length - 12)
        {
            if (stream.Read(hdr, 0, 8) < 8) break;

            if (hdr[0] != '8' || hdr[1] != 'B' || hdr[2] != 'I' || hdr[3] != 'M') break;

            if (stream.Read(lenBuf, 0, 4) < 4) break;
            var blockLen = (long)(((uint)lenBuf[0] << 24) | ((uint)lenBuf[1] << 16) |
                                  ((uint)lenBuf[2] << 8) | lenBuf[3]);

            var blockStart = stream.Position;
            var tagStr = Encoding.ASCII.GetString(hdr, 4, 4);

            if (tagStr == "samp" && blockLen is > 0 and < 200_000_000)
            {
                var data = new byte[blockLen];
                var totalRead = 0;
                while (totalRead < (int)blockLen)
                {
                    var n = stream.Read(data, totalRead, (int)blockLen - totalRead);
                    if (n == 0) break;
                    totalRead += n;
                }
                ScanV10Samp(data, results, ref errors);
            }

            if (stream.CanSeek)
                stream.Seek(blockStart + blockLen, SeekOrigin.Begin);
            else
            {
                var remaining = blockLen - (stream.Position - blockStart);
                var skipBuf = new byte[4096];
                while (remaining > 0)
                {
                    var n = stream.Read(skipBuf, 0, (int)Math.Min(remaining, skipBuf.Length));
                    if (n == 0) break;
                    remaining -= n;
                }
            }
        }
    }

    private static void ScanV10Samp(byte[] data, List<BrushAsset> results, ref int errors)
    {
        var brushIndex = 0;
        for (var i = 0; i < data.Length - 38; i++)
        {
            if (data[i] != (byte)'$') continue;
            if (!IsV10Guid(data, i)) continue;

            var dataStart = i + 38; // byte after null terminator
            try
            {
                var asset = ParseV10Brush(data, dataStart, brushIndex);
                if (asset != null)
                {
                    results.Add(asset);
                    brushIndex++;
                }
            }
            catch { errors++; }
        }
    }

    private static bool IsV10Guid(byte[] data, int pos)
    {
        if (pos + 38 > data.Length) return false;
        if (data[pos + 9] != '-') return false;
        if (data[pos + 14] != '-') return false;
        if (data[pos + 19] != '-') return false;
        if (data[pos + 24] != '-') return false;
        if (data[pos + 37] != 0) return false;

        for (var i = 1; i <= 8; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 10; i <= 13; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 15; i <= 18; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 20; i <= 23; i++) if (!IsHexByte(data[pos + i])) return false;
        for (var i = 25; i <= 36; i++) if (!IsHexByte(data[pos + i])) return false;
        return true;
    }

    private static bool IsHexByte(byte b) =>
        (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    private static BrushAsset? ParseV10Brush(byte[] data, int ds, int brushIndex)
    {
        if (ds + 283 > data.Length) return null;

        var topCrop = (data[ds + 13] << 8) | data[ds + 14];
        var leftCrop = (data[ds + 17] << 8) | data[ds + 18];
        var renderH = (data[ds + 21] << 8) | data[ds + 22];
        var renderW = (data[ds + 25] << 8) | data[ds + 26];
        var depth = data[ds + 280];
        var comp = data[ds + 281];

        if (depth != 8) return null;
        if (renderW <= 0 || renderH <= 0 || renderW > 5000 || renderH > 5000) return null;

        var hActual = renderH - topCrop;
        var wActual = renderW - leftCrop;
        if (hActual <= 0 || wActual <= 0) return null;

        var pixelOff = ds + 282;
        var storedPixels = new byte[hActual * wActual];

        if (comp == 0) // raw
        {
            var needed = hActual * wActual;
            if (pixelOff + needed > data.Length) return null;
            Array.Copy(data, pixelOff, storedPixels, 0, needed);
        }
        else if (comp == 1) // PackBits RLE
        {
            var rcBase = pixelOff;
            var rdBase = rcBase + hActual * 2;
            if (rdBase > data.Length) return null;

            var pos = rdBase;
            for (var y = 0; y < hActual; y++)
            {
                var rowCount = (data[rcBase + y * 2] << 8) | data[rcBase + y * 2 + 1];
                if (pos + rowCount > data.Length) return null;
                var rowSrc = data.AsSpan(pos, rowCount).ToArray();
                UnpackBitsRow(rowSrc, storedPixels, y * wActual, wActual, 8);
                pos += rowCount;
            }
        }
        else return null;

        // Invert: ABR stores 0=opaque/dark, 255=transparent; alpha needs 255=opaque
        for (var j = 0; j < storedPixels.Length; j++)
            storedPixels[j] = (byte)(255 - storedPixels[j]);

        byte[] fullPixels;
        if (topCrop == 0 && leftCrop == 0)
        {
            fullPixels = storedPixels;
        }
        else
        {
            fullPixels = new byte[renderH * renderW]; // transparent background
            for (var y = 0; y < hActual; y++)
            {
                var dstOff = (topCrop + y) * renderW + leftCrop;
                if (dstOff + wActual > fullPixels.Length) break;
                Array.Copy(storedPixels, y * wActual, fullPixels, dstOff, wActual);
            }
        }

        return MakeAsset($"Brush {brushIndex + 1}", 25, fullPixels, renderW, renderH);
    }

    // ── v1 / v2 ──────────────────────────────────────────────────────────────

    private static void TryReadV12(AbrReader r, int version, List<BrushAsset> results)
    {
        var brushType = r.U16();
        var dataLength = (int)r.U32();
        var blockStart = r.Position;

        try
        {
            if (brushType != 2) return; // 1=computed, skip

            r.Skip(4); // misc flags
            var spacing = r.U16();

            string name;
            if (version == 1)
            {
                var len = r.Byte();
                var chars = new byte[len];
                r.ReadExact(chars);
                name = Encoding.ASCII.GetString(chars);
            }
            else
            {
                var charCount = r.U16();
                var charBytes = new byte[charCount * 2];
                r.ReadExact(charBytes);
                name = Encoding.BigEndianUnicode.GetString(charBytes).TrimEnd('\0');
            }

            r.Skip(1); // antiAlias
            var top = r.I16();
            var left = r.I16();
            var bottom = r.I16();
            var right = r.I16();
            var w = right - left;
            var h = bottom - top;
            var depth = r.U16();
            var comp = r.Byte();

            if (w <= 0 || h <= 0 || w > 5000 || h > 5000) return;

            var pixels = ReadPixels(r, w, h, depth, comp);
            if (pixels == null) return;

            results.Add(MakeAsset(name, spacing, pixels, w, h));
        }
        finally
        {
            var remaining = dataLength - (int)(r.Position - blockStart);
            if (remaining > 0) r.Skip(remaining);
        }
    }

    // ── v6 / v7 ──────────────────────────────────────────────────────────────

    private static bool TryReadV6(AbrReader r, int subVersion, List<BrushAsset> results)
    {
        var brushType = r.I32();
        var blockSize = r.I32();
        if (blockSize <= 0) return false;

        var blockStart = r.Position;

        try
        {
            if (brushType != 2) return true; // computed brush, skip

            r.Skip(10); // misc
            var spacing = r.U16();

            string name;
            if (subVersion == 1)
            {
                var charCount = r.U16();
                var charBytes = new byte[charCount * 2];
                r.ReadExact(charBytes);
                name = Encoding.BigEndianUnicode.GetString(charBytes).TrimEnd('\0');
            }
            else
            {
                r.Skip(4);
                name = "Brush";
            }

            r.Skip(1); // antiAlias
            var top = r.I16();
            var left = r.I16();
            var bottom = r.I16();
            var right = r.I16();
            var w = right - left;
            var h = bottom - top;
            var depth = r.U16();
            var comp = r.Byte();

            if (w > 0 && h > 0 && w <= 5000 && h <= 5000)
            {
                var pixels = ReadPixels(r, w, h, depth, comp);
                if (pixels != null)
                    results.Add(MakeAsset(name, spacing, pixels, w, h));
            }
        }
        finally
        {
            var remaining = blockSize - (int)(r.Position - blockStart);
            if (remaining > 0) r.Skip(remaining);
        }

        return true;
    }

    // ── Pixel decoding ────────────────────────────────────────────────────────

    private static byte[]? ReadPixels(AbrReader r, int w, int h, int depth, int comp)
    {
        if (depth != 8 && depth != 16) return null;

        var pixels = new byte[w * h];

        if (comp == 0) // raw
        {
            if (depth == 8)
            {
                r.ReadExact(pixels);
            }
            else
            {
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = r.Byte();
                    r.Skip(1);
                }
            }
        }
        else if (comp == 1) // PackBits RLE
        {
            var rowCounts = new int[h];
            for (var y = 0; y < h; y++)
                rowCounts[y] = r.U16();

            for (var y = 0; y < h; y++)
            {
                var rowData = new byte[rowCounts[y]];
                r.ReadExact(rowData);
                UnpackBitsRow(rowData, pixels, y * w, w, depth);
            }
        }
        else return null;

        return pixels;
    }

    private static void UnpackBitsRow(byte[] src, byte[] dst, int dstOffset, int w, int depth)
    {
        var bytesPerPixel = depth / 8;
        var expectedBytes = w * bytesPerPixel;
        Span<byte> row = stackalloc byte[Math.Min(expectedBytes, 16384)];
        if (expectedBytes > 16384) row = new byte[expectedBytes];

        var outPos = 0;
        var inPos = 0;

        while (inPos < src.Length && outPos < expectedBytes)
        {
            var n = (sbyte)src[inPos++];
            if (n >= 0)
            {
                var count = n + 1;
                var copy = Math.Min(count, expectedBytes - outPos);
                src.AsSpan(inPos, copy).CopyTo(row[outPos..]);
                outPos += copy;
                inPos += count;
            }
            else if (n != -128)
            {
                var count = -n + 1;
                var fill = Math.Min(count, expectedBytes - outPos);
                var val = src[inPos++];
                row.Slice(outPos, fill).Fill(val);
                outPos += fill;
            }
        }

        if (depth == 8)
        {
            row[..w].CopyTo(dst.AsSpan(dstOffset));
        }
        else
        {
            // 16-bit: take high byte of each sample
            for (var x = 0; x < w; x++)
                dst[dstOffset + x] = row[x * 2];
        }
    }

    // ── PNG construction ──────────────────────────────────────────────────────

    private static unsafe byte[] PixelsToPng(byte[] pixels, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var ptr = (byte*)bmp.GetPixels().ToPointer();
        var rowBytes = bmp.RowBytes;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var alpha = pixels[y * w + x];
                var p = ptr + y * rowBytes + x * 4;
                p[0] = 0;     // B
                p[1] = 0;     // G
                p[2] = 0;     // R
                p[3] = alpha; // A
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ── Asset construction ────────────────────────────────────────────────────

    private static BrushAsset MakeAsset(string name, int spacingPct, byte[] pixels, int w, int h)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Imported Brush" : name.Trim();
        var spacing = Math.Clamp(spacingPct / 100.0, 0.02, 1.0);
        var pngBytes = PixelsToPng(pixels, w, h);

        var tipData = new BrushTipData
        {
            Kind = BrushTipStorageKind.EmbeddedPng,
            PngBytes = pngBytes
        };

        var preset = new BrushPreset(cleanName, BrushKind.Ink, 40, 1.0, 0.9, spacing, Color.Parse("#111111"), 100.0)
        {
            Dynamics = new BrushDynamics { Size = CurveOption.Pressure(1.2f) },
            Tip = tipData.CreateTip()
        };

        return new BrushAsset
        {
            Id = Guid.NewGuid().ToString("N"),
            Preset = preset,
            Tip = tipData
        };
    }

    // ── Big-endian stream reader ──────────────────────────────────────────────

    private sealed class AbrReader(Stream s)
    {
        private readonly byte[] _buf = new byte[4];

        public long Position => s.Position;

        public byte Byte()
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return (byte)b;
        }

        public short I16()
        {
            ReadExact(2);
            return (short)((_buf[0] << 8) | _buf[1]);
        }

        public ushort U16()
        {
            ReadExact(2);
            return (ushort)((_buf[0] << 8) | _buf[1]);
        }

        public int I32()
        {
            ReadExact(4);
            return (_buf[0] << 24) | (_buf[1] << 16) | (_buf[2] << 8) | _buf[3];
        }

        public uint U32()
        {
            ReadExact(4);
            return (uint)((_buf[0] << 24) | (_buf[1] << 16) | (_buf[2] << 8) | _buf[3]);
        }

        public void ReadExact(byte[] dst) => ReadExact(dst.AsSpan());

        public void ReadExact(Span<byte> dst)
        {
            var read = 0;
            while (read < dst.Length)
            {
                var n = s.Read(dst[read..]);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
        }

        public void Skip(int bytes)
        {
            if (bytes <= 0) return;
            if (s.CanSeek) { s.Seek(bytes, SeekOrigin.Current); return; }
            Span<byte> tmp = stackalloc byte[Math.Min(bytes, 4096)];
            while (bytes > 0)
            {
                var n = s.Read(tmp[..Math.Min(bytes, tmp.Length)]);
                if (n == 0) throw new EndOfStreamException();
                bytes -= n;
            }
        }

        private void ReadExact(int count) => ReadExact(_buf.AsSpan(0, count));
    }
}
