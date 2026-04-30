using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Brushes;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App;

public sealed class BrushStrokePreview : Control
{
    private BrushPreset? _brush;
    private WriteableBitmap? _bitmap;
    private int _renderedW;
    private int _renderedH;

    public BrushPreset? Brush
    {
        get => _brush;
        set { _brush = value; InvalidateBitmap(); }
    }

    public void InvalidateBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = (int)Bounds.Width;
        var h = (int)Bounds.Height;
        if (w < 2 || h < 2) return;

        context.FillRectangle(new SolidColorBrush(Color.Parse("#e8e6e0")), new Rect(0, 0, w, h));

        if (_brush == null) return;

        if (_bitmap == null || _renderedW != w || _renderedH != h)
        {
            _bitmap?.Dispose();
            _bitmap = RenderPreview(w, h, _brush);
            _renderedW = w;
            _renderedH = h;
        }

        context.DrawImage(_bitmap, new Rect(0, 0, w, h));
    }

    private static unsafe WriteableBitmap RenderPreview(int w, int h, BrushPreset brush)
    {
        var bmp = new WriteableBitmap(
            new PixelSize(w, h),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var fb = bmp.Lock();
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
        if (surface == null) return bmp;
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0xe8, 0xe6, 0xe0));
        StampSineStroke(canvas, w, h, brush);
        surface.Flush();
        return bmp;
    }

    private static void StampSineStroke(SKCanvas canvas, int w, int h, BrushPreset brush)
    {
        var color = brush.Color;
        var skColor = new SKColor(color.R, color.G, color.B, color.A);
        var baseSize = Math.Max(1.0, brush.Size);
        var spacing = Math.Max(0.5, baseSize * brush.Spacing);
        var dynamics = DynamicsMatrix.FromBrush(brush);

        var mask = brush.Tip.GenerateMask(Math.Max(1, (int)Math.Ceiling(baseSize)), (float)brush.Hardness);
        var colorFilter = SKColorFilter.CreateBlendMode(skColor, SKBlendMode.SrcIn);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver,
            Color = skColor,
            ColorFilter = colorFilter
        };

        var amplitude = Math.Min(h * 0.28, baseSize * 3.5);
        var steps = Math.Max(300, (int)((w + baseSize * 2) / Math.Max(0.5, spacing)) + 32);
        double leftover = 0;
        double prevX = -baseSize;
        double prevY = h * 0.5;

        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var x = t * (w + baseSize * 2) - baseSize;
            var y = h * 0.5 + Math.Sin(t * Math.PI * 2.5) * amplitude;
            var pressure = (float)(Math.Sin(t * Math.PI) * 0.82 + 0.18);

            var dx = x - prevX;
            var dy = y - prevY;
            var segLen = Math.Sqrt(dx * dx + dy * dy);
            if (segLen < 0.001) { prevX = x; prevY = y; continue; }

            var consumed = leftover;
            while (consumed <= segLen)
            {
                var ratio = consumed / segLen;
                var sx = prevX + dx * ratio;
                var sy = prevY + dy * ratio;

                var sample = new CanvasInputSample(sx, sy, pressure, 0, 0, 0, 0, 0, CanvasInputSource.Pen, CanvasInputPhase.Move);
                var sizeM  = dynamics.Evaluate(DynamicOutputTarget.Size,    sample, 0.25f);
                var opacM  = dynamics.Evaluate(DynamicOutputTarget.Opacity, sample, 0.25f);

                var stampSize = (float)Math.Max(0.5, baseSize * sizeM);
                var opacity   = (float)Math.Clamp(brush.Opacity * brush.Flow * opacM, 0, 1);
                var alpha     = (byte)Math.Clamp((int)(opacity * 255), 0, 255);
                paint.Color   = new SKColor(color.R, color.G, color.B, alpha);

                var scale  = stampSize / Math.Max(1, mask.Width);
                var matrix = SKMatrix.CreateTranslation(-mask.Width * 0.5f, -mask.Height * 0.5f);
                matrix = matrix.PostConcat(SKMatrix.CreateScale(scale, scale));
                matrix = matrix.PostConcat(SKMatrix.CreateTranslation((float)sx, (float)sy));

                canvas.Save();
                canvas.Concat(in matrix);
                canvas.DrawBitmap(mask, 0, 0, paint);
                canvas.Restore();

                consumed += spacing;
            }

            leftover = segLen - (consumed - spacing);
            if (leftover >= spacing) leftover = 0;
            prevX = x;
            prevY = y;
        }

        colorFilter.Dispose();
        if (brush.Tip is CompoundBrushTip)
            mask.Dispose();
    }
}
