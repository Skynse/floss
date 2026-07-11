using System;
using System.Buffers;
using Floss.App.Document;

namespace Floss.App.Canvas.Compositing;

internal static unsafe class AdjustmentLayerProcessor
{
    public static void ApplyWithLayer(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer layer, double opacityScale,
        PixelRegion clip, int originX, int originY)
    {
        var adj = layer.Adjustment;
        if (adj == null || opacityScale <= 0) return;

        if (!layer.HasMask || !layer.IsMaskVisible)
        {
            Apply(dst, dstStride, width, height, adj, opacityScale, clip, originX, originY);
            return;
        }

        ApplyWithMask(dst, dstStride, adj, layer, (float)Math.Clamp(opacityScale, 0.0, 1.0),
            clip, originX, originY);
    }

    /// <summary>
    /// Masked adjustment: LUT lookup in-place, iterate mask tiles (not per-pixel GetTileOrNull).
    /// Avoids full-clip scratch copy + full-clip transform on transparent mask areas.
    /// </summary>
    private static void ApplyWithMask(
        byte* dst, int dstStride, AdjustmentLayerData adj, DrawingLayer layer, float op,
        PixelRegion clip, int originX, int originY)
    {
        if (IsEffectivelyIdentity(adj)) return;

        var mask = layer.MaskPixels!;
        const int ts = TiledPixelBuffer.TileSize;
        var offX = layer.OffsetX;
        var offY = layer.OffsetY;

        var layerLeft = Math.Max(0, clip.X - offX);
        var layerTop = Math.Max(0, clip.Y - offY);
        var layerRight = Math.Min(layer.Width, clip.Right - offX);
        var layerBottom = Math.Min(layer.Height, clip.Bottom - offY);
        if (layerLeft >= layerRight || layerTop >= layerBottom) return;

        var firstTileX = LayerCompositorPixelOps.FloorDiv(layerLeft, ts);
        var firstTileY = LayerCompositorPixelOps.FloorDiv(layerTop, ts);
        var lastTileX = LayerCompositorPixelOps.FloorDiv(layerRight - 1, ts);
        var lastTileY = LayerCompositorPixelOps.FloorDiv(layerBottom - 1, ts);

        var opacityByte = (uint)Math.Round(op * 255);
        var fullOpacity = opacityByte == 255;

        mask.EnterPixelReadLock();
        try
        {
            for (var ty = firstTileY; ty <= lastTileY; ty++)
            {
                for (var tx = firstTileX; tx <= lastTileX; tx++)
                {
                    var maskTile = mask.GetTileOrNull(tx, ty);
                    if (maskTile == null) continue;

                    var tileLayerLeft = Math.Max(layerLeft, tx * ts);
                    var tileLayerTop = Math.Max(layerTop, ty * ts);
                    var tileLayerRight = Math.Min(layerRight, tx * ts + ts);
                    var tileLayerBottom = Math.Min(layerBottom, ty * ts + ts);

                    for (var ly = tileLayerTop; ly < tileLayerBottom; ly++)
                    {
                        var localY = ly - ty * ts;
                        var docY = ly + offY;
                        var dstRow = dst + (docY - originY) * dstStride;
                        var maskRowBase = localY * ts * 4;

                        for (var lx = tileLayerLeft; lx < tileLayerRight; lx++)
                        {
                            var localX = lx - tx * ts;
                            var maskA = maskTile[maskRowBase + localX * 4 + 3];
                            if (maskA == 0) continue;

                            var docX = lx + offX;
                            var px = dstRow + (docX - originX) * 4;
                            if (px[3] == 0) continue;
                            TransformRgb(adj, px[0], px[1], px[2], out var rb, out var gb, out var bb);

                            if (fullOpacity && maskA == 255)
                            {
                                px[0] = rb;
                                px[1] = gb;
                                px[2] = bb;
                                continue;
                            }

                            var srcA = fullOpacity
                                ? (uint)maskA
                                : (uint)((maskA * opacityByte + 127) / 255);
                            if (srcA == 0) continue;
                            if (srcA == 255)
                            {
                                px[0] = rb;
                                px[1] = gb;
                                px[2] = bb;
                                continue;
                            }

                            var inv = 255 - srcA;
                            px[0] = (byte)((rb * srcA + px[0] * inv + 127) / 255);
                            px[1] = (byte)((gb * srcA + px[1] * inv + 127) / 255);
                            px[2] = (byte)((bb * srcA + px[2] * inv + 127) / 255);
                        }
                    }
                }
            }
        }
        finally
        {
            mask.ExitPixelReadLock();
        }
    }

