using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Floss.App.Brushes;

public sealed class ImageBrushTip : IBrushTip, IDisposable
{
    private readonly byte[] _pngBytes;
    private readonly SKBitmap _source;
    private readonly bool _sourceHasUsefulAlpha;
    private readonly Dictionary<(int Size, int Hardness), SKBitmap> _maskCache = [];

    public ImageBrushTip(string pngPath)
        : this(File.ReadAllBytes(pngPath))
    {
    }

    public ImageBrushTip(byte[] pngBytes)
    {
        _pngBytes = pngBytes.ToArray();
        _source = SKBitmap.Decode(_pngBytes) ?? throw new InvalidDataException("Brush tip PNG could not be decoded.");
        _sourceHasUsefulAlpha = DetectUsefulAlpha(_source);
    }

    public byte[] GetPngBytes() => _pngBytes.ToArray();

    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Max(1, baseSize);
        var clampedHardness = Math.Clamp(hardness, 0.001f, 1.0f);
        var key = (size, QuantizeHardness(clampedHardness));

        if (_maskCache.TryGetValue(key, out var cached))
            return cached;

        // Step 1: scale source to target size with optional blur for hardness.
        // Preserve the source aspect ratio; sampled brush tips are often not square.
        using var scaled = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(scaled))
        using (var paint = new SKPaint
        {
            IsAntialias = true,
#pragma warning disable CS0618 // FilterQuality is obsolete but still works in this SkiaSharp version
            FilterQuality = SKFilterQuality.High
#pragma warning restore CS0618
        })
        {
            if (clampedHardness < 0.999f)
            {
                var sigma = (1.0f - clampedHardness) * size * 0.22f;
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
            }

            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(size / (float)_source.Width, size / (float)_source.Height);
            var dstW = _source.Width * scale;
            var dstH = _source.Height * scale;
            var dst = SKRect.Create((size - dstW) * 0.5f, (size - dstH) * 0.5f, dstW, dstH);
            canvas.DrawBitmap(_source, dst, paint);
        }

        // Step 2: extract alpha into Alpha8
        var mask = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));

        unsafe
        {
            var srcPtr = (byte*)scaled.GetPixels().ToPointer();
            var dstPtr = (byte*)mask.GetPixels().ToPointer();
            var srcRowBytes = scaled.RowBytes;
            var dstRowBytes = mask.RowBytes;

            for (var y = 0; y < size; y++)
            {
                var srcRow = srcPtr + y * srcRowBytes;
                var dstRow = dstPtr + y * dstRowBytes;

                for (var x = 0; x < size; x++)
                {
                    // BGRA8888: B=0, G=1, R=2, A=3
                    var b = srcRow[x * 4 + 0];
                    var g = srcRow[x * 4 + 1];
                    var r = srcRow[x * 4 + 2];
                    var a = srcRow[x * 4 + 3];

                    byte alpha;
                    if (_sourceHasUsefulAlpha)
                    {
                        alpha = a;
                    }
                    else
                    {
                        // No meaningful alpha: use luminance
                        alpha = (byte)Math.Clamp((int)(r * 0.2126f + g * 0.7152f + b * 0.0722f), 0, 255);
                    }

                    dstRow[x] = alpha;
                }
            }
        }

        _maskCache[key] = mask;
        return mask;
    }

    public void Dispose()
    {
        _source.Dispose();
        foreach (var mask in _maskCache.Values)
            mask.Dispose();
        _maskCache.Clear();
    }

    private static int QuantizeHardness(float hardness)
        => Math.Clamp((int)MathF.Round(Math.Clamp(hardness, 0.001f, 1f) * 255f), 0, 255);

    private static unsafe bool DetectUsefulAlpha(SKBitmap bitmap)
    {
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        var rowBytes = bitmap.RowBytes;
        var w = bitmap.Width;
        var h = bitmap.Height;
        byte min = 255;
        byte max = 0;

        for (var y = 0; y < h; y++)
        {
            var row = ptr + y * rowBytes;
            for (var x = 0; x < w; x++)
            {
                var alpha = row[x * 4 + 3];
                min = Math.Min(min, alpha);
                max = Math.Max(max, alpha);
            }
        }

        return min < 250 && max - min > 4;
    }
}
