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
        for (var y = 0; y < clipped.Height; y++)
        {
            for (var x = 0; x < clipped.Width; x++)
            {
                ReadPixel(clipped.X + x, clipped.Y + y, bytes, (y * clipped.Width + x) * BytesPerPixel);
            }
        }

        return bytes;
    }

    public void Restore(PixelRegion region, byte[] bytes)
    {
        var clipped = region.ClipTo(Width, Height);
        if (clipped.IsEmpty || bytes.Length == 0) return;

        for (var y = 0; y < clipped.Height; y++)
        {
            for (var x = 0; x < clipped.Width; x++)
            {
                WritePixel(clipped.X + x, clipped.Y + y, bytes, (y * clipped.Width + x) * BytesPerPixel);
            }
        }

        PruneTransparentTiles(clipped);
    }

    public void CopyFromBgra(byte[] src, int srcWidth, int srcHeight)
    {
        var copyW = Math.Min(srcWidth, Width);
        var copyH = Math.Min(srcHeight, Height);
        for (var y = 0; y < copyH; y++)
        {
            for (var x = 0; x < copyW; x++)
            {
                WritePixel(x, y, src, (y * srcWidth + x) * BytesPerPixel);
            }
        }
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

    private static bool IsTransparent(byte[] tile)
    {
        for (var i = 3; i < tile.Length; i += BytesPerPixel)
        {
            if (tile[i] != 0) return false;
        }

        return true;
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
