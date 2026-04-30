using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Floss.App.Document;

public sealed class DrawingLayer : IDisposable
{
    private WriteableBitmap? _thumbnail;
    private int _thumbnailSize;
    private bool _thumbnailDirty = true;

    public DrawingLayer(string name, int width, int height)
    {
        Name = name;
        Pixels = new TiledPixelBuffer(width, height);
    }

    public string Name { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }
    public double Opacity { get; set; } = 1.0;
    public string BlendMode { get; set; } = "Normal";
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool IsGroup { get; set; }
    public bool IsOpen { get; set; } = true;
    public bool IsClipping { get; set; }
    public int IndentLevel { get; set; }
    public DrawingLayer? Parent { get; set; }
    public List<DrawingLayer> Children { get; } = [];
    public TiledPixelBuffer Pixels { get; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;
    public PixelRegion DocumentContentBounds
        => IsGroup
            ? Children.Aggregate(PixelRegion.Empty, (bounds, child) => bounds.Union(child.DocumentContentBounds))
            : Pixels.ContentTileBounds.Translate(OffsetX, OffsetY);

    public void Dispose()
    {
        _thumbnail?.Dispose();
    }

    public WriteableBitmap GetThumbnail(int size)
    {
        if (_thumbnail == null || _thumbnailSize != size)
        {
            _thumbnail?.Dispose();
            _thumbnailSize = size;
            _thumbnail = new WriteableBitmap(
                new PixelSize(size, size),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
            _thumbnailDirty = true;
        }

        if (_thumbnailDirty)
        {
            RefreshThumbnail();
        }

        return _thumbnail;
    }

    public void MarkThumbnailDirty() => _thumbnailDirty = true;

    public void RefreshThumbnail()
    {
        if (_thumbnail == null) return;

        using var dstFrame = _thumbnail.Lock();
        unsafe
        {
            var dst  = (byte*)dstFrame.Address;
            var srcW = Width;
            var srcH = Height;
            var dstW = _thumbnail.PixelSize.Width;
            var dstH = _thumbnail.PixelSize.Height;
            const int ts = TiledPixelBuffer.TileSize;

            for (var y = 0; y < dstH; y++)
            {
                var srcY       = Math.Clamp((int)((y + 0.5) * srcH / dstH), 0, srcH - 1);
                var tilY       = srcY / ts;
                var tilLocalY  = srcY - tilY * ts;
                var dstRow     = dst + y * dstFrame.RowBytes;

                int     prevTilX = -1;
                byte[]? tile     = null;

                for (var x = 0; x < dstW; x++)
                {
                    var srcX      = Math.Clamp((int)((x + 0.5) * srcW / dstW), 0, srcW - 1);
                    var tilX      = srcX / ts;
                    var tilLocalX = srcX - tilX * ts;
                    var dstPx     = dstRow + x * 4;

                    if (tilX != prevTilX)
                    {
                        tile     = Pixels.GetTileOrNull(tilX, tilY);
                        prevTilX = tilX;
                    }

                    if (tile == null)
                    {
                        *(uint*)dstPx = 0;
                    }
                    else
                    {
                        var offset = (tilLocalY * ts + tilLocalX) * 4;
                        *(uint*)dstPx = *(uint*)(System.Runtime.CompilerServices.Unsafe.AsPointer(ref tile[offset]));
                    }
                }
            }
        }

        _thumbnailDirty = false;
    }

    public void Clear()
    {
        Pixels.Clear();
        MarkThumbnailDirty();
    }

    public void Clear(PixelRegion region)
    {
        Pixels.Clear(region);
        MarkThumbnailDirty();
    }

    public byte[] CapturePixels()
        => CapturePixels(Pixels.Bounds);

    public byte[] CapturePixels(PixelRegion region) => Pixels.Capture(region);

    public void RestorePixels(byte[] bytes)
        => RestorePixels(Pixels.Bounds, bytes);

    public void RestorePixels(PixelRegion region, byte[] bytes)
    {
        Pixels.Restore(region, bytes);
        MarkThumbnailDirty();
    }

    public Dictionary<(int X, int Y), byte[]> CaptureTiles() => Pixels.CaptureTiles();

    public void RestoreTiles(Dictionary<(int X, int Y), byte[]> tiles)
    {
        Pixels.RestoreTiles(tiles);
        MarkThumbnailDirty();
    }
}