    public static void Apply(
        byte* dst, int dstStride, int width, int height,
        AdjustmentLayerData adj, double opacityScale,
        PixelRegion clip, int originX, int originY)
    {
        if (opacityScale <= 0 || clip.IsEmpty) return;
        var op = (float)Math.Clamp(opacityScale, 0.0, 1.0);
        ApplyPixels(dst, dstStride, adj, op, clip, originX, originY);
    }

    public static void ApplyClipped(
        byte* dst, int dstStride, int width, int height,
        DrawingLayer adjLayer, double opacityScale,
        DrawingLayer baseLayer,
        PixelRegion clip, int originX, int originY)
    {
        var adj = adjLayer.Adjustment;
        if (adj == null || opacityScale <= 0 || clip.IsEmpty) return;

        var hasMask = adjLayer.HasMask && adjLayer.IsMaskVisible;
        var maskPixels = adjLayer.MaskPixels;

        var bufLen = clip.Width * clip.Height * 4;
        var buf = ArrayPool<byte>.Shared.Rent(bufLen);
        try
        {
            fixed (byte* bufPtr = buf)
            {
                for (var y = clip.Y; y < clip.Bottom; y++)
                {
                    var srcRow = dst + (y - originY) * dstStride + (clip.X - originX) * 4;
                    var dstRow = bufPtr + (y - clip.Y) * clip.Width * 4;
                    Buffer.MemoryCopy(srcRow, dstRow, clip.Width * 4, clip.Width * 4);
                }

                Apply(bufPtr, clip.Width * 4, clip.Width, clip.Height,
                    adj, 1.0, clip, clip.X, clip.Y);

                var baseOffX = baseLayer.OffsetX;
                var baseOffY = baseLayer.OffsetY;
                const int ts = TiledPixelBuffer.TileSize;
                var op = (float)Math.Clamp(opacityScale, 0.0, 1.0);

                var basePixels = baseLayer.Pixels;
                var adjOffX = adjLayer.OffsetX;
                var adjOffY = adjLayer.OffsetY;
                basePixels.EnterPixelReadLock();
                if (hasMask) maskPixels!.EnterPixelReadLock();
                try
                {
                    for (var y = clip.Y; y < clip.Bottom; y++)
                    {
                        var dstRow = dst + (y - originY) * dstStride + (clip.X - originX) * 4;
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

                            float maskFactor = 1f;
                            if (hasMask)
                            {
                                var mx = x - adjOffX;
                                var my = y - adjOffY;
                                var mTileX = LayerCompositorPixelOps.FloorDiv(mx, ts);
                                var mTileY = LayerCompositorPixelOps.FloorDiv(my, ts);
                                var mTile = maskPixels!.GetTileOrNull(mTileX, mTileY);
                                if (mTile == null) continue;
                                var mLocalX = mx - mTileX * ts;
                                var mLocalY = my - mTileY * ts;
                                maskFactor = mTile[(mLocalY * ts + mLocalX) * 4 + 3] / 255f;
                                if (maskFactor <= 0) continue;
                            }

                            var eff = op * (baseA / 255f) * maskFactor;
                            BlendPx(dstRow, adjRow[0], adjRow[1], adjRow[2], eff);
                        }
                    }
                }
                finally
                {
                    if (hasMask) maskPixels!.ExitPixelReadLock();
                    basePixels.ExitPixelReadLock();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Exact per-pixel transform at full opacity (used to bake the RGB cube).</summary>
    internal static void TransformPixel(Span<byte> px, AdjustmentLayerData adj)
    {
        var b = px[0];
        var g = px[1];
        var r = px[2];
        TransformRgb(adj, b, g, r, out var bo, out var go, out var ro);
        px[0] = bo;
        px[1] = go;
        px[2] = ro;
    }

    private static void ApplyPixels(
        byte* dst, int dstStride, AdjustmentLayerData adj, float op,
        PixelRegion clip, int ox, int oy)
    {
        if (IsEffectivelyIdentity(adj)) return;

        for (var y = clip.Y; y < clip.Bottom; y++)
        {
            var row = dst + (y - oy) * dstStride + (clip.X - ox) * 4;
            for (var x = clip.X; x < clip.Right; x++, row += 4)
            {
                if (row[3] == 0) continue;
                TransformRgb(adj, row[0], row[1], row[2], out var rb, out var gb, out var bb);
                BlendPx(row, rb, gb, bb, op);
            }
        }
    }

    internal static bool IsEffectivelyIdentity(AdjustmentLayerData adj) => adj.Kind switch
    {
        AdjustmentKind.BrightnessContrast => adj.Brightness == 0f && adj.Contrast == 0f,
        AdjustmentKind.HueSaturationLuminosity => adj.Hue == 0f && adj.Saturation == 0f && adj.Luminosity == 0f,
        AdjustmentKind.Posterization => false,
        AdjustmentKind.LevelCorrection =>
            adj.LevelInBlack == 0f && adj.LevelInWhite == 255f && adj.LevelGamma == 1f
            && adj.LevelOutBlack == 0f && adj.LevelOutWhite == 255f,
        AdjustmentKind.ToneCurve => IsFlatCurve(adj.CurveAll) && IsFlatCurve(adj.CurveR)
            && IsFlatCurve(adj.CurveG) && IsFlatCurve(adj.CurveB),
        AdjustmentKind.ColorBalance =>
            adj.ShadowR == 0f && adj.ShadowG == 0f && adj.ShadowB == 0f
            && adj.MidtoneR == 0f && adj.MidtoneG == 0f && adj.MidtoneB == 0f
            && adj.HighlightR == 0f && adj.HighlightG == 0f && adj.HighlightB == 0f,
        AdjustmentKind.Binarization => false,
        AdjustmentKind.GradientMap => false,
        AdjustmentKind.ReverseGradient => false,
        _ => true,
    };

    private static bool IsFlatCurve(float[] pts) =>
        pts.Length >= 4 && pts[0] == 0f && pts[1] == 0f && pts[^2] == 255f && pts[^1] == 255f;

    private static void TransformRgb(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        switch (adj.Kind)
        {
            case AdjustmentKind.BrightnessContrast:
                TransformBC(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.HueSaturationLuminosity:
                TransformHSL(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.Posterization:
                TransformPosterize(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.LevelCorrection:
                TransformLevels(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.ToneCurve:
                TransformCurves(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.ColorBalance:
                TransformColorBalance(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.Binarization:
                TransformBinarize(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.GradientMap:
                TransformGradientMap(adj, b, g, r, out bo, out go, out ro);
                break;
            case AdjustmentKind.ReverseGradient:
                bo = (byte)(255 - b);
                go = (byte)(255 - g);
                ro = (byte)(255 - r);
                break;
            default:
                bo = b; go = g; ro = r;
                break;
        }
    }

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

    private static void TransformBC(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        var brightness = adj.Brightness;
        float cf = adj.Contrast;
        float cFactor = cf > 0 ? 1f + cf / 100f * 2.5f : 1f + cf / 100f;
        bo = C((b - 128f) * cFactor + 128f + brightness);
        go = C((g - 128f) * cFactor + 128f + brightness);
        ro = C((r - 128f) * cFactor + 128f + brightness);
    }

    /// <summary>
    /// Same HSL math as <see cref="Floss.App.Filters.FilterEngine.ApplyHueSaturationLightness"/>.
    /// Sliders are degrees / percent (−100..100 sat &amp; lum); filter uses −1..1 for sat &amp; light.
    /// </summary>
    private static void TransformHSL(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        var saturation = adj.Saturation / 100f;
        var lightness = adj.Luminosity / 100f;
        RgbToHsl(r / 255f, g / 255f, b / 255f, out var h, out var s, out var l);
        h = Wrap01(h + adj.Hue / 360f);
        s = saturation >= 0 ? s + (1f - s) * saturation : s * (1f + saturation);
        l = lightness >= 0 ? l + (1f - l) * lightness : l * (1f + lightness);
        HslToRgb(h, Math.Clamp(s, 0f, 1f), Math.Clamp(l, 0f, 1f), out var rof, out var gof, out var bof);
        bo = C(bof * 255f);
        go = C(gof * 255f);
        ro = C(rof * 255f);
    }

    private static float Wrap01(float value)
    {
        value %= 1f;
        return value < 0 ? value + 1f : value;
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

    private static void TransformPosterize(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        var levels = Math.Max(2, adj.Levels);
        var step = 255f / (levels - 1);
        bo = C(MathF.Round(b / step) * step);
        go = C(MathF.Round(g / step) * step);
        ro = C(MathF.Round(r / step) * step);
    }

    private static void TransformLevels(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        var inBlack = Math.Clamp(adj.LevelInBlack, 0f, 254f);
        var inWhite = Math.Clamp(adj.LevelInWhite, inBlack + 1f, 255f);
        var gamma = Math.Clamp(adj.LevelGamma, 0.1f, 10f);
        var outBlack = Math.Clamp(adj.LevelOutBlack, 0f, 254f);
        var outWhite = Math.Clamp(adj.LevelOutWhite, outBlack + 1f, 255f);
        var inRange = inWhite - inBlack;
        var outRange = outWhite - outBlack;
        var gammaInv = 1f / gamma;
        bo = LevelCh(b, inBlack, inRange, gammaInv, outBlack, outRange);
        go = LevelCh(g, inBlack, inRange, gammaInv, outBlack, outRange);
        ro = LevelCh(r, inBlack, inRange, gammaInv, outBlack, outRange);
    }

    private static byte LevelCh(byte v, float inBlack, float inRange, float gammaInv, float outBlack, float outRange)
    {
        var t = Math.Clamp((v - inBlack) / inRange, 0f, 1f);
        t = MathF.Pow(t, gammaInv);
        return C(outBlack + t * outRange);
    }

    private static void TransformCurves(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        bo = C(EvalCurve(adj.CurveB, EvalCurve(adj.CurveAll, b)));
        go = C(EvalCurve(adj.CurveG, EvalCurve(adj.CurveAll, g)));
        ro = C(EvalCurve(adj.CurveR, EvalCurve(adj.CurveAll, r)));
    }

    private static void TransformColorBalance(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        float lum = (r + g + b) / (3f * 255f);
        var shadow = Math.Max(0f, 1f - lum * 2f);
        var highlight = Math.Max(0f, lum * 2f - 1f);
        var midtone = 1f - shadow - highlight;
        var dr = (adj.ShadowR * shadow + adj.MidtoneR * midtone + adj.HighlightR * highlight) * 255f / 100f;
        var dg = (adj.ShadowG * shadow + adj.MidtoneG * midtone + adj.HighlightG * highlight) * 255f / 100f;
        var db = (adj.ShadowB * shadow + adj.MidtoneB * midtone + adj.HighlightB * highlight) * 255f / 100f;
        bo = C(b + db);
        go = C(g + dg);
        ro = C(r + dr);
    }

    private static void TransformBinarize(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        var thr = (int)Math.Clamp(adj.Threshold, 0f, 255f);
        var lum = (int)(r * 299 + g * 587 + b * 114) / 1000;
        bo = go = ro = (byte)(lum >= thr ? 255 : 0);
    }

    private static void TransformGradientMap(AdjustmentLayerData adj, byte b, byte g, byte r,
        out byte bo, out byte go, out byte ro)
    {
        var lum = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        SampleGradient(adj.GradientStops, lum / 255f, out var rf, out var gf, out var bf);
        bo = C(bf * 255f);
        go = C(gf * 255f);
        ro = C(rf * 255f);
    }

    private static void SampleGradient(float[] stops, float t, out float rf, out float gf, out float bf)
    {
        int stopCount = stops.Length / 4;
        if (stopCount < 2)
        {
            rf = gf = bf = t;
            return;
        }

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
        rf = stops[lo * 4 + 1] + (stops[hi * 4 + 1] - stops[lo * 4 + 1]) * f;
        gf = stops[lo * 4 + 2] + (stops[hi * 4 + 2] - stops[lo * 4 + 2]) * f;
        bf = stops[lo * 4 + 3] + (stops[hi * 4 + 3] - stops[lo * 4 + 3]) * f;
    }
}
