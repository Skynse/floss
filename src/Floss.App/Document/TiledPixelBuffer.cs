using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SkiaSharp;

namespace Floss.App.Document;

public sealed class TiledPixelBuffer
{
    public const int TileSize = 64;
    private const int BytesPerPixel = 4;
    private const int TileBytes = TileSize * TileSize * BytesPerPixel;

    // Hot raw tiles — currently in active use.
    private readonly Dictionary<(int X, int Y), byte[]> _tiles = [];
    // Cold compressed tiles — swapped out to reduce memory.
    private readonly Dictionary<(int X, int Y), byte[]> _compressed = [];

    public TiledPixelBuffer(int width, int height)
    {
        MinX = 0;
        MinY = 0;
        MaxX = Math.Max(1, width);
        MaxY = Math.Max(1, height);
    }

    public int Width => Math.Max(1, MaxX - MinX);
    public int Height => Math.Max(1, MaxY - MinY);
    public PixelRegion Bounds => new(MinX, MinY, Width, Height);
    public int MinX { get; private set; }
    public int MinY { get; private set; }
    public int MaxX { get; private set; }
    public int MaxY { get; private set; }

    public int TileCount => _tiles.Count + _compressed.Count;

    public void Resize(int width, int height)
    {
        MinX = 0;
        MinY = 0;
        MaxX = Math.Max(1, width);
        MaxY = Math.Max(1, height);
        _tiles.Clear();
        _compressed.Clear();
    }

    private void ExtendBounds(int left, int top, int right, int bottom)
    {
        if (left < MinX) MinX = left;
        if (top < MinY) MinY = top;
        if (right > MaxX) MaxX = right;
        if (bottom > MaxY) MaxY = bottom;
    }

    // ── Compression ────────────────────────────────────────────────────────────

    public void CompressTiles()
    {
        if (_tiles.Count == 0) return;

        var toCompress = new List<((int X, int Y) Key, byte[] Tile)>(_tiles.Count);
        foreach (var (key, tile) in _tiles)
            toCompress.Add((key, tile));
        _tiles.Clear();

        foreach (var (key, tile) in toCompress)
        {
            var compressed = Deflate(tile);
            _compressed[key] = compressed.Length < tile.Length ? compressed : tile;
        }
    }

    private byte[] EnsureRaw((int X, int Y) key)
    {
        if (_tiles.TryGetValue(key, out var raw))
            return raw;

        if (!_compressed.Remove(key, out var compressed))
            return null!;

        raw = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
        _tiles[key] = raw;
        return raw;
    }

    private static bool IsProbablyDeflated(byte[] data)
        => data.Length != TileBytes;

    // ── Tile lookup ────────────────────────────────────────────────────────────

    public byte[]? GetTileOrNull(int tileX, int tileY)
    {
        var key = (tileX, tileY);
        if (_tiles.TryGetValue(key, out var raw))
            return raw;
        if (_compressed.ContainsKey(key))
            return EnsureRaw(key);
        return null;
    }

    // ── Clear ──────────────────────────────────────────────────────────────────

    public void Clear()
    {
        _tiles.Clear();
        _compressed.Clear();
    }

