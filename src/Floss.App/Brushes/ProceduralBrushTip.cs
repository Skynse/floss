using System;
using System.Collections.Generic;
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
    private readonly Dictionary<(int Size, int Hardness), SKBitmap> _maskCache = [];

    public BrushTipShape Shape { get; } = shape;
    public float AspectRatio { get; } = aspectRatio;

    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Max(1, baseSize);
        var h = Math.Clamp(hardness, 0.001f, 1.0f);
        var key = (size, QuantizeHardness(h));
        if (_maskCache.TryGetValue(key, out var cached))
            return cached;

        var mask = Shape switch
        {
            BrushTipShape.Chalk => GenerateChalk(size, h),
            BrushTipShape.Bristle => GenerateBristle(size, h),
            BrushTipShape.Scatter => GenerateScatter(size, h),
            _ => GenerateSmooth(size, h),
        };
        _maskCache[key] = mask;
        return mask;
    }

    private static int QuantizeHardness(float hardness)
        => Math.Clamp((int)MathF.Round(Math.Clamp(hardness, 0.001f, 1f) * 255f), 0, 255);

    private unsafe SKBitmap GenerateSmooth(int size, float hardness)
    {
        var cx = size * 0.5f;
        var cy = size * 0.5f;
        var maxR = size * 0.5f - 0.5f;

        // Circle and ellipse shapes use per-pixel radial alpha so the soft edge
        // never gets clipped by bitmap boundaries (the Gaussian-blur approach was
        // cutting off the falloff and making large soft brushes look square).
        if (Shape is BrushTipShape.Circle or BrushTipShape.SoftRound or BrushTipShape.Ellipse)
        {
            var bmp = NewAlpha8(size);
            var pixels = bmp.GetPixels();
            if (pixels == IntPtr.Zero) return bmp; // allocation failed; return blank
            var dst = (byte*)pixels.ToPointer();
            var stride = bmp.RowBytes;
            if (maxR < 0.001f) { dst[0] = 255; return bmp; } // degenerate 1px case

            float aspect = Shape == BrushTipShape.Ellipse ? Math.Clamp(AspectRatio, 0.05f, 20f) : 1f;
            // rx/ry: half-axes of the ellipse in pixels.
            float rx = aspect >= 1f ? maxR : maxR * aspect;
            float ry = aspect >= 1f ? maxR / aspect : maxR;
            bool isSoft = Shape == BrushTipShape.SoftRound;

            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - cx) / rx;
                    float dy = (y + 0.5f - cy) / ry;
                    float t = MathF.Sqrt(dx * dx + dy * dy); // 0=centre, 1=edge

                    float alpha;
                    if (t >= 1f)
                    {
                        alpha = 0f;
                    }
                    else if (t <= hardness)
                    {
                        alpha = 1f;
                    }
                    else
                    {
                        var fade = (t - hardness) / (1f - hardness);
                        // Smoothstep for SoftRound, cosine-ease for Circle.
                        alpha = isSoft
                            ? 1f - fade * fade * (3f - 2f * fade)
                            : (MathF.Cos(fade * MathF.PI) + 1f) * 0.5f;
                    }

                    dst[y * stride + x] = (byte)(alpha * 255f + 0.5f);
                }
            return bmp;
        }
        else
        {
            // Flat, Rectangle — Gaussian blur is fine; no risk of clipping at edge
            // because these shapes are rectilinear and intentionally hard-edged.
            var bmp = NewAlpha8(size);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);
            var aspect = Shape == BrushTipShape.Flat ? 4.0f : Math.Clamp(AspectRatio, 0.05f, 20f);
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
            var sigma = (1f - hardness) * size * 0.22f;
            paint.MaskFilter = sigma > 0.01f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null;
            DrawEllipse(canvas, paint, cx, cy, maxR, Shape, aspect);
            if (hardness < 0.999f)
            {
                paint.MaskFilter = null;
                DrawEllipse(canvas, paint, cx, cy, maxR * hardness, Shape, aspect);
            }
            return bmp;
        }
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
