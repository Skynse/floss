using System;
using Floss.App.Brushes;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.Tools;

public sealed class SmudgeEngine
{
    private float _cr, _cg, _cb;
    private bool _hasCarried;

    public void Begin() { _hasCarried = false; }

    public unsafe PixelRegion Smudge(DrawingLayer layer, BrushPreset brush, float cx, float cy, float strength)
    {
        var maskSize = Math.Max(1, (int)Math.Ceiling(brush.Size));
        var tipMask = brush.Tip.GenerateMask(maskSize, (float)brush.Hardness);
        SKBitmap mask;
        bool ownMask;
        if (brush.Shape != null)
        {
            var shapeMask = brush.Shape.GenerateMask(maskSize, (float)brush.Hardness);
            mask = MultiplyMasks(tipMask, shapeMask, maskSize);
            if (brush.Tip is CompoundBrushTip) tipMask.Dispose();
            ownMask = true;
        }
        else
        {
            mask = tipMask;
            ownMask = brush.Tip is CompoundBrushTip;
        }

        var halfSize = maskSize / 2;
        var startX = (int)Math.Round(cx) - halfSize;
        var startY = (int)Math.Round(cy) - halfSize;

        var maskPtr = (byte*)mask.GetPixels().ToPointer();
        var maskStride = mask.RowBytes;

        // Weighted-average canvas color under the mask footprint
        float totalW = 0, sumR = 0, sumG = 0, sumB = 0;
        for (var my = 0; my < maskSize; my++)
        {
            var py = startY + my;
            if (py < 0 || py >= layer.Height) continue;
            for (var mx = 0; mx < maskSize; mx++)
            {
                var px = startX + mx;
                if (px < 0 || px >= layer.Width) continue;
                var w = maskPtr[my * maskStride + mx] / 255f;
                if (w < 0.004f) continue;
                layer.Pixels.GetPixel(px, py, out var pb, out var pg, out var pr, out var pa);
                if (pa == 0) continue;
                sumR += pr * w; sumG += pg * w; sumB += pb * w;
                totalW += w;
            }
        }

        if (!_hasCarried)
        {
            if (totalW > 0)
            {
                _cr = sumR / totalW;
                _cg = sumG / totalW;
                _cb = sumB / totalW;
                _hasCarried = true;
                // Fall through to render immediately so the first dab on color
                // produces visible output instead of requiring a second stroke.
            }
            else
            {
                if (ownMask) mask.Dispose();
                return PixelRegion.Empty;
            }
        }

        // Paint the carried color at the current position
        var region = new PixelRegion(startX, startY, maskSize, maskSize).ClipTo(layer.Width, layer.Height);
        if (!region.IsEmpty)
        {
            var paintColor = new SKColor(
                (byte)Math.Clamp(_cr, 0, 255),
                (byte)Math.Clamp(_cg, 0, 255),
                (byte)Math.Clamp(_cb, 0, 255));
            using var colorFilter = SKColorFilter.CreateBlendMode(paintColor, SKBlendMode.SrcIn);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                BlendMode = SKBlendMode.SrcOver,
                ColorFilter = colorFilter,
                Color = new SKColor(255, 255, 255, (byte)Math.Clamp(strength * 255, 0, 255))
            };
            var localMask = mask;
            var lx = startX;
            var ly = startY;
            layer.Pixels.RenderWithSkia(region, canvas =>
            {
                canvas.Save();
                canvas.Translate(lx, ly);
                canvas.DrawBitmap(localMask, 0, 0, paint);
                canvas.Restore();
            });
        }

        // Blend sampled color into carried
        if (totalW > 0)
        {
            var blend = Math.Clamp(strength * 0.45f, 0f, 1f);
            _cr = _cr * (1 - blend) + (sumR / totalW) * blend;
            _cg = _cg * (1 - blend) + (sumG / totalW) * blend;
            _cb = _cb * (1 - blend) + (sumB / totalW) * blend;
        }

        if (ownMask) mask.Dispose();
        return region;
    }

    private static unsafe SKBitmap MultiplyMasks(SKBitmap tip, SKBitmap shape, int size)
    {
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));
        var a = (byte*)tip.GetPixels().ToPointer();
        var b = (byte*)shape.GetPixels().ToPointer();
        var dst = (byte*)bmp.GetPixels().ToPointer();
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var ta = y < tip.Height && x < tip.Width ? a[y * tip.RowBytes + x] : (byte)0;
            var sa = y < shape.Height && x < shape.Width ? b[y * shape.RowBytes + x] : (byte)0;
            dst[y * bmp.RowBytes + x] = (byte)(ta * sa / 255);
        }
        return bmp;
    }
}
