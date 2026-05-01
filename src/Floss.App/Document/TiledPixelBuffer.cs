using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Floss.App.Document;

public sealed class TiledPixelBuffer
{
    public const int TileSize = 64;
    private const int BytesPerPixel = 4;

    private readonly Dictionary<(int X, int Y), byte[]> _tiles = [];

    public TiledPixelBuffer(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public int Width { get; }
    public int Height { get; }

    public PixelRegion Bounds => new(0, 0, Width, Height);

    public PixelRegion ContentTileBounds
    {
        get
        {
            if (_tiles.Count == 0) return PixelRegion.Empty;

            var first = true;
            var minX = 0;
            var minY = 0;
            var maxX = 0;
            var maxY = 0;

            foreach (var key in _tiles.Keys)
            {
                var x = key.X * TileSize;
                var y = key.Y * TileSize;
                if (first)
                {
                    minX = x;
                    minY = y;
                    maxX = x + TileSize;
                    maxY = y + TileSize;
                    first = false;
                }
                else
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x + TileSize);
                    maxY = Math.Max(maxY, y + TileSize);
                }
            }

            return new PixelRegion(minX, minY, maxX - minX, maxY - minY).ClipTo(Width, Height);
        }
    }

    public void Clear()
    {
        _tiles.Clear();
    }

    public void Clear(PixelRegion region)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty) return;

        ForEachTile(clipped, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var row = (y - tileY * TileSize) * TileSize * BytesPerPixel + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                Array.Clear(tile, row, tileRegion.Width * BytesPerPixel);
            }
        }, create: false);

        PruneTransparentTiles(clipped);
    }

    public byte[] Capture(PixelRegion region)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty) return [];

        var bytes = new byte[clipped.Width * clipped.Height * BytesPerPixel];
        ForEachTile(clipped, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var srcOffset = (y - tileY * TileSize) * TileSize * BytesPerPixel
                              + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                var dstOffset = (y - clipped.Y) * clipped.Width * BytesPerPixel
                              + (tileRegion.X - clipped.X) * BytesPerPixel;
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
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty) return;

        var firstTileX = FloorDiv(clipped.X, TileSize);
        var firstTileY = FloorDiv(clipped.Y, TileSize);
        var lastTileX = FloorDiv(clipped.Right - 1, TileSize);
        var lastTileY = FloorDiv(clipped.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var key = (tx, ty);
                if (target.ContainsKey(key)) continue;
                target.Add(key, CaptureTile(tx, ty));
            }
        }
    }

    public byte[]? CaptureTile(int tileX, int tileY)
    {
        if (!_tiles.TryGetValue((tileX, tileY), out var tile)) return null;

        var copy = new byte[tile.Length];
        Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
        return copy;
    }

    public void Restore(PixelRegion region, byte[] bytes)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty || bytes.Length == 0) return;

        ForEachTile(clipped, (tileX, tileY, tile, tileRegion) =>
        {
            for (var y = tileRegion.Y; y < tileRegion.Bottom; y++)
            {
                var srcOffset = (y - clipped.Y) * clipped.Width * BytesPerPixel
                              + (tileRegion.X - clipped.X) * BytesPerPixel;
                var dstOffset = (y - tileY * TileSize) * TileSize * BytesPerPixel
                              + (tileRegion.X - tileX * TileSize) * BytesPerPixel;
                Buffer.BlockCopy(bytes, srcOffset, tile, dstOffset, tileRegion.Width * BytesPerPixel);
            }
        }, create: true);

        PruneTransparentTiles(clipped);
    }

    public void RestoreTile(int tileX, int tileY, byte[]? bytes)
    {
        var key = (tileX, tileY);
        if (bytes == null || IsTransparent(bytes))
        {
            _tiles.Remove(key);
            return;
        }

        // Reuse the existing tile array if present — avoids 16 KB allocation per undo tile.
        if (!_tiles.TryGetValue(key, out var tile) || tile.Length != bytes.Length)
        {
            tile = new byte[bytes.Length];
            _tiles[key] = tile;
        }
        Buffer.BlockCopy(bytes, 0, tile, 0, bytes.Length);
    }

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

                var tile = new byte[TileSize * TileSize * BytesPerPixel];
                for (var y = 0; y < tileH; y++)
                {
                    var srcOffset = ((tileY + y) * srcWidth + tileX) * BytesPerPixel;
                    var dstOffset = y * TileSize * BytesPerPixel;
                    Buffer.BlockCopy(src, srcOffset, tile, dstOffset, tileW * BytesPerPixel);
                }

                _tiles[(tx, ty)] = tile;
            }
        }
    }

    public void CopyFromBgra(PixelRegion region, byte[] src, int srcStride)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty) return;

        for (var y = 0; y < clipped.Height; y++)
        {
            for (var x = 0; x < clipped.Width; x++)
            {
                var srcOffset = y * srcStride + x * BytesPerPixel;
                if (src[srcOffset + 3] == 0) continue;
                WritePixel(clipped.X + x, clipped.Y + y, src, srcOffset);
            }
        }

        PruneTransparentTiles(clipped);
    }

    public unsafe void RenderWithSkia(PixelRegion region, Action<SKCanvas> render)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty) return;

        ForEachTile(clipped, (tileX, tileY, tile, tileRegion) =>
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

        PruneTransparentTiles(clipped);
    }

    public void ReadPixel(int x, int y, byte[] dst, int dstOffset)
    {
        if ((uint)x >= Width || (uint)y >= Height)
        {
            dst[dstOffset + 0] = 0;
            dst[dstOffset + 1] = 0;
            dst[dstOffset + 2] = 0;
            dst[dstOffset + 3] = 0;
            return;
        }

        var tileKey = ToTileKey(x, y);
        if (!_tiles.TryGetValue(tileKey, out var tile))
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
        if ((uint)x >= Width || (uint)y >= Height) return;

        var tile = GetOrCreateTile(x, y);
        var offset = OffsetInTile(x, y);
        tile[offset + 0] = src[srcOffset + 0];
        tile[offset + 1] = src[srcOffset + 1];
        tile[offset + 2] = src[srcOffset + 2];
        tile[offset + 3] = src[srcOffset + 3];
    }

    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;

        var tile = GetOrCreateTile(x, y);
        var offset = OffsetInTile(x, y);
        tile[offset + 0] = b;
        tile[offset + 1] = g;
        tile[offset + 2] = r;
        tile[offset + 3] = a;
    }

    public void GetPixel(int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        if ((uint)x >= Width || (uint)y >= Height || !_tiles.TryGetValue(ToTileKey(x, y), out var tile))
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

    public bool HasNonTransparentPixels(PixelRegion region)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty) return false;

        var found = false;
        ForEachTile(clipped, (tileX, tileY, tile, tileRegion) =>
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

    public PixelRegion ComputeContentBounds()
    {
        if (_tiles.Count == 0) return PixelRegion.Empty;

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        var found = false;

        foreach (var (key, tile) in _tiles)
        {
            var tileBaseX = key.X * TileSize;
            var tileBaseY = key.Y * TileSize;

            for (var y = 0; y < TileSize; y++)
            {
                var py = tileBaseY + y;
                if ((uint)py >= (uint)Height) continue;

                for (var x = 0; x < TileSize; x++)
                {
                    var px = tileBaseX + x;
                    if ((uint)px >= (uint)Width) continue;

                    var offset = (y * TileSize + x) * BytesPerPixel + 3;
                    if (tile[offset] == 0) continue;

                    if (px < minX) minX = px;
                    if (py < minY) minY = py;
                    if (px > maxX) maxX = px;
                    if (py > maxY) maxY = py;
                    found = true;
                }
            }
        }

        return found ? new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1) : PixelRegion.Empty;
    }

    public bool HasContentTiles(PixelRegion region)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty || _tiles.Count == 0) return false;

        var firstTileX = FloorDiv(clipped.X, TileSize);
        var firstTileY = FloorDiv(clipped.Y, TileSize);
        var lastTileX = FloorDiv(clipped.Right - 1, TileSize);
        var lastTileY = FloorDiv(clipped.Bottom - 1, TileSize);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                if (_tiles.ContainsKey((tx, ty))) return true;
            }
        }

        return false;
    }

    public byte[]? GetTileOrNull(int tileX, int tileY)
        => _tiles.TryGetValue((tileX, tileY), out var tile) ? tile : null;

    public Dictionary<(int X, int Y), byte[]> CaptureTiles()
    {
        var result = new Dictionary<(int X, int Y), byte[]>(_tiles.Count);
        foreach (var (key, tile) in _tiles)
        {
            var copy = new byte[tile.Length];
            Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
            result[key] = copy;
        }
        return result;
    }

    public void RestoreTiles(Dictionary<(int X, int Y), byte[]> tiles)
    {
        _tiles.Clear();
        foreach (var (key, tile) in tiles)
        {
            var copy = new byte[tile.Length];
            Buffer.BlockCopy(tile, 0, copy, 0, tile.Length);
            _tiles[key] = copy;
        }
    }

    private byte[] GetOrCreateTile(int x, int y)
    {
        var key = ToTileKey(x, y);
        if (!_tiles.TryGetValue(key, out var tile))
        {
            tile = new byte[TileSize * TileSize * BytesPerPixel];
            _tiles.Add(key, tile);
        }

        return tile;
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
                if (!_tiles.TryGetValue(key, out var tile))
                {
                    if (!create) continue;
                    tile = new byte[TileSize * TileSize * BytesPerPixel];
                    _tiles.Add(key, tile);
                }

                var tileRegion = new PixelRegion(tx * TileSize, ty * TileSize, TileSize, TileSize).Intersect(region);
                if (!tileRegion.IsEmpty) action(tx, ty, tile, tileRegion);
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
                if (_tiles.TryGetValue(key, out var tile) && IsTransparent(tile))
                {
                    _tiles.Remove(key);
                }
            }
        }
    }

    private static unsafe bool IsTransparent(byte[] tile)
    {
        // Read as uint (BGRA little-endian) — alpha is the top byte.
        // Checks 4 bytes at a time instead of every 4th byte individually.
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
