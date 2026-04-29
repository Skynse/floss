using System;
using SkiaSharp;

namespace Floss.App.Brushes;

public enum BrushTipShape
{
    Circle,
    Ellipse,
    Rectangle
}

public sealed class ProceduralBrushTip(BrushTipShape shape = BrushTipShape.Circle, float aspectRatio = 1.0f) : IBrushTip
{
    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        var size = Math.Max(1, baseSize);
        var clampedHardness = Math.Clamp(hardness, 0.001f, 1.0f);
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));

        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var cx = size * 0.5f;
        var cy = size * 0.5f;
        var maxRadius = size * 0.5f - 1;
        var sigma = (1.0f - clampedHardness) * size * 0.22f;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            MaskFilter = sigma > 0.01f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null
        };

        DrawShape(canvas, paint, cx, cy, maxRadius, shape, aspectRatio);

        if (clampedHardness < 0.999f)
        {
            paint.MaskFilter = null;
            DrawShape(canvas, paint, cx, cy, maxRadius * clampedHardness, shape, aspectRatio);
        }

        return bitmap;
    }

    private static void DrawShape(SKCanvas canvas, SKPaint paint, float cx, float cy, float radius, BrushTipShape shape, float aspectRatio)
    {
        var ratio = Math.Clamp(aspectRatio, 0.05f, 20.0f);
        var rx = radius;
        var ry = radius;
        if (ratio > 1)
            ry /= ratio;
        else
            rx *= ratio;

        var rect = SKRect.Create(cx - rx, cy - ry, rx * 2, ry * 2);
        switch (shape)
        {
            case BrushTipShape.Rectangle:
                canvas.DrawRect(rect, paint);
                break;
            case BrushTipShape.Ellipse:
            case BrushTipShape.Circle:
            default:
                canvas.DrawOval(rect, paint);
                break;
        }
    }
}
