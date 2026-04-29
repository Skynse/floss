using System;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Floss.App.Brushes;

public sealed class ImageBrushTip : IBrushTip, IDisposable
{
    private readonly byte[] _pngBytes;
    private readonly SKBitmap _source;
    private SKBitmap? _cachedMask;
    private int _cachedSize;

    public ImageBrushTip(string pngPath)
        : this(File.ReadAllBytes(pngPath))
    {
    }

    public ImageBrushTip(byte[] pngBytes)
    {
        _pngBytes = pngBytes.ToArray();
        _source = SKBitmap.Decode(_pngBytes) ?? throw new InvalidDataException("Brush tip PNG could not be decoded.");
    }

    public byte[] GetPngBytes() => _pngBytes.ToArray();

    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Max(1, baseSize);
        if (_cachedMask != null && Math.Abs(_cachedSize - size) <= Math.Max(2, size / 10))
            return _cachedMask;

        _cachedMask?.Dispose();
        _cachedSize = size;

        using var scaled = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(scaled))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(_source, SKRect.Create(0, 0, size, size), paint);
        }

        _cachedMask = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var color = scaled.GetPixel(x, y);
                var luminance = (byte)Math.Clamp((int)(color.Red * 0.2126f + color.Green * 0.7152f + color.Blue * 0.0722f), 0, 255);
                var alpha = (byte)(luminance * color.Alpha / 255);
                _cachedMask.SetPixel(x, y, new SKColor(0, 0, 0, alpha));
            }
        }

        return _cachedMask;
    }

    public void Dispose()
    {
        _source.Dispose();
        _cachedMask?.Dispose();
    }
}
