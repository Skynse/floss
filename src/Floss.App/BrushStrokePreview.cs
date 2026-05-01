using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App;

public sealed class BrushStrokePreview : Control
{
    private static readonly SKColor BgColor = new(0x28, 0x24, 0x28);

    private BrushPreset? _brush;
    private WriteableBitmap? _bitmap;
    private int _renderedW;
    private int _renderedH;
    private DispatcherTimer? _debounceTimer;

    public BrushPreset? Brush
    {
        get => _brush;
        set { _brush = value; InvalidateBitmap(); }
    }

    public void InvalidateBitmap()
    {
        if (_debounceTimer == null)
        {
            _debounceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, OnDebounceTick)
            {
                IsEnabled = false
            };
        }
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();
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

        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x28, 0x24, 0x28)), new Rect(0, 0, w, h));
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
        canvas.Clear(BgColor);
        PaintStroke(canvas, w, h, brush);
        surface.Flush();
        return bmp;
    }

    private static void PaintStroke(SKCanvas canvas, int w, int h, BrushPreset brush)
    {
        // Cap size: large brushes still show their shape/texture
        var baseSize = (float)Math.Clamp(brush.Size, 1.0, h * 0.72);
        var mask = brush.Tip.GenerateMask(Math.Max(1, (int)Math.Ceiling(baseSize)), (float)brush.Hardness);

        // Always white on dark — every brush reads cleanly regardless of its color
        using var colorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver,
            ColorFilter = colorFilter,
        };

        var hPad = w * 0.05f;
        var pathW = w - hPad * 2f;
        const float strokeRandom = 0.37f;

        // Accumulate distance along path; place dabs when we've travelled >= nextSpacing.
        // nextSpacing is recomputed per-dab from the current stamp size so size dynamics
        // never create gaps between stamps.
        const int steps = 600;
        float prevX = hPad, prevY = h * 0.5f;
        double accum = 0;
        double nextSpacing = Math.Max(0.5, baseSize * brush.Spacing);
        int dabIdx = 0;

        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            // Gentle S-curve: sin envelope fades the wave in and out so it starts/ends on-axis
            var envelope = (float)Math.Sin(t * Math.PI);
            var px = hPad + pathW * t;
            var py = h * 0.5f + (float)Math.Sin(t * Math.PI * 2.0) * h * 0.20f * envelope;

            // Pressure: full bell-curve, 0.15 → 1.0 → 0.15
            var pressure = 0.15f + 0.85f * envelope;
            var segDx = px - prevX;
            var segDy = py - prevY;
            var segLen = (float)Math.Sqrt(segDx * segDx + segDy * segDy);
            if (segLen < 0.001f) { prevX = px; prevY = py; continue; }

            var drawAngle = (float)Math.Atan2(segDy, segDx);
            accum += segLen;

            while (accum >= nextSpacing)
            {
                accum -= nextSpacing;

                // Exact dab position: backtrack from segment end by remaining accum
                var frac = Math.Clamp((float)(1.0 - accum / segLen), 0f, 1f);
                var dabX = prevX + segDx * frac;
                var dabY = prevY + segDy * frac;

                var sp = new StrokePoint(
                    dabX, dabY, pressure, 0, 0, 0,
                    drawAngle, 0.3f, (float)(t * w),
                    dabIdx, DabHash(dabIdx), strokeRandom);

                var sizeM  = brush.Dynamics.EvalSize(sp);
                var opacM  = brush.Dynamics.EvalOpacity(sp);
                var stampSize = (float)Math.Max(0.5, baseSize * sizeM);
                var opacity   = (float)Math.Clamp(brush.Opacity * brush.Flow * opacM, 0.0, 1.0);
                paint.Color = new SKColor(255, 255, 255, (byte)(opacity * 255));

                var scale = stampSize / Math.Max(1, mask.Width);
                var mx = SKMatrix.CreateTranslation(-mask.Width * 0.5f, -mask.Height * 0.5f)
                                 .PostConcat(SKMatrix.CreateScale(scale, scale));

                float angle = (float)brush.Angle;
                if (brush.BaseAngleSource == BrushDynamics.AngleSource.DirectionOfLine)
                    angle += drawAngle * (180f / MathF.PI);
                if (MathF.Abs(angle) > 0.001f)
                    mx = mx.PostConcat(SKMatrix.CreateRotationDegrees(angle));

                mx = mx.PostConcat(SKMatrix.CreateTranslation(dabX, dabY));

                canvas.Save();
                canvas.Concat(in mx);
                canvas.DrawBitmap(mask, 0, 0, paint);
                canvas.Restore();

                // Update spacing for the next dab (adapts to current stamp size)
                nextSpacing = Math.Max(0.5, stampSize * brush.Spacing);
                dabIdx++;
            }

            prevX = px;
            prevY = py;
        }

        if (brush.Tip is CompoundBrushTip)
            mask.Dispose();
    }

    private static float DabHash(int i)
    {
        unchecked
        {
            var h = (uint)(i * 2654435761u);
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }
    }
}
