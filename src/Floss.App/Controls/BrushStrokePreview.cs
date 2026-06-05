using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Controls;

public sealed class BrushStrokePreview : Control
{
    private static readonly SKColor BgColor = new(0x28, 0x24, 0x28);

    private BrushPreset? _brush;
    private WriteableBitmap? _bitmap;
    private int _renderedW;
    private int _renderedH;
    private DispatcherTimer? _debounceTimer;

    /// <summary>
    /// List-row mode: short horizontal stroke with a small fixed mask (cheap enough for many presets).
    /// </summary>
    public bool CompactPreview { get; set; }

    /// <summary>
    /// When set, rasterize at this size even if the control is wider (CSP-style: dock resize does not rebuild).
    /// </summary>
    public int FixedRenderWidth { get; set; }

    public int FixedRenderHeight { get; set; }

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

        var rw = FixedRenderWidth > 0 ? FixedRenderWidth : w;
        var rh = FixedRenderHeight > 0 ? FixedRenderHeight : h;
        if (rw < 2 || rh < 2) return;

        if (_bitmap == null || _renderedW != rw || _renderedH != rh)
        {
            _bitmap?.Dispose();
            _bitmap = RenderPreview(rw, rh, _brush, CompactPreview);
            _renderedW = rw;
            _renderedH = rh;
        }

