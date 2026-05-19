using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Floss.App.Brushes;

public sealed class ImageBrushTip : IBrushTip, IDisposable
{
    private const int ColorDetectThreshold = 3;
    private const int MaxSourceDimension = 1024;
    private const int MaxGeneratedSize = 1024;

    private readonly byte[] _pngBytes;
    private readonly SKBitmap _source;
    private readonly bool _hasColor;
    private readonly bool _sourceHasUsefulAlpha;
    private readonly Dictionary<(int Size, int Hardness), SKBitmap> _maskCache = [];
    private readonly Dictionary<int, SKBitmap> _colorStampCache = [];

    public ImageBrushTip(string pngPath)
        : this(File.ReadAllBytes(pngPath))
    {
    }

    public ImageBrushTip(byte[] pngBytes)
    {
        _pngBytes = pngBytes.ToArray();
        _source = DecodeSource(_pngBytes);
        _sourceHasUsefulAlpha = DetectUsefulAlpha(_source);
        _hasColor = DetectColor(_source);
    }

    public byte[] GetPngBytes() => _pngBytes.ToArray();

    public bool HasColor => _hasColor;

    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Clamp(baseSize, 1, MaxGeneratedSize);
        var clampedHardness = Math.Clamp(hardness, 0.001f, 1.0f);
        var key = (size, QuantizeHardness(clampedHardness));

        if (_maskCache.TryGetValue(key, out var cached))
            return cached;

        // Step 1: scale source to target size with optional blur for hardness.
        using var scaled = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(scaled))
        using (var paint = new SKPaint { IsAntialias = true })
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
                        alpha = (byte)Math.Clamp((int)(r * 0.2126f + g * 0.7152f + b * 0.0722f), 0, 255);
                    }

                    dstRow[x] = alpha;
                }
            }
        }

        _maskCache[key] = mask;
        return mask;
    }

    /// <summary>
    /// Returns the tip image scaled to the given size, preserving aspect ratio,
    /// as an RGBA bitmap. Used for color-stamp rendering when <see cref="HasColor"/> is true.
    /// </summary>
    public SKBitmap GenerateColorStamp(int baseSize)
    {
        if (!_hasColor)
            return GenerateMask(baseSize, 1.0f);

        var size = Math.Clamp(baseSize, 1, MaxGeneratedSize);
        if (_colorStampCache.TryGetValue(size, out var cached))
            return cached;

        var stamp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(stamp))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(size / (float)_source.Width, size / (float)_source.Height);
            var dstW = _source.Width * scale;
            var dstH = _source.Height * scale;
            var dst = SKRect.Create((size - dstW) * 0.5f, (size - dstH) * 0.5f, dstW, dstH);
            canvas.DrawBitmap(_source, dst, paint);
        }

        _colorStampCache[size] = stamp;
        return stamp;
    }

    public void Dispose()
    {
        _source.Dispose();
        foreach (var mask in _maskCache.Values)
            mask.Dispose();
        _maskCache.Clear();
        foreach (var stamp in _colorStampCache.Values)
            stamp.Dispose();
        _colorStampCache.Clear();
    }

    private static unsafe bool DetectColor(SKBitmap bitmap)
    {
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        var rowBytes = bitmap.RowBytes;
        var w = bitmap.Width;
        var h = bitmap.Height;

        for (var y = 0; y < h; y++)
        {
            var row = ptr + y * rowBytes;
            for (var x = 0; x < w; x++)
            {
                var b = row[x * 4 + 0];
                var g = row[x * 4 + 1];
                var r = row[x * 4 + 2];
                if (Math.Abs(r - g) > ColorDetectThreshold ||
                    Math.Abs(g - b) > ColorDetectThreshold ||
                    Math.Abs(b - r) > ColorDetectThreshold)
                    return true;
            }
        }
        return false;
    }

    private static int QuantizeHardness(float hardness)
        => Math.Clamp((int)MathF.Round(Math.Clamp(hardness, 0.001f, 1f) * 255f), 0, 255);

    private static SKBitmap DecodeSource(byte[] pngBytes)
    {
        var decoded = SKBitmap.Decode(pngBytes) ?? throw new InvalidDataException("Brush tip PNG could not be decoded.");
        var maxDim = Math.Max(decoded.Width, decoded.Height);
        if (maxDim <= MaxSourceDimension)
            return decoded;

        var scale = MaxSourceDimension / (float)maxDim;
        var targetW = Math.Max(1, (int)MathF.Round(decoded.Width * scale));
        var targetH = Math.Max(1, (int)MathF.Round(decoded.Height * scale));
        var resized = new SKBitmap(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!decoded.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
        {
            resized.Dispose();
            decoded.Dispose();
            throw new InvalidDataException("Brush tip PNG could not be resized.");
        }

        decoded.Dispose();
        return resized;
    }

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
