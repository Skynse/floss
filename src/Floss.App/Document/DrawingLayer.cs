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
            var dst = (byte*)dstFrame.Address;
            var srcW = Width;
            var srcH = Height;
            var dstW = _thumbnail.PixelSize.Width;
            var dstH = _thumbnail.PixelSize.Height;

            for (var y = 0; y < dstH; y++)
            {
                var srcY = Math.Clamp((int)((y + 0.5) * srcH / dstH), 0, srcH - 1);
                var dstRow = dst + y * dstFrame.RowBytes;

                for (var x = 0; x < dstW; x++)
                {
                    var srcX = Math.Clamp((int)((x + 0.5) * srcW / dstW), 0, srcW - 1);
                    var dstPx = dstRow + x * 4;
                    Pixels.GetPixel(srcX, srcY, out var b, out var g, out var r, out var a);
                    dstPx[0] = b;
                    dstPx[1] = g;
                    dstPx[2] = r;
                    dstPx[3] = a;
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
}
