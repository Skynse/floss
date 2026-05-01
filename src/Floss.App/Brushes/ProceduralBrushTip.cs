using System;
using SkiaSharp;

namespace Floss.App.Brushes;

public enum BrushTipShape
{
    Circle,
    SoftRound,
    Flat,
    Ellipse,
    Rectangle,
    Chalk,
    Bristle,
    Scatter,
}

public sealed class ProceduralBrushTip(BrushTipShape shape = BrushTipShape.Circle, float aspectRatio = 1.0f) : IBrushTip
{
    private SKBitmap? _cachedMask;
    private int _cachedSize;
    private float _cachedHardness;

    public BrushTipShape Shape { get; } = shape;
    public float AspectRatio { get; } = aspectRatio;

    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Max(1, baseSize);
        var h = Math.Clamp(hardness, 0.001f, 1.0f);
        if (_cachedMask != null && _cachedSize == size && MathF.Abs(_cachedHardness - h) < 0.0001f)
            return _cachedMask;

        _cachedMask?.Dispose();
        _cachedSize = size;
        _cachedHardness = h;
        _cachedMask = Shape switch
        {
            BrushTipShape.Chalk   => GenerateChalk(size, h),
            BrushTipShape.Bristle => GenerateBristle(size, h),
            BrushTipShape.Scatter => GenerateScatter(size, h),
            _                     => GenerateSmooth(size, h),
        };
        return _cachedMask;
    }

    private SKBitmap GenerateSmooth(int size, float hardness)
    {
        var bmp = NewAlpha8(size);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        var cx = size * 0.5f;
        var cy = size * 0.5f;
        var maxR = size * 0.5f - 1;
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };

        if (Shape == BrushTipShape.SoftRound)
        {
            // Always carries some softness regardless of hardness slider
            var sigma = (0.12f + (1f - hardness) * 0.22f) * size;
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
            DrawEllipse(canvas, paint, cx, cy, maxR, BrushTipShape.Circle, 1f);
        }
        else
        {
            var aspect = Shape == BrushTipShape.Flat ? 4.0f : Math.Clamp(AspectRatio, 0.05f, 20f);
            var sigma = (1f - hardness) * size * 0.22f;
            paint.MaskFilter = sigma > 0.01f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null;
            DrawEllipse(canvas, paint, cx, cy, maxR, Shape, aspect);
            if (hardness < 0.999f)
            {
                paint.MaskFilter = null;
                DrawEllipse(canvas, paint, cx, cy, maxR * hardness, Shape, aspect);
            }
        }
        return bmp;
    }

    private static SKBitmap GenerateChalk(int size, float hardness)
    {
        var bmp = NewAlpha8(size);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        var cx = size * 0.5f;
        var cy = size * 0.5f;
        var r = size * 0.46f;
        var segLen = MathF.Max(1f, size * 0.035f);
        var jitter = MathF.Max(0.5f, size * (0.025f + (1f - hardness) * 0.05f));
        using var pe = SKPathEffect.CreateDiscrete(segLen, jitter, 31u);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            PathEffect = pe,
        };
        var sigma = (1f - hardness) * size * 0.08f;
        paint.MaskFilter = sigma > 0.5f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null;
        canvas.DrawOval(SKRect.Create(cx - r, cy - r, r * 2, r * 2), paint);
        return bmp;
    }

    private static SKBitmap GenerateBristle(int size, float hardness)
    {
        var bmp = NewAlpha8(size);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        const int strands = 7;
        var rng = new Random(42);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        var sigma = (1f - hardness) * size * 0.04f;
        paint.MaskFilter = sigma > 0.5f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null;
        for (var i = 0; i < strands; i++)
        {
            var t = (i + 0.5f) / strands;
            var y = t * size;
            var env = (float)Math.Sin(t * Math.PI);
            var xOff = (float)(rng.NextDouble() - 0.5) * size * 0.05f;
            paint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp((0.45f + 0.55f * env) * 255, 60, 255));
            paint.StrokeWidth = MathF.Max(0.5f, size * (0.008f + 0.018f * env));
            canvas.DrawLine(xOff, y, size + xOff, y, paint);
        }
        return bmp;
    }

    private static SKBitmap GenerateScatter(int size, float hardness)
    {
        var bmp = NewAlpha8(size);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        var cx = size * 0.5f;
        var cy = size * 0.5f;
        var rng = new Random(77);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        var sigma = (1f - hardness) * size * 0.05f;
        paint.MaskFilter = sigma > 0.5f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null;
        const int dots = 9;
        for (var i = 0; i < dots; i++)
        {
            var angle = rng.NextDouble() * Math.PI * 2;
            var dist = (float)(Math.Sqrt(rng.NextDouble()) * 0.38 * size);
            var dotX = cx + (float)(dist * Math.Cos(angle));
            var dotY = cy + (float)(dist * Math.Sin(angle));
            var dotR = (float)(size * (0.08 + rng.NextDouble() * 0.09));
            paint.Color = new SKColor(255, 255, 255, (byte)rng.Next(160, 256));
            canvas.DrawOval(SKRect.Create(dotX - dotR, dotY - dotR, dotR * 2, dotR * 2), paint);
        }
        return bmp;
    }

    private static void DrawEllipse(SKCanvas canvas, SKPaint paint, float cx, float cy, float r, BrushTipShape shape, float aspect)
    {
        var rx = r;
        var ry = r;
        if (aspect > 1) ry /= aspect;
        else rx *= aspect;
        var rect = SKRect.Create(cx - rx, cy - ry, rx * 2, ry * 2);
        if (shape == BrushTipShape.Rectangle)
            canvas.DrawRect(rect, paint);
        else
            canvas.DrawOval(rect, paint);
    }

    private static SKBitmap NewAlpha8(int size) =>
        new(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));
}
