using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Floss.App.Document;

public sealed class DrawingLayer : IDisposable
{
    public const int ThumbnailMaxLongEdge = 44;

    private WriteableBitmap? _thumbnail;
    private int _thumbnailWidth;
    private int _thumbnailHeight;
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
    public Avalonia.Media.Color? LayerColor { get; set; }
    public ExpressionColorMode ExpressionColor { get; set; } = ExpressionColorMode.Color;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool IsGroup { get; set; }
    public bool IsOpen { get; set; } = true;
    public bool IsClipping { get; set; }
    public bool IsAlphaLocked { get; set; }
    public bool IsReference { get; set; }
    public bool IsPaper { get; set; }
    public AdjustmentLayerData? Adjustment { get; set; }
    public int IndentLevel { get; set; }
    public DrawingLayer? Parent { get; set; }
    public List<DrawingLayer> Children { get; } = [];
    public TiledPixelBuffer Pixels { get; internal set; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;
    public int MinX => Pixels.MinX;
    public int MinY => Pixels.MinY;
    public int MaxX => Pixels.MaxX;
    public int MaxY => Pixels.MaxY;
    public PixelRegion DocumentContentBounds
        => IsGroup
            ? Children.Aggregate(PixelRegion.Empty, (bounds, child) => bounds.Union(child.DocumentContentBounds))
            : Pixels.ContentTileBounds.Translate(OffsetX, OffsetY);

    public void Dispose()
    {
        _thumbnail?.Dispose();
        Pixels?.Dispose();
    }

    public static (int Width, int Height) ComputeThumbnailPixelSize(int documentWidth, int documentHeight)
    {
        if (documentWidth <= 0 || documentHeight <= 0)
            return (ThumbnailMaxLongEdge, ThumbnailMaxLongEdge);

        var scale = (double)ThumbnailMaxLongEdge / Math.Max(documentWidth, documentHeight);
        return (
            Math.Max(1, (int)Math.Round(documentWidth * scale)),
            Math.Max(1, (int)Math.Round(documentHeight * scale)));
    }

    public WriteableBitmap GetThumbnail()
    {
        var (tw, th) = ComputeThumbnailPixelSize(Width, Height);
        if (_thumbnail == null || _thumbnailWidth != tw || _thumbnailHeight != th)
        {
            _thumbnail?.Dispose();
            _thumbnailWidth = tw;
            _thumbnailHeight = th;
            _thumbnail = new WriteableBitmap(
                new PixelSize(tw, th),
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
            var dstW = _thumbnail.PixelSize.Width;
            var dstH = _thumbnail.PixelSize.Height;
            const int ts = TiledPixelBuffer.TileSize;
            var offsetX = OffsetX;
            var offsetY = OffsetY;
            var docW = Math.Max(1, Width);
            var docH = Math.Max(1, Height);

            const int checkSize = 4;
            const byte cbDark = 0x88;
            const byte cbLight = 0xBB;

            for (var y = 0; y < dstH; y++)
            {
                var dstRow = (uint*)(dst + y * dstFrame.RowBytes);
                for (var x = 0; x < dstW; x++)
                {
                    var onDark = ((x / checkSize) + (y / checkSize)) % 2 == 0;
                    dstRow[x] = onDark
                        ? (uint)(cbDark | (cbDark << 8) | (cbDark << 16) | (0xFFu << 24))
                        : (uint)(cbLight | (cbLight << 8) | (cbLight << 16) | (0xFFu << 24));
                }
            }

            for (var y = 0; y < dstH; y++)
            {
                var docY = Math.Clamp((int)((y + 0.5) * docH / dstH), 0, docH - 1);
                var localY = docY - offsetY;
                var tilY = FloorDiv(localY, ts);
                var tilLocalY = localY - tilY * ts;
                var dstRow = dst + y * dstFrame.RowBytes;

                int prevTilX = -1;
                int prevTilY = int.MinValue;
                byte[]? tile = null;

                for (var x = 0; x < dstW; x++)
                {
                    var docX = Math.Clamp((int)((x + 0.5) * docW / dstW), 0, docW - 1);
                    var localX = docX - offsetX;
                    var tilX = FloorDiv(localX, ts);
                    var tilLocalX = localX - tilX * ts;
                    var dstPx = dstRow + x * 4;

                    if (tilX != prevTilX || tilY != prevTilY)
                    {
                        tile = Pixels.GetTileOrNull(tilX, tilY);
                        prevTilX = tilX;
                        prevTilY = tilY;
                    }

                    if (tile == null || tilLocalX is < 0 or >= ts || tilLocalY is < 0 or >= ts)
                        continue;

                    var off = (tilLocalY * ts + tilLocalX) * 4;
                    var b = tile[off];
                    var g = tile[off + 1];
                    var r = tile[off + 2];
                    var a = tile[off + 3];
                    // Get the checkerboard pixel at this position
                    var (cbR, cbG, cbB) = CheckerboardBg(x, y, checkSize, cbDark, cbLight);
                    *(uint*)dstPx = BlendThumbnailPixelOnCheckers(b, g, r, a, cbR, cbG, cbB);
                }
            }
        }

        _thumbnailDirty = false;
    }

    private static (byte r, byte g, byte b) CheckerboardBg(int x, int y, int checkSize, byte dark, byte light)
    {
        var onDark = ((x / checkSize) + (y / checkSize)) % 2 == 0;
        var v = onDark ? dark : light;
        return (v, v, v);
    }

    private static uint BlendThumbnailPixelOnCheckers(byte b, byte g, byte r, byte a, byte cbR, byte cbG, byte cbB)
    {
        if (a == 0) return (uint)(cbB | (cbG << 8) | (cbR << 16) | (255u << 24));
        if (a == 255) return (uint)(b | (g << 8) | (r << 16) | (255u << 24));
        var fa = a / 255.0;
        var inv = 1.0 - fa;
        var ob = (byte)(b * fa + cbB * inv);
        var og = (byte)(g * fa + cbG * inv);
        var or = (byte)(r * fa + cbR * inv);
        return (uint)(ob | (og << 8) | (or << 16) | (255u << 24));
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

    public Dictionary<(int X, int Y), byte[]?> CaptureTiles(PixelRegion region) => Pixels.CaptureTiles(region);

    public void CaptureTiles(PixelRegion region, Dictionary<(int X, int Y), byte[]?> target)
        => Pixels.CaptureTiles(region, target);

    public byte[]? CaptureTile(int tileX, int tileY) => Pixels.CaptureTile(tileX, tileY);

    public void RestorePixels(byte[] bytes)
        => RestorePixels(Pixels.Bounds, bytes);

    public void RestorePixels(PixelRegion region, byte[] bytes)
    {
        Pixels.Restore(region, bytes);
        MarkThumbnailDirty();
    }

    public void FillSolid(PixelRegion region, Avalonia.Media.Color color)
    {
        Pixels.FillSolid(region, color.B, color.G, color.R, color.A);
        MarkThumbnailDirty();
    }

    public void RestoreTile(int tileX, int tileY, byte[]? bytes)
    {
        Pixels.RestoreTile(tileX, tileY, bytes);
        MarkThumbnailDirty();
    }

    public Dictionary<(int X, int Y), byte[]> CaptureTiles() => Pixels.CaptureTiles();

    public void RestoreTiles(Dictionary<(int X, int Y), byte[]> tiles)
    {
        Pixels.RestoreTiles(tiles);
        MarkThumbnailDirty();
    }

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        if ((value ^ divisor) < 0 && value % divisor != 0) result--;
        return result;
    }

}