    public void Clear(PixelRegion region)
    {
        if (region.IsEmpty) return;

        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var row = (y - tileY * TileSize) * TileSize * BytesPerPixel + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                Array.Clear(tile, row, tileRegion.Width * BytesPerPixel);
            }
        }, create: false);

        PruneTransparentTiles(region);
    }

    // ── Capture / Restore ──────────────────────────────────────────────────────

    public byte[] Capture(PixelRegion region)
    {
        if (region.IsEmpty) return [];

        var bytes = new byte[region.Width * region.Height * BytesPerPixel];
        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var srcOffset = (y - tileY * TileSize) * TileSize * BytesPerPixel
                              + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                var dstOffset = (y - region.Y) * region.Width * BytesPerPixel
                              + (tileRegion.X - region.X) * BytesPerPixel;
                Buffer.BlockCopy(tile, srcOffset, bytes, dstOffset, tileRegion.Width * BytesPerPixel);
            }
        }, create: false);

        return bytes;
    }

    public Dictionary<(int X, int Y), byte[]?> CaptureTiles(PixelRegion region)
    {
        var result = new Dictionary<(int X, int Y), byte[]?>();
        CaptureTiles(region, result);
        return result;
    }

    public void CaptureTiles(PixelRegion region, Dictionary<(int X, int Y), byte[]?> target)
    {
        if (region.IsEmpty) return;

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                if (target.ContainsKey(key)) continue;

                var raw = EnsureRaw(key);
                if (raw == null)
                {
                    target.Add(key, null);
                    continue;
                }

                var copy = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
                target.Add(key, copy);
            }
        }
    }

    public byte[]? CaptureTile(int tileX, int tileY)
    {
        var key = (tileX, tileY);
        var raw = EnsureRaw(key);
        if (raw == null) return null;

        var copy = new byte[raw.Length];
        Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
        return copy;
    }

    public void Restore(PixelRegion region, byte[] bytes)
    {
        if (region.IsEmpty || bytes.Length == 0) return;

        var expected = region.Width * region.Height * BytesPerPixel;
        if (bytes.Length != expected)
        {
            var actualHeight = bytes.Length / (region.Width * BytesPerPixel);
            if (actualHeight <= 0) return;
            region = new PixelRegion(region.X, region.Y, region.Width, Math.Min(region.Height, actualHeight));
            if (region.IsEmpty) return;
        }

        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var srcOffset = (y - region.Y) * region.Width * BytesPerPixel
                              + (tileRegion.X - region.X) * BytesPerPixel;
                var dstOffset = (y - tileY * TileSize) * TileSize * BytesPerPixel
                              + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                Buffer.BlockCopy(bytes, srcOffset, tile, dstOffset, tileRegion.Width * BytesPerPixel);
            }
        }, create: true);

        PruneTransparentTiles(region);
    }

    public void RestoreTile(int tileX, int tileY, byte[]? bytes)
    {
        var key = (tileX, tileY);
        if (bytes == null || IsTransparent(bytes))
        {
            _tiles.Remove(key);
            _compressed.Remove(key);
            return;
        }

        var raw = EnsureRaw(key);
        if (raw == null || raw.Length != bytes.Length)
        {
            raw = new byte[bytes.Length];
            _tiles[key] = raw;
            _compressed.Remove(key);
            ExtendBounds(tileX * TileSize, tileY * TileSize, (tileX + 1) * TileSize, (tileY + 1) * TileSize);
        }
        Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
    }

    // ── Bulk copy ──────────────────────────────────────────────────────────────

    public void CopyFromBgra(byte[] src, int srcWidth, int srcHeight)
    {
        var copyW = Math.Min(srcWidth, Width);
        var copyH = Math.Min(srcHeight, Height);
        if (copyW <= 0 || copyH <= 0) return;

        var lastTileX = (copyW - 1) / TileSize;
        var lastTileY = (copyH - 1) / TileSize;

        for (var ty = 0; ty <= lastTileY; ty++)
        {
            var tileY = ty * TileSize;
            var tileH = Math.Min(TileSize, copyH - tileY);
            for (var tx = 0; tx <= lastTileX; tx++)
            {
                var tileX = tx * TileSize;
                var tileW = Math.Min(TileSize, copyW - tileX);
                if (!HasAnyAlpha(src, srcWidth, tileX, tileY, tileW, tileH)) continue;

                var tile = new byte[TileBytes];
                for (var y = 0; y < tileH; y++)
                {
                    var srcOffset = ((tileY + y) * srcWidth + tileX) * BytesPerPixel;
                    var dstOffset = y * TileSize * BytesPerPixel;
                    Buffer.BlockCopy(src, srcOffset, tile, dstOffset, tileW * BytesPerPixel);
                }

                _tiles[(tx, ty)] = tile;
                _compressed.Remove((tx, ty));
            }
        }
    }

    public void CopyFromBgra(PixelRegion region, byte[] src, int srcStride)
    {
        if (region.IsEmpty) return;

        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                var srcOffset = y * srcStride + x * BytesPerPixel;
                if (src[srcOffset + 3] == 0) continue;
                WritePixel(region.X + x, region.Y + y, src, srcOffset);
            }
        }

        PruneTransparentTiles(region);
    }

    // ── Skia rendering ─────────────────────────────────────────────────────────

    public unsafe void RenderWithSkia(PixelRegion region, Action<SKCanvas> render)
    {
        if (region.IsEmpty) return;

        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            fixed (byte* tilePtr = tile)
            {
                var info = new SKImageInfo(TileSize, TileSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using var bitmap = new SKBitmap();
                if (!bitmap.InstallPixels(info, (IntPtr)tilePtr, TileSize * BytesPerPixel))
                    return;

                using var canvas = new SKCanvas(bitmap);
                canvas.Translate(-tileX * TileSize, -tileY * TileSize);
                canvas.ClipRect(new SKRect(tileRegion.X, tileRegion.Y, tileRegion.Right, tileRegion.Bottom));
                render(canvas);
                canvas.Flush();
            }
        }, create: true);

        PruneTransparentTiles(region);
    }

    // ── Pixel access ───────────────────────────────────────────────────────────

    public void ReadPixel(int x, int y, byte[] dst, int dstOffset)
    {
        var tileKey = ToTileKey(x, y);
        var tile = EnsureRaw(tileKey);
        if (tile == null)
        {
            dst[dstOffset + 0] = 0;
            dst[dstOffset + 1] = 0;
            dst[dstOffset + 2] = 0;
            dst[dstOffset + 3] = 0;
            return;
        }

        var offset = OffsetInTile(x, y);
        dst[dstOffset + 0] = tile[offset + 0];
        dst[dstOffset + 1] = tile[offset + 1];
        dst[dstOffset + 2] = tile[offset + 2];
        dst[dstOffset + 3] = tile[offset + 3];
    }

    public void WritePixel(int x, int y, byte[] src, int srcOffset)
    {
        var tile = GetOrCreateTile(x, y);
        var offset = OffsetInTile(x, y);
        tile[offset + 0] = src[srcOffset + 0];
        tile[offset + 1] = src[srcOffset + 1];
        tile[offset + 2] = src[srcOffset + 2];
        tile[offset + 3] = src[srcOffset + 3];
    }

    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
    {
        var tile = GetOrCreateTile(x, y);
        var offset = OffsetInTile(x, y);
        tile[offset + 0] = b;
        tile[offset + 1] = g;
        tile[offset + 2] = r;
        tile[offset + 3] = a;
    }

    public void GetPixel(int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        var tile = EnsureRaw(ToTileKey(x, y));
        if (tile == null)
        {
            b = g = r = a = 0;
            return;
        }

        var offset = OffsetInTile(x, y);
        b = tile[offset + 0];
        g = tile[offset + 1];
        r = tile[offset + 2];
        a = tile[offset + 3];
    }

    // ── Queries ────────────────────────────────────────────────────────────────

    public bool HasNonTransparentPixels(PixelRegion region)
    {
        if (region.IsEmpty) return false;

        var found = false;
        ForEachTile(region, (tileX, tileY, tile, tileRegion) =>
        {
            if (found) return;

            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var offset = (y - tileY * TileSize) * TileSize * BytesPerPixel + (tileRegion.X - tileX * TileSize) * BytesPerPixel + 3;
                for (var x = 0; x < tileRegion.Width; x++, offset += BytesPerPixel)
                {
                    if (tile[offset] == 0) continue;
                    found = true;
                    return;
                }
            }
        }, create: false);

        return found;
    }

    public PixelRegion ContentTileBounds
    {
        get
        {
            var total = TileCount;
            if (total == 0) return PixelRegion.Empty;

            var first = true;
            var minX = 0;
            var minY = 0;
            var maxX = 0;
            var maxY = 0;

            void Visit(int tx, int ty)
            {
                var x = tx * TileSize;
                var y = ty * TileSize;
                if (first) { minX = x; minY = y; maxX = x + TileSize; maxY = y + TileSize; first = false; }
                else { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x + TileSize); maxY = Math.Max(maxY, y + TileSize); }
            }

            foreach (var key in _tiles.Keys) Visit(key.X, key.Y);
            foreach (var key in _compressed.Keys) Visit(key.X, key.Y);

            return new PixelRegion(minX, minY, maxX - minX, maxY - minY);
        }
    }

    public PixelRegion ComputeContentBounds()
    {
        var total = TileCount;
        if (total == 0) return PixelRegion.Empty;

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        var found = false;

        void CheckTile(int tx, int ty, byte[] tileData)
        {
            var tileBaseX = tx * TileSize;
            var tileBaseY = ty * TileSize;
            for (var y = 0; y < TileSize; y++)
            {
                var py = tileBaseY + y;
                for (var x = 0; x < TileSize; x++)
                {
                    var offset = (y * TileSize + x) * BytesPerPixel + 3;
                    if (tileData[offset] == 0) continue;
                    var px = tileBaseX + x;
                    if (px < minX) minX = px;
                    if (py < minY) minY = py;
                    if (px > maxX) maxX = px;
                    if (py > maxY) maxY = py;
                    found = true;
                }
            }
        }

        // Decompress on demand for bounds check — this is cheap (called rarely).
        foreach (var (key, tile) in _tiles)
            CheckTile(key.X, key.Y, tile);
        foreach (var (key, compressed) in _compressed)
        {
            var raw = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
            CheckTile(key.X, key.Y, raw);
            _tiles[key] = raw;
            _compressed.Remove(key);
        }

        return found ? new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1) : PixelRegion.Empty;
    }

    public bool HasContentTiles(PixelRegion region)
    {
        if (region.IsEmpty || TileCount == 0) return false;

        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                if (_tiles.ContainsKey(key) || _compressed.ContainsKey(key))
                    return true;
            }
        }

        return false;
    }

    // ── Blend onto flat buffer ─────────────────────────────────────────────────

    public void BlendOnto(byte[] dst, int dstWidth, int dstHeight, double opacity)
    {
        if (opacity <= 0) return;
        int opInt = (int)(opacity * 255 + 0.5);

        foreach (var ((tx, ty), tile) in _tiles)
            BlendTileOnto(tx, ty, tile, dst, dstWidth, dstHeight, opInt);

        foreach (var ((tx, ty), compressed) in _compressed)
        {
            var raw = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
            BlendTileOnto(tx, ty, raw, dst, dstWidth, dstHeight, opInt);
            _tiles[(tx, ty)] = raw;
            _compressed.Remove((tx, ty));
        }
    }

    private static void BlendTileOnto(int tx, int ty, byte[] tile, byte[] dst, int dstWidth, int dstHeight, int opInt)
    {
        int ox = tx * TileSize;
        int oy = ty * TileSize;
        for (int py = 0; py < TileSize; py++)
        {
            int docY = oy + py;
            if ((uint)docY >= (uint)dstHeight) continue;
            int rowBase = docY * dstWidth;
            int srcRow = py * TileSize;
            for (int px = 0; px < TileSize; px++)
            {
                int docX = ox + px;
                if ((uint)docX >= (uint)dstWidth) continue;
                int srcOff = (srcRow + px) * BytesPerPixel;
                int srcA = tile[srcOff + 3] * opInt / 255;
                if (srcA == 0) continue;
                int dstOff = (rowBase + docX) * BytesPerPixel;
                int dstA = dst[dstOff + 3];
                int outA = srcA + dstA * (255 - srcA) / 255;
                if (outA == 0) continue;
                dst[dstOff + 0] = (byte)((tile[srcOff + 0] * srcA + dst[dstOff + 0] * dstA * (255 - srcA) / 255) / outA);
                dst[dstOff + 1] = (byte)((tile[srcOff + 1] * srcA + dst[dstOff + 1] * dstA * (255 - srcA) / 255) / outA);
                dst[dstOff + 2] = (byte)((tile[srcOff + 2] * srcA + dst[dstOff + 2] * dstA * (255 - srcA) / 255) / outA);
                dst[dstOff + 3] = (byte)outA;
            }
        }
    }

    // ── Bulk capture/restore (file I/O, layer snapshots) ───────────────────────

    public Dictionary<(int X, int Y), byte[]> CaptureTiles()
    {
        var total = TileCount;
        var result = new Dictionary<(int X, int Y), byte[]>(total);
        foreach (var (key, tile) in _tiles)
        {
            var copy = new byte[tile.Length];
            Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
            result[key] = copy;
        }
        foreach (var (key, compressed) in _compressed)
        {
            var raw = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
            var copy = new byte[raw.Length];
            Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
            result[key] = copy;
            _tiles[key] = raw;
            _compressed.Remove(key);
        }
        return result;
    }

    public void RestoreTiles(Dictionary<(int X, int Y), byte[]> tiles)
    {
        _tiles.Clear();
        _compressed.Clear();
        foreach (var (key, tile) in tiles)
        {
            var copy = new byte[tile.Length];
            Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
            _tiles[key] = copy;
        }
    }

    // ── Internal tile management ───────────────────────────────────────────────

    private byte[] GetOrCreateTile(int x, int y)
    {
        var key = ToTileKey(x, y);
        var raw = EnsureRaw(key);
        if (raw != null) return raw;

        raw = new byte[TileBytes];
        _tiles.Add(key, raw);
        _compressed.Remove(key);
        ExtendBounds(key.X * TileSize, key.Y * TileSize, key.X * TileSize + TileSize, key.Y * TileSize + TileSize);
        return raw;
    }

    private void ForEachTile(PixelRegion region, Action<int, int, byte[], PixelRegion> action, bool create)
    {
        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                var raw = EnsureRaw(key);
                if (raw == null)
                {
                    if (!create) continue;
                    raw = new byte[TileBytes];
                    _tiles.Add(key, raw);
                    _compressed.Remove(key);
                    ExtendBounds(tx * TileSize, ty * TileSize, (tx + 1) * TileSize, (ty + 1) * TileSize);
                }

                var tileRegion = new PixelRegion(tx * TileSize, ty * TileSize, TileSize, TileSize).Intersect(region);
                if (!tileRegion.IsEmpty) action(tx, ty, raw, tileRegion);
            }
        }
    }

    private void PruneTransparentTiles(PixelRegion region)
    {
        var firstTileX = FloorDiv(region.X, TileSize);
        var firstTileY = FloorDiv(region.Y, TileSize);
        var lastTileX = FloorDiv(region.Right - 1, TileSize);
        var lastTileY = FloorDiv(region.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                if (_tiles.TryGetValue(key, out var raw) && IsTransparent(raw))
                    _tiles.Remove(key);
                else if (_compressed.TryGetValue(key, out var compressed))
                {
                    var decomp = IsProbablyDeflated(compressed) ? Inflate(compressed) : compressed;
                    if (IsTransparent(decomp))
                        _compressed.Remove(key);
                    else
                    {
                        _tiles[key] = decomp;
                        _compressed.Remove(key);
                    }
                }
            }
        }
    }

    // ── Compression primitives ─────────────────────────────────────────────────

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream(raw.Length / 4);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(TileBytes);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static unsafe bool IsTransparent(byte[] tile)
    {
        fixed (byte* p = tile)
        {
            var up = (uint*)p;
            var count = tile.Length / BytesPerPixel;
            for (int i = 0; i < count; i++)
            {
                if ((up[i] & 0xFF000000u) != 0) return false;
            }
        }
        return true;
    }

    private static bool HasAnyAlpha(byte[] src, int srcWidth, int x, int y, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            var offset = ((y + row) * srcWidth + x) * BytesPerPixel + 3;
            for (var col = 0; col < width; col++, offset += BytesPerPixel)
            {
                if (src[offset] != 0) return true;
            }
        }

        return false;
    }

    private static (int X, int Y) ToTileKey(int x, int y)
        => (FloorDiv(x, TileSize), FloorDiv(y, TileSize));

    private static int OffsetInTile(int x, int y)
    {
        var lx = x - FloorDiv(x, TileSize) * TileSize;
        var ly = y - FloorDiv(y, TileSize) * TileSize;
        return (ly * TileSize + lx) * BytesPerPixel;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        if ((value ^ divisor) < 0 && value % divisor != 0) result--;
        return result;
    }
}