        var dest = FixedRenderWidth > 0 && FixedRenderHeight > 0
            ? new Rect((w - rw) * 0.5, (h - rh) * 0.5, rw, rh)
            : new Rect(0, 0, w, h);
        context.DrawImage(_bitmap, dest);
    }

    private static unsafe WriteableBitmap RenderPreview(int w, int h, BrushPreset brush, bool compact)
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
        if (compact)
            PaintListRowStroke(canvas, w, h, brush);
        else
            PaintStroke(canvas, w, h, brush);
        surface.Flush();
        return bmp;
    }

    private static void PaintListRowStroke(SKCanvas canvas, int w, int h, BrushPreset brush)
    {
        BrushMaterialTips.BindToPreset(brush);
        const int steps = 96;
        const int maxMaskSize = 28;
        var baseSize = (float)Math.Clamp(Math.Min(brush.Size, h * 0.82), 6.0, maxMaskSize);
        var maskSize = Math.Max(1, (int)Math.Ceiling(baseSize));

        var tip = brush.Tip;
        var stamp = tip.HasColor
            ? tip.GenerateColorStamp(maskSize)
            : tip.GenerateMask(maskSize, (float)brush.Hardness);
        if (stamp == null) return;

        using var colorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver,
            ColorFilter = colorFilter,
        };
        using var colorStampPaint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver
        };

        var hPad = w * 0.04f;
        var pathW = w - hPad * 2f;
        float prevX = hPad, prevY = h * 0.5f;
        double accum = 0;
        double nextSpacing = BrushSpacing.EffectiveDistance(brush, baseSize, 1f);
        int dabIdx = 0;

        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var envelope = (float)Math.Sin(t * Math.PI);
            var px = hPad + pathW * t;
            var py = h * 0.5f + (float)Math.Sin(t * Math.PI * 2.0) * h * 0.18f * envelope;

            var segDx = px - prevX;
            var segDy = py - prevY;
            var segLen = (float)Math.Sqrt(segDx * segDx + segDy * segDy);
            if (segLen < 0.001f) { prevX = px; prevY = py; continue; }

            var drawAngle = (float)Math.Atan2(segDy, segDx);
            accum += segLen;

            while (accum >= nextSpacing)
            {
                accum -= nextSpacing;
                var frac = Math.Clamp((float)(1.0 - accum / segLen), 0f, 1f);
                var dabX = prevX + segDx * frac;
                var dabY = prevY + segDy * frac;

                var opacity = (float)Math.Clamp(
                    brush.Opacity * brush.Flow * brush.TipDensity * (0.15f + 0.85f * envelope),
                    0.0, 1.0);
                var stampAlpha = (byte)(opacity * 255);
                paint.Color = new SKColor(255, 255, 255, stampAlpha);
                colorStampPaint.Color = new SKColor(255, 255, 255, stampAlpha);

                var stampSize = baseSize;
                var scale = stampSize / Math.Max(1, stamp.Width);
                var mx = SKMatrix.CreateTranslation(-stamp.Width * 0.5f, -stamp.Height * 0.5f)
                    .PostConcat(SKMatrix.CreateScale(scale, scale));

                var angle = (float)brush.Angle;
                if (brush.BaseAngleSource == BrushDynamics.AngleSource.DirectionOfLine)
                    angle += drawAngle * (180f / MathF.PI);
                if (MathF.Abs(angle) > 0.001f)
                    mx = mx.PostConcat(SKMatrix.CreateRotationDegrees(angle));
                mx = mx.PostConcat(SKMatrix.CreateTranslation(dabX, dabY));

                canvas.Save();
                canvas.Concat(in mx);
                canvas.DrawBitmap(stamp, 0, 0, tip.HasColor ? colorStampPaint : paint);
                canvas.Restore();

                nextSpacing = BrushSpacing.EffectiveDistance(brush, stampSize, 1f);
                dabIdx++;
            }

            prevX = px;
            prevY = py;
        }
    }

    private static void PaintStroke(SKCanvas canvas, int w, int h, BrushPreset brush)
    {
        BrushMaterialTips.BindToPreset(brush);
        // Cap size: large brushes still show their shape/texture
        var baseSize = (float)Math.Clamp(brush.Size, 1.0, h * 0.72);
        var maskSize = Math.Max(1, (int)Math.Ceiling(baseSize));
        var materialTips = brush.TipSelectionMode != BrushTipSelectionMode.Single && brush.Tips.Count > 0
            ? brush.Tips.Select(t => t.CreateTip()).ToList()
            : null;
        var maskCache = new Dictionary<int, SKBitmap>();
        var ownedMasks = new HashSet<SKBitmap>();

        IBrushTip TipFor(int tipIndex)
        {
            if (materialTips is { Count: > 0 })
                return materialTips[Math.Clamp(tipIndex, 0, materialTips.Count - 1)];
            return brush.Tip;
        }

        SKBitmap MaskForTip(int tipIndex)
        {
            tipIndex = materialTips is { Count: > 0 } ? Math.Clamp(tipIndex, 0, materialTips.Count - 1) : 0;
            if (maskCache.TryGetValue(tipIndex, out var cached))
                return cached;

            var tip = TipFor(tipIndex);
            var tipMask = tip.GenerateMask(maskSize, (float)brush.Hardness);
            if (brush.Shape == null)
            {
                maskCache[tipIndex] = tipMask;
                return tipMask;
            }

            var shapeMask = brush.Shape.GenerateMask(maskSize, (float)brush.Hardness);
            var combined = MultiplyMasks(tipMask, shapeMask, maskSize);
            ownedMasks.Add(combined);
            maskCache[tipIndex] = combined;
            return combined;
        }

        // Always white on dark — every brush reads cleanly regardless of its color
        using var colorFilter = SKColorFilter.CreateBlendMode(new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, 255), SKBlendMode.SrcIn);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver,
            ColorFilter = colorFilter,
        };
        using var colorStampPaint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver
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
        double nextSpacing = BrushSpacing.EffectiveDistance(brush, baseSize, 1f);
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
                var tipIndex = SelectTipIndex(brush, sp);
                var tip = TipFor(tipIndex);
                var stampBitmap = tip.HasColor
                    ? tip.GenerateColorStamp(maskSize)
                    : MaskForTip(tipIndex);
                if (stampBitmap == null) continue;

                var sizeM = brush.Dynamics.EvalSize(sp);
                var opacM = brush.Dynamics.EvalOpacity(sp);
                var tipDensityM = brush.Dynamics.TipDensity.IsEnabled ? brush.Dynamics.EvalTipDensity(sp) : 1f;
                var tipThicknessM = brush.Dynamics.TipThickness.IsEnabled ? brush.Dynamics.EvalTipThickness(sp) : 1f;
                var stampSize = (float)Math.Max(0.5, baseSize * sizeM);
                var opacity = (float)Math.Clamp(brush.Opacity * brush.Flow * brush.TipDensity * tipDensityM * opacM, 0.0, 1.0);
                var stampAlpha = (byte)(opacity * 255);
                paint.Color = new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, stampAlpha);
                colorStampPaint.Color = new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, stampAlpha);

                var scale = stampSize / Math.Max(1, stampBitmap.Width);
                var thickness = Math.Clamp((float)brush.TipThickness * tipThicknessM, 0.01f, 1f);
                var scaleX = scale;
                var scaleY = scale;
                if (brush.TipDirection == BrushTipDirection.Horizontal)
                    scaleY *= thickness;
                else
                    scaleX *= thickness;
                if (brush.FlipHorizontal) scaleX = -scaleX;
                if (brush.FlipVertical) scaleY = -scaleY;
                var mx = SKMatrix.CreateTranslation(-stampBitmap.Width * 0.5f, -stampBitmap.Height * 0.5f)
                                 .PostConcat(SKMatrix.CreateScale(scaleX, scaleY));

                float angle = (float)brush.Angle;
                if (brush.BaseAngleSource == BrushDynamics.AngleSource.DirectionOfLine)
                    angle += drawAngle * (180f / MathF.PI);
                if (MathF.Abs(angle) > 0.001f)
                    mx = mx.PostConcat(SKMatrix.CreateRotationDegrees(angle));

                mx = mx.PostConcat(SKMatrix.CreateTranslation(dabX, dabY));

                canvas.Save();
                canvas.Concat(in mx);
                canvas.DrawBitmap(stampBitmap, 0, 0, tip.HasColor ? colorStampPaint : paint);
                canvas.Restore();

                // Update spacing for the next dab (adapts to current stamp size)
                nextSpacing = BrushSpacing.EffectiveDistance(brush, stampSize, 1f);
                dabIdx++;
            }

            prevX = px;
            prevY = py;
        }

        foreach (var mask in ownedMasks)
            mask.Dispose();
        if (materialTips != null)
        {
            foreach (var tip in materialTips)
                if (tip is IDisposable disposable)
                    disposable.Dispose();
        }
    }

    private static int SelectTipIndex(BrushPreset brush, in StrokePoint sp)
    {
        if (brush.TipSelectionMode == BrushTipSelectionMode.Single || brush.Tips.Count <= 1)
            return 0;

        return brush.TipSelectionMode switch
        {
            BrushTipSelectionMode.Sequential => sp.DabSeqNo % brush.Tips.Count,
            BrushTipSelectionMode.Random => Math.Clamp((int)(DabHash(sp.DabSeqNo) * brush.Tips.Count), 0, brush.Tips.Count - 1),
            _ => 0
        };
    }

    private static unsafe SKBitmap MultiplyMasks(SKBitmap tip, SKBitmap shape, int size)
    {
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));
        var a = (byte*)tip.GetPixels().ToPointer();
        var b = (byte*)shape.GetPixels().ToPointer();
        var dst = (byte*)bmp.GetPixels().ToPointer();
        var aStride = tip.RowBytes;
        var bStride = shape.RowBytes;
        var dStride = bmp.RowBytes;
        var tw = Math.Min(tip.Width, size);
        var th = Math.Min(tip.Height, size);
        var sw = Math.Min(shape.Width, size);
        var sh = Math.Min(shape.Height, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var ta = y < th && x < tw ? a[y * aStride + x] : (byte)0;
                var sa = y < sh && x < sw ? b[y * bStride + x] : (byte)0;
                dst[y * dStride + x] = (byte)(ta * sa / 255);
            }
        return bmp;
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
