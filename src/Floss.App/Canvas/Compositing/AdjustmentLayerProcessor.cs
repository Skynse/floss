using System;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

internal static unsafe class AdjustmentLayerProcessor
{
    public static void Apply(
        byte* dst, int dstStride, int width, int height,
        AdjustmentLayerData adj, double opacityScale,
        PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0) return;
        var op = (float)Math.Clamp(opacityScale, 0.0, 1.0);
        switch (adj.Kind)
        {
            case AdjustmentKind.BrightnessContrast:      ApplyBC(dst, dstStride, adj, op, clip, originX, originY);    break;
            case AdjustmentKind.HueSaturationLuminosity: ApplyHSL(dst, dstStride, adj, op, clip, originX, originY);   break;
            case AdjustmentKind.Posterization:           ApplyPosterize(dst, dstStride, adj, op, clip, originX, originY); break;
            case AdjustmentKind.LevelCorrection:         ApplyLevels(dst, dstStride, adj, op, clip, originX, originY); break;
            case AdjustmentKind.ToneCurve:               ApplyCurves(dst, dstStride, adj, op, clip, originX, originY); break;
            case AdjustmentKind.ColorBalance:            ApplyColorBalance(dst, dstStride, adj, op, clip, originX, originY); break;
            case AdjustmentKind.Binarization:            ApplyBinarize(dst, dstStride, adj, op, clip, originX, originY); break;
            case AdjustmentKind.GradientMap:             ApplyGradientMap(dst, dstStride, adj, op, clip, originX, originY); break;
            case AdjustmentKind.ReverseGradient:         ApplyInvert(dst, dstStride, adj, op, clip, originX, originY);  break;
        }
    }

    /// <summary>
    /// Clipping-mask variant: applies adjustment only where baseLayer has alpha,
    /// scaling effective opacity by the base layer's per-pixel alpha.
    /// </summary>
    public static void ApplyClipped(
        byte* dst, int dstStride, int width, int height,
        AdjustmentLayerData adj, double opacityScale,
        DrawingLayer baseLayer,
        PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0) return;

        // Build a scratch buffer with the full adjustment applied at op=1,
        // then composite into dst pixel-by-pixel gated on baseLayer's alpha.
        var bufLen = clip.Width * clip.Height * 4;
        var buf = new byte[bufLen];
        fixed (byte* bufPtr = buf)
        {
            // Copy dst into scratch so the adjustment has something to work on.
            for (var y = clip.Y; y < clip.Bottom; y++)
            {
                var srcRow = dst + (y - originY) * dstStride + (clip.X - originX) * 4;
                var dstRow = bufPtr + (y - clip.Y) * clip.Width * 4;
                Buffer.MemoryCopy(srcRow, dstRow, clip.Width * 4, clip.Width * 4);
            }

            // Apply adjustment in place on scratch, full opacity.
            Apply(bufPtr, clip.Width * 4, clip.Width, clip.Height,
                adj, 1.0, clip, clip.X, clip.Y);

            // Composite scratch back using base-layer alpha as the mask.
            var baseOffX = baseLayer.OffsetX;
            var baseOffY = baseLayer.OffsetY;
            const int ts = TiledPixelBuffer.TileSize;
            var op = (float)Math.Clamp(opacityScale, 0.0, 1.0);

            var basePixels = baseLayer.Pixels;
            basePixels.EnterPixelReadLock();
            try
            {
                for (var y = clip.Y; y < clip.Bottom; y++)
                {
                    var dstRow = dst  + (y - originY) * dstStride + (clip.X - originX) * 4;
                    var adjRow = bufPtr + (y - clip.Y) * clip.Width * 4;

                    for (var x = clip.X; x < clip.Right; x++, dstRow += 4, adjRow += 4)
                    {
                        var bx = x - baseOffX;
                        var by = y - baseOffY;
                        if (bx < baseLayer.MinX || bx >= baseLayer.MaxX ||
                            by < baseLayer.MinY || by >= baseLayer.MaxY)
                            continue;

                        var tileX = LayerCompositorPixelOps.FloorDiv(bx, ts);
                        var tileY = LayerCompositorPixelOps.FloorDiv(by, ts);
                        var tile = basePixels.GetTileOrNull(tileX, tileY);
                        if (tile == null) continue;

                        var localX = bx - tileX * ts;
                        var localY = by - tileY * ts;
                        var baseA = tile[(localY * ts + localX) * 4 + 3];
                        if (baseA == 0) continue;

                        var eff = op * (baseA / 255f);
                        BlendPx(dstRow, adjRow[0], adjRow[1], adjRow[2], eff);
                    }
                }
            }
            finally
            {
                basePixels.ExitPixelReadLock();
            }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static byte C(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    private static void BlendPx(byte* px, byte rb, byte gb, byte bb, float op)
    {
        if (op >= 1f)
        {
            px[0] = rb; px[1] = gb; px[2] = bb;
        }
        else
        {
            px[0] = (byte)(px[0] + (rb - px[0]) * op);
            px[1] = (byte)(px[1] + (gb - px[1]) * op);
            px[2] = (byte)(px[2] + (bb - px[2]) * op);
        }
    }

    // Piecewise-linear lookup through a flat [x0,y0, x1,y1, ...] curve (0..255 space)
    private static float EvalCurve(float[] pts, float v)
    {
        if (pts.Length < 4) return v;
        if (v <= pts[0]) return pts[1];
        for (var i = 2; i < pts.Length; i += 2)
        {
            if (v <= pts[i])
            {
                var t = (v - pts[i - 2]) / (pts[i] - pts[i - 2]);
                return pts[i - 1] + t * (pts[i + 1] - pts[i - 1]);
            }
        }
        return pts[pts.Length - 1];
    }

    // ── Brightness / Contrast ─────────────────────────────────────────────────

    private static void ApplyBC(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        // Standard Photoshop-style formula
        var brightness = adj.Brightness;
        float cf = adj.Contrast;
        float cFactor = cf > 0
            ? 1f + cf / 100f * 2.5f
            : 1f + cf / 100f;

        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                var rb = C((row[0] - 128f) * cFactor + 128f + brightness);
                var gb = C((row[1] - 128f) * cFactor + 128f + brightness);
                var bb = C((row[2] - 128f) * cFactor + 128f + brightness);
                BlendPx(row, rb, gb, bb, op);
            }
        }
    }

    // ── Hue / Saturation / Luminosity ─────────────────────────────────────────

    private static void ApplyHSL(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        var hShift = adj.Hue / 360f;
        var sShift = adj.Saturation / 100f;
        var lShift = adj.Luminosity / 100f;

        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                float r = row[2] / 255f, g = row[1] / 255f, b = row[0] / 255f;
                RgbToHsl(r, g, b, out var h, out var s, out var l);

                h = (h + hShift) % 1f;
                if (h < 0) h += 1f;
                s = Math.Clamp(s + sShift, 0f, 1f);
                l = Math.Clamp(l + lShift, 0f, 1f);

                HslToRgb(h, s, l, out var ro, out var go, out var bo);
                BlendPx(row, C(bo * 255f), C(go * 255f), C(ro * 255f), op);
            }
        }
    }

    private static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        l = (max + min) * 0.5f;
        var d = max - min;
        if (d < 1e-5f) { h = s = 0; return; }
        s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        if (max == r)      h = ((g - b) / d + (g < b ? 6f : 0f)) / 6f;
        else if (max == g) h = ((b - r) / d + 2f) / 6f;
        else               h = ((r - g) / d + 4f) / 6f;
    }

    private static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
    {
        if (s < 1e-5f) { r = g = b = l; return; }
        var q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        var p = 2f * l - q;
        r = HueChannel(p, q, h + 1f / 3f);
        g = HueChannel(p, q, h);
        b = HueChannel(p, q, h - 1f / 3f);
    }

    private static float HueChannel(float p, float q, float t)
    {
        if (t < 0) t += 1f;
        if (t > 1) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f)    return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    // ── Posterization ────────────────────────────────────────────────────────

    private static void ApplyPosterize(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        var levels = Math.Max(2, adj.Levels);
        var step = 255f / (levels - 1);

        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                var rb = C(MathF.Round(row[0] / step) * step);
                var gb = C(MathF.Round(row[1] / step) * step);
                var bb = C(MathF.Round(row[2] / step) * step);
                BlendPx(row, rb, gb, bb, op);
            }
        }
    }

    // ── Level Correction ─────────────────────────────────────────────────────

    private static void ApplyLevels(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        var inBlack  = Math.Clamp(adj.LevelInBlack,  0f, 254f);
        var inWhite  = Math.Clamp(adj.LevelInWhite,  inBlack + 1f, 255f);
        var gamma    = Math.Clamp(adj.LevelGamma,    0.1f, 10f);
        var outBlack = Math.Clamp(adj.LevelOutBlack, 0f, 254f);
        var outWhite = Math.Clamp(adj.LevelOutWhite, outBlack + 1f, 255f);
        var inRange  = inWhite - inBlack;
        var outRange = outWhite - outBlack;
        var gammaInv = 1f / gamma;

        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                var rb = LevelCh(row[0], inBlack, inRange, gammaInv, outBlack, outRange);
                var gb = LevelCh(row[1], inBlack, inRange, gammaInv, outBlack, outRange);
                var bb = LevelCh(row[2], inBlack, inRange, gammaInv, outBlack, outRange);
                BlendPx(row, rb, gb, bb, op);
            }
        }
    }

    private static byte LevelCh(byte v, float inBlack, float inRange, float gammaInv, float outBlack, float outRange)
    {
        var t = Math.Clamp((v - inBlack) / inRange, 0f, 1f);
        t = MathF.Pow(t, gammaInv);
        return C(outBlack + t * outRange);
    }

    // ── Tone Curve ───────────────────────────────────────────────────────────

    private static void ApplyCurves(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        // Build LUTs for speed
        var lutAll = BuildLut(adj.CurveAll);
        var lutR   = BuildLut(adj.CurveR);
        var lutG   = BuildLut(adj.CurveG);
        var lutB   = BuildLut(adj.CurveB);

        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                var rb = lutB[lutAll[row[0]]];
                var gb = lutG[lutAll[row[1]]];
                var bb = lutR[lutAll[row[2]]]; // Note: BGRA - row[2]=R, apply lutR
                BlendPx(row, rb, gb, bb, op);
            }
        }
    }

    private static byte[] BuildLut(float[] curve)
    {
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
            lut[i] = C(EvalCurve(curve, i));
        return lut;
    }

    // ── Color Balance ────────────────────────────────────────────────────────

    private static void ApplyColorBalance(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                float b = row[0], g = row[1], r = row[2];
                float lum = (r + g + b) / (3f * 255f);

                var shadow    = Math.Max(0f, 1f - lum * 2f);
                var highlight = Math.Max(0f, lum * 2f - 1f);
                var midtone   = 1f - shadow - highlight;

                var dr = (adj.ShadowR * shadow + adj.MidtoneR * midtone + adj.HighlightR * highlight) * 255f / 100f;
                var dg = (adj.ShadowG * shadow + adj.MidtoneG * midtone + adj.HighlightG * highlight) * 255f / 100f;
                var db = (adj.ShadowB * shadow + adj.MidtoneB * midtone + adj.HighlightB * highlight) * 255f / 100f;

                BlendPx(row, C(b + db), C(g + dg), C(r + dr), op);
            }
        }
    }

    // ── Binarization ────────────────────────────────────────────────────────

    private static void ApplyBinarize(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        var thr = (int)Math.Clamp(adj.Threshold, 0f, 255f);
        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                var lum = (int)(row[2] * 299 + row[1] * 587 + row[0] * 114) / 1000;
                var v = (byte)(lum >= thr ? 255 : 0);
                BlendPx(row, v, v, v, op);
            }
        }
    }

    // ── Gradient Map ────────────────────────────────────────────────────────

    private static void ApplyGradientMap(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        // Build 256-entry LUT from gradient stops
        var stops = adj.GradientStops;
        var lutR = new byte[256];
        var lutG = new byte[256];
        var lutB = new byte[256];

        int stopCount = stops.Length / 4;
        if (stopCount < 2)
        {
            // degenerate: identity
            for (var i = 0; i < 256; i++) { lutR[i] = lutG[i] = lutB[i] = (byte)i; }
        }
        else
        {
            for (var i = 0; i < 256; i++)
            {
                var t = i / 255f;
                // find bracketing stops
                int lo = 0, hi = stopCount - 1;
                for (var s = 0; s < stopCount - 1; s++)
                {
                    if (stops[s * 4] <= t && stops[(s + 1) * 4] >= t)
                    {
                        lo = s; hi = s + 1; break;
                    }
                }
                var p0 = stops[lo * 4];
                var p1 = stops[hi * 4];
                var f = p1 > p0 ? (t - p0) / (p1 - p0) : 0f;
                f = Math.Clamp(f, 0f, 1f);
                lutR[i] = C((stops[lo * 4 + 1] + (stops[hi * 4 + 1] - stops[lo * 4 + 1]) * f) * 255f);
                lutG[i] = C((stops[lo * 4 + 2] + (stops[hi * 4 + 2] - stops[lo * 4 + 2]) * f) * 255f);
                lutB[i] = C((stops[lo * 4 + 3] + (stops[hi * 4 + 3] - stops[lo * 4 + 3]) * f) * 255f);
            }
        }

        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                var lum = (byte)((row[2] * 299 + row[1] * 587 + row[0] * 114) / 1000);
                BlendPx(row, lutB[lum], lutG[lum], lutR[lum], op);
            }
        }
    }

    // ── Reverse Gradient (Invert) ────────────────────────────────────────────

    private static void ApplyInvert(byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        _ = adj;
        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
                BlendPx(row, (byte)(255 - row[0]), (byte)(255 - row[1]), (byte)(255 - row[2]), op);
        }
    }
}
