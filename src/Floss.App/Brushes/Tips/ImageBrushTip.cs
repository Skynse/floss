using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SkiaSharp;

namespace Floss.App.Brushes.Tips;

public sealed class ImageBrushTip : IBrushTip, IDisposable
{
    private const int ColorDetectThreshold = 3;
    private const int MaxSourceDimension = 1024;
    private const int MaxGeneratedSize = 1024;

    private static readonly object SharedLock = new();
    private static readonly Dictionary<ulong, SharedSource> SharedSources = new();

    private readonly byte[] _pngBytes;
    private readonly ulong _pngKey;
    private readonly SharedSource _shared;
    private readonly object _cacheLock = new();
    private readonly Dictionary<(int Size, int Hardness), SKBitmap> _maskCache = [];
    private readonly Dictionary<int, SKBitmap> _colorStampCache = [];

    private sealed class SharedSource
    {
        public required SKBitmap Source { get; init; }
        public required bool HasColor { get; init; }
        public required bool SourceHasUsefulAlpha { get; init; }
    }

    public ImageBrushTip(string pngPath)
        : this(File.ReadAllBytes(pngPath))
    {
    }

    public ImageBrushTip(byte[] pngBytes)
    {
        _pngBytes = pngBytes;
        _pngKey = PngKey(pngBytes);
        _shared = AcquireSharedSource(_pngBytes, _pngKey);
    }

    public byte[] GetPngBytes()
    {
        var copy = new byte[_pngBytes.Length];
        _pngBytes.AsSpan().CopyTo(copy);
        return copy;
    }

    public bool HasColor => _shared.HasColor;

    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Clamp(baseSize, 1, MaxGeneratedSize);
        var clampedHardness = Math.Clamp(hardness, 0.001f, 1.0f);
        var key = (size, QuantizeHardness(clampedHardness));

        lock (_cacheLock)
        {
            if (_maskCache.TryGetValue(key, out var cached))
                return cached;
        }

        var mask = BuildMask(size, clampedHardness);

        lock (_cacheLock)
        {
            if (_maskCache.TryGetValue(key, out var existing))
            {
                mask.Dispose();
                return existing;
            }
            _maskCache[key] = mask;
            return mask;
        }
    }

    private SKBitmap BuildMask(int size, float clampedHardness)
    {
        var source = _shared.Source;
        var sourceHasUsefulAlpha = _shared.SourceHasUsefulAlpha;

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
            var scale = Math.Min(size / (float)source.Width, size / (float)source.Height);
            var dstW = source.Width * scale;
            var dstH = source.Height * scale;
            var dst = SKRect.Create((size - dstW) * 0.5f, (size - dstH) * 0.5f, dstW, dstH);
            canvas.DrawBitmap(source, dst, paint);
        }

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
                    if (sourceHasUsefulAlpha)
                        alpha = a;
                    else
                        alpha = (byte)Math.Clamp((int)(r * 0.2126f + g * 0.7152f + b * 0.0722f), 0, 255);

                    dstRow[x] = alpha;
                }
            }
        }

        return mask;
    }

    public SKBitmap GenerateColorStamp(int baseSize)
    {
        if (!_shared.HasColor)
            return GenerateMask(baseSize, 1.0f);

        var size = Math.Clamp(baseSize, 1, MaxGeneratedSize);
        lock (_cacheLock)
        {
            if (_colorStampCache.TryGetValue(size, out var cached))
                return cached;
        }

        var source = _shared.Source;
        var stamp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(stamp))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(size / (float)source.Width, size / (float)source.Height);
            var dstW = source.Width * scale;
            var dstH = source.Height * scale;
            var dst = SKRect.Create((size - dstW) * 0.5f, (size - dstH) * 0.5f, dstW, dstH);
            canvas.DrawBitmap(source, dst, paint);
        }

        lock (_cacheLock)
        {
            if (_colorStampCache.TryGetValue(size, out var existing))
            {
                stamp.Dispose();
                return existing;
            }
            _colorStampCache[size] = stamp;
            return stamp;
        }
    }

    public unsafe float SampleMaskAlpha(float u, float v, int baseSize, float hardness)
    {
        var mask = GenerateMask(baseSize, hardness);
        var size = mask.Width;
        var mx = u * size - 0.5f;
        var my = v * size - 0.5f;
        if (mx < 0f || my < 0f || mx >= size - 1f || my >= size - 1f)
            return 0f;

        var maskPx = (byte*)mask.GetPixels().ToPointer();
        var maskStride = mask.RowBytes;
        var ix0 = (int)mx;
        var iy0 = (int)my;
        var ix1 = Math.Min(ix0 + 1, size - 1);
        var iy1 = Math.Min(iy0 + 1, size - 1);
        var fx = mx - ix0;
        var fy = my - iy0;
        var a00 = maskPx[iy0 * maskStride + ix0];
        var a10 = maskPx[iy0 * maskStride + ix1];
        var a01 = maskPx[iy1 * maskStride + ix0];
        var a11 = maskPx[iy1 * maskStride + ix1];
        return (a00 + (a10 - a00) * fx + (a01 - a00) * fy + (a00 - a10 - a01 + a11) * fx * fy) / 255f;
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            foreach (var mask in _maskCache.Values)
                mask.Dispose();
            _maskCache.Clear();
            foreach (var stamp in _colorStampCache.Values)
                stamp.Dispose();
            _colorStampCache.Clear();
        }
    }

    private static SharedSource AcquireSharedSource(byte[] pngBytes, ulong pngKey)
    {
        lock (SharedLock)
        {
            if (SharedSources.TryGetValue(pngKey, out var existing))
                return existing;

            var source = DecodeSource(pngBytes);
            var entry = new SharedSource
            {
                Source = source,
                SourceHasUsefulAlpha = DetectUsefulAlpha(source),
                HasColor = DetectColor(source)
            };
            SharedSources[pngKey] = entry;
            return entry;
        }
    }

    internal static ulong PngKey(byte[] pngBytes)
    {
        if (pngBytes.Length == 0)
            return 0;
        var hash = SHA256.HashData(pngBytes);
        return BitConverter.ToUInt64(hash, 0) ^ BitConverter.ToUInt64(hash, 8);
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
