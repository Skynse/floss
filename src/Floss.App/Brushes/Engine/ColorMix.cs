using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes.Engine;

public sealed partial class BrushEngine
{
    private static bool UsesLiveSmudgePickup(BrushPreset brush)
        => brush.ColorMix && brush.SmudgeMode != SmudgeMode.Blend;

    private void RasterizeColorMixBatch(
        DrawingLayer layer,
        BrushPreset brush,
        ActiveStroke stroke,
        PixelRegion dirty,
        PixelSampler? sampleSource,
        TileReader? tileReader,
        long started)
    {
        PrepareStampColors(layer, brush, stroke, sampleSource);
        if (dirty.IsEmpty || _stamps.Count == 0)
        {
            LastStats = BrushRenderStats.From("Empty", _stamps.Count, 0, 0, 0, ElapsedMs(started));
            return;
        }

        RenderCurrentStamps(layer, stroke, brush, dirty);
    }

    // Smear and Running Color must read the LIVE layer and render stamp-by-stamp
    // so each dab picks up pigment left by the previous dab. Batch sampling from
    // the pre-stroke snapshot produces disconnected circular blobs — the #1 reason
    // smudge looked nothing like CSP even with matching settings.
    private void RasterizeColorMixSequential(
        DrawingLayer layer,
        BrushPreset brush,
        ActiveStroke stroke,
        PixelRegion dirty,
        long started)
    {
        _lastRasterPath = brush.SmudgeMode == SmudgeMode.Smear ? "SpatialSmear" : "SmudgeSequential";
        if (_stamps.Count == 0)
        {
            LastStats = BrushRenderStats.From("Empty", 0, 0, 0, 0, ElapsedMs(started));
            return;
        }

        var allStamps = _stamps.ToArray();
        _stamps.Clear();
        _stampColors.Clear();
        var useSpatialSmear = brush.SmudgeMode == SmudgeMode.Smear;

        try
        {
            for (var i = 0; i < allStamps.Length; i++)
            {
                var stamp = allStamps[i];
                var stampDirty = StampBounds(stamp);

                if (useSpatialSmear)
                {
                    try
                    {
                        var smearResult = TryRenderSpatialSmearStamp(layer, stroke, brush, stamp, stampDirty);
                        if (smearResult == SpatialSmearResult.Rendered)
                        {
                            layer.Pixels.PruneRegion(stampDirty);
                        }
                        else if (smearResult == SpatialSmearResult.Failed)
                        {
                                                _stampColors.Clear();
                            _stamps.Add(stamp);
                            PrepareOneStampColor(layer, brush, stroke, stamp, sampleSource: null);
                            if (_stampColors.Count > 0 && _stampColors[0].Alpha > 0)
                                RenderCurrentStamps(layer, stroke, brush, stampDirty);
                            _stamps.Clear();
                            _stampColors.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "BrushEngine.TryRenderSpatialSmearStamp", flushToDisk: true);
                    }
                    continue;
                }

                        _stampColors.Clear();
                _stamps.Add(stamp);
                PrepareOneStampColor(layer, brush, stroke, stamp, sampleSource: null);
                if (_stampColors.Count == 0 || _stampColors[0].Alpha == 0)
                {
                    _stamps.Clear();
                    continue;
                }

                try
                {
                    RenderCurrentStamps(layer, stroke, brush, stampDirty);
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex, "BrushEngine.RenderCurrentStamps(SmudgeSequential)", flushToDisk: true);
                }
                _stamps.Clear();
                _stampColors.Clear();
            }
        }
        finally
        {
        }

        _stamps.AddRange(allStamps);
    }
    private const int MaxMixBlurRadius = 48;

    private void PrepareStampColors(DrawingLayer layer, BrushPreset brush, ActiveStroke stroke, PixelSampler? sampleSource)
    {
        _stampColors.Clear();
        try
        {
            for (var i = 0; i < _stamps.Count; i++)
                PrepareOneStampColor(layer, brush, stroke, _stamps[i], sampleSource);
        }
        finally
        {
        }
    }

    private void PrepareOneStampColor(
        DrawingLayer layer,
        BrushPreset brush,
        ActiveStroke stroke,
        StampSample stamp,
        PixelSampler? sampleSource)
    {
        var amount = ComputeEffectivePaintAmount(brush);
        var density = Math.Clamp((float)brush.DensityOfPaint, 0f, 1f);
        var stretch = Math.Clamp((float)brush.ColorStretch, 0f, 1f);
        var stretchCarry = MinStretchCarry + (MaxStretchCarry - MinStretchCarry) * stretch;
        var blur = Math.Clamp((float)brush.BlurAmount, 0f, 1f);
        var mixingMode = brush.MixingMode;

        var existing = SampleExistingPigment(layer, stroke, stamp, blur, sampleSource, mixingMode);
        if (existing.Alpha == 0 && stroke.CarriedColor.Alpha == 0 && amount <= 0f)
        {
            _stampColors.Add(SKColors.Transparent);
            return;
        }

        var pigment = existing.Alpha > 0
            ? MixColors(existing, stroke.CarriedColor, stretch, mixingMode)
            : stroke.CarriedColor;
        var baseColor = stroke.BaseColor;
        var mixedRgb = MixRgb(pigment, baseColor, amount, mixingMode);
        var alpha = ComputeSmudgeDepositionAlpha(brush, pigment, baseColor, amount, density);
        if (alpha == 0)
        {
            _stampColors.Add(SKColors.Transparent);
        }
        else
        {
            _stampColors.Add(new SKColor(mixedRgb.Red, mixedRgb.Green, mixedRgb.Blue, alpha));
        }

        stroke.CarriedColor = brush.SmudgeMode switch
        {
            SmudgeMode.Blend => SKColors.Transparent,
            SmudgeMode.Smudge => existing.Alpha > 0
                ? DecayAlpha(MixColors(stroke.CarriedColor, existing, 1f, mixingMode), stretchCarry)
                : stroke.CarriedColor,
            _ => DecayAlpha(
                existing.Alpha > 0
                    ? MixColors(MixColors(stroke.CarriedColor, existing, 1f, mixingMode), baseColor, amount, mixingMode)
                    : MixColors(stroke.CarriedColor, baseColor, amount, mixingMode),
                existing.Alpha > 0 ? stretchCarry : 1f)
        };
    }

    private static byte ComputeSmudgeDepositionAlpha(
        BrushPreset brush, SKColor pigment, SKColor baseColor, float amount, float density)
    {
        if (pigment.Alpha == 0 && amount <= 0f)
            return 0;

        return brush.SmudgeMode switch
        {
            // Amount/density gate NEW brush paint. Picked-up pigment smears at
            // full strength when amount=0 — matching CSP Running Color / Smear.
            SmudgeMode.Smear when amount <= 0f => pigment.Alpha,
            SmudgeMode.Smear => (byte)Math.Clamp(
                Math.Max(pigment.Alpha, baseColor.Alpha * amount),
                0, 255),
            SmudgeMode.Smudge when amount <= 0f => pigment.Alpha,
            SmudgeMode.Smudge => MixAlpha(pigment.Alpha, baseColor.Alpha, Math.Max(amount, density)),
            _ => amount <= 0f
                ? pigment.Alpha
                : MixAlpha(pigment.Alpha, baseColor.Alpha, density)
        };
    }

    private SKColor SampleExistingPigment(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        float blur,
        PixelSampler? sampleSource,
        MixingMode mixingMode)
        => SampleExistingPigmentCore(layer, stroke, stamp, blur, sampleSource, mixingMode, () =>
        {
            if (stroke.TryGetCachedDab(stamp, out var dab))
                return SamplePigmentUnderDab(layer, stroke, stamp, dab, sampleSource);

            SamplePixel(layer, sampleSource, (int)stamp.X, (int)stamp.Y, out var sb, out var sg, out var sr, out var sa);
            return sa > 0 ? new SKColor(sr, sg, sb, sa) : SKColors.Transparent;
        });

    private SKColor SampleExistingPigmentCore(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        float blur,
        PixelSampler? sampleSource,
        MixingMode mixingMode,
        Func<SKColor> sampleFootprint)
    {
        var referenceSize = MathF.Max(8f, stamp.Size);
        if (blur > 0.001f)
        {
            var blurred = SampleHaltonDullingColor(layer, stroke, stamp, blur, referenceSize, sampleSource);
            if (blur >= 0.85f)
                return blurred.Alpha > 0 ? blurred : SKColors.Transparent;

            var footprint = sampleFootprint();
            if (footprint.Alpha == 0 && blurred.Alpha > 0)
                return blurred;

            return MixColors(footprint, blurred, blur, mixingMode);
        }

        return sampleFootprint();
    }

    // Krita squares color rate before applying opacity so low amounts feel
    // genuinely zero — matching CSP/Krita slider response.
    private static float ComputeEffectivePaintAmount(BrushPreset brush)
    {
        var raw = Math.Clamp((float)brush.AmountOfPaint, 0f, 1f)
            * Math.Clamp((float)brush.ColorLoad, 0f, 1f);
        return raw * raw;
    }

    private static float ComputeSmearRate(BrushPreset brush, float stampOpacity)
    {
        var stretch = Math.Clamp((float)brush.ColorStretch, 0f, 1f);
        return stampOpacity * (0.2f + 0.8f * stretch);
    }

    private static float Halton(int index, int basis)
    {
        var result = 0f;
        var f = 1f / basis;
        var i = index;
        while (i > 0)
        {
            result += f * (i % basis);
            i /= basis;
            f /= basis;
        }
        return result;
    }

    private static int MaxRgbDifference(SKColor a, SKColor b)
        => Math.Max(Math.Abs(a.Red - b.Red),
            Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    private static float TryGetMaskWeight(ActiveStroke.CachedDab dab, int cx, int cy, int px, int py)
    {
        var localX = px - (cx + dab.OffsetX);
        var localY = py - (cy + dab.OffsetY);
        if ((uint)localX >= (uint)dab.LogicalWidth || (uint)localY >= (uint)dab.LogicalHeight || dab.Mask.IsEmpty)
            return -1f;
        return SampleMaskAlpha(dab, localX, localY) / 255f;
    }

    // Krita-style dulling pickup: Halton-weighted samples inside the dab
    // footprint, converging early when the estimate stabilizes.
    private SKColor SampleHaltonDullingColor(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        float blur,
        float referenceSize,
        PixelSampler? sampleSource)
    {
        var cx = (int)MathF.Round(stamp.X);
        var cy = (int)MathF.Round(stamp.Y);
        var sampleRadius = Math.Clamp(blur, 0f, 1f);

        int srcLeft, srcTop, srcW, srcH;
        ActiveStroke.CachedDab? dab = null;
        if (stroke.TryGetCachedDab(stamp, out var cachedDab))
        {
            dab = cachedDab;
            srcLeft = cx + cachedDab.OffsetX;
            srcTop = cy + cachedDab.OffsetY;
            srcW = cachedDab.LogicalWidth;
            srcH = cachedDab.LogicalHeight;
        }
        else
        {
            var radius = MathF.Min(MaxMixBlurRadius, MathF.Max(1f, blur * referenceSize * 0.85f));
            var iradius = (int)MathF.Ceiling(radius);
            srcLeft = cx - iradius;
            srcTop = cy - iradius;
            srcW = srcH = iradius * 2 + 1;
        }

        var currentRadius = sampleRadius;
        SKColor result = SKColors.Transparent;
        do
        {
            var blow = sampleRadius > 0f ? 0.5f * (currentRadius - 1f) : 0f;
            var sampleLeft = srcLeft - (int)MathF.Floor(blow);
            var sampleTop = srcTop - (int)MathF.Floor(blow);
            var sampleRight = srcLeft + srcW + (int)MathF.Ceiling(blow);
            var sampleBottom = srcTop + srcH + (int)MathF.Ceiling(blow);
            var sampleW = Math.Max(1, sampleRight - sampleLeft);
            var sampleH = Math.Max(1, sampleBottom - sampleTop);
            var numPixels = sampleW * sampleH;
            var minSamples = Math.Min(numPixels, Math.Clamp((int)MathF.Round(0.02f * numPixels), 64, 256));

            float accR = 0f, accG = 0f, accB = 0f, accA = 0f, colorWeightSum = 0f, alphaWeightSum = 0f;
            var hIndex2 = 1;
            var hIndex3 = 1;
            var restartWithBiggerRadius = false;

            for (var i = 0; i < minSamples; i++)
            {
                var localX = sampleW <= 1 ? 0 : (int)(Halton(hIndex2++, 2) * (sampleW - 1));
                var localY = sampleH <= 1 ? 0 : (int)(Halton(hIndex3++, 3) * (sampleH - 1));
                var px = sampleLeft + localX;
                var py = sampleTop + localY;
                SamplePixel(layer, sampleSource, px, py, out var b, out var g, out var r, out var a);

                var weight = 1f;
                if (dab != null)
                {
                    weight = TryGetMaskWeight(dab, cx, cy, px, py);
                    if (weight < 0f)
                    {
                        restartWithBiggerRadius = true;
                        weight = 0f;
                    }
                    else if (weight <= 0f)
                    {
                        restartWithBiggerRadius = true;
                    }
                }

                if (weight <= 0f) continue;

                alphaWeightSum += weight;
                accA += a * weight;
                if (a == 0) continue;

                var colorWeight = weight * (a / 255f);
                accR += r * colorWeight;
                accG += g * colorWeight;
                accB += b * colorWeight;
                colorWeightSum += colorWeight;
            }

            if (colorWeightSum > 0.0001f)
            {
                result = new SKColor(
                    (byte)Math.Clamp(accR / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accG / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accB / colorWeightSum, 0, 255),
                    alphaWeightSum > 0.0001f
                        ? (byte)Math.Clamp(accA / alphaWeightSum, 0, 255)
                        : (byte)0);
            }
            else
            {
                SamplePixel(layer, sampleSource, cx, cy, out var cb, out var cg, out var cr, out var ca);
                result = ca > 0 ? new SKColor(cr, cg, cb, ca) : SKColors.Transparent;
            }

            var lastResult = result;
            var samplesLeft = numPixels - minSamples;
            while (samplesLeft > 0 && colorWeightSum > 0.0001f)
            {
                var batchSize = Math.Min(samplesLeft, 16);
                for (var i = 0; i < batchSize; i++)
                {
                    var localX = sampleW <= 1 ? 0 : (int)(Halton(hIndex2++, 2) * (sampleW - 1));
                    var localY = sampleH <= 1 ? 0 : (int)(Halton(hIndex3++, 3) * (sampleH - 1));
                    var px = sampleLeft + localX;
                    var py = sampleTop + localY;
                    SamplePixel(layer, sampleSource, px, py, out var b, out var g, out var r, out var a);

                    var weight = 1f;
                    if (dab != null)
                    {
                        weight = TryGetMaskWeight(dab, cx, cy, px, py);
                        if (weight < 0f || weight <= 0f)
                        {
                            restartWithBiggerRadius = true;
                            continue;
                        }
                    }

                    alphaWeightSum += weight;
                    accA += a * weight;
                    if (a == 0) continue;
                    var colorWeight = weight * (a / 255f);
                    accR += r * colorWeight;
                    accG += g * colorWeight;
                    accB += b * colorWeight;
                    colorWeightSum += colorWeight;
                }

                result = new SKColor(
                    (byte)Math.Clamp(accR / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accG / colorWeightSum, 0, 255),
                    (byte)Math.Clamp(accB / colorWeightSum, 0, 255),
                    alphaWeightSum > 0.0001f
                        ? (byte)Math.Clamp(accA / alphaWeightSum, 0, 255)
                        : (byte)0);

                if (MaxRgbDifference(result, lastResult) <= 2)
                    break;

                lastResult = result;
                samplesLeft -= batchSize;
            }

            if (!restartWithBiggerRadius || currentRadius >= 1f)
                break;

            currentRadius = Math.Min(1f, currentRadius + 0.05f);
        } while (true);

        return result;
    }
    private enum SpatialSmearResult { SkippedFirstDab, Rendered, Failed }

    // Krita smearing mode: each dab reads pixels from the previous dab rect
    // translated by cursor movement, then optionally deposits new paint.
    private unsafe SpatialSmearResult TryRenderSpatialSmearStamp(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        StampSample stamp,
        PixelRegion stampDirty)
    {
        if (stamp.Opacity <= 0f || stamp.Size <= 0f)
            return SpatialSmearResult.Failed;

        if (!stroke.TryGetCachedDab(stamp, out var dab) || dab.Mask.IsEmpty)
            return SpatialSmearResult.Failed;

        var maskPixels = dab.Mask.GetPixels();
        if (maskPixels == IntPtr.Zero)
            return SpatialSmearResult.Failed;

        var cx = (int)MathF.Round(stamp.X);
        var cy = (int)MathF.Round(stamp.Y);
        var left = cx + dab.OffsetX;
        var top = cy + dab.OffsetY;
        var right = left + dab.LogicalWidth;
        var bottom = top + dab.LogicalHeight;
        var centerX = (left + right) * 0.5f;
        var centerY = (top + bottom) * 0.5f;

        if (stroke.SmearFirstDabPending)
        {
            stroke.LastSmearCenterX = centerX;
            stroke.LastSmearCenterY = centerY;
            stroke.SmearFirstDabPending = false;
            return SpatialSmearResult.SkippedFirstDab;
        }

        var offsetX = (int)MathF.Round(stroke.LastSmearCenterX - centerX);
        var offsetY = (int)MathF.Round(stroke.LastSmearCenterY - centerY);
        stroke.LastSmearCenterX = centerX;
        stroke.LastSmearCenterY = centerY;

        var smearRate = ComputeSmearRate(brush, stamp.Opacity);
        var paintRate = ComputeEffectivePaintAmount(brush) * stamp.Opacity;
        var density = Math.Clamp((float)brush.DensityOfPaint, 0f, 1f);
        if (paintRate > 0f)
            paintRate *= density;

        var blurSoftening = Math.Clamp((float)brush.BlurAmount, 0f, 1f);
        var applyBlurSoften = blurSoftening >= 0.35f;
        Span<byte> softenLut = stackalloc byte[256];
        if (applyBlurSoften)
        {
            var soften = (blurSoftening - 0.35f) / 0.65f;
            var exponent = 1f - soften * 0.75f;
            for (var v = 0; v < 256; v++)
            {
                var a = v / 255f;
                softenLut[v] = (byte)Math.Clamp((int)(MathF.Pow(a, exponent) * 255f + 0.5f), 0, 255);
            }
        }

        var baseColor = stroke.BaseColor;
        var useFastMaskPath = !dab.IsScaled && !applyBlurSoften;
        var maskPtr = useFastMaskPath ? (byte*)maskPixels.ToPointer() : null;
        var maskStride = dab.Mask.RowBytes;
        var renderedAny = false;

        var pxMinX = Math.Max(left, stampDirty.X);
        var pxMinY = Math.Max(top, stampDirty.Y);
        var pxMaxX = Math.Min(right, stampDirty.Right);
        var pxMaxY = Math.Min(bottom, stampDirty.Bottom);
        if (pxMinX >= pxMaxX || pxMinY >= pxMaxY)
            return SpatialSmearResult.Failed;

        const int tsz = TiledPixelBuffer.TileSize;
        _smearSnapshots.Clear();
        var srcSnapshots = _smearSnapshots;
        var srcMinX = pxMinX + offsetX;
        var srcMinY = pxMinY + offsetY;
        var srcMaxX = pxMaxX + offsetX;
        var srcMaxY = pxMaxY + offsetY;
        var needsPaint = paintRate > 0f && baseColor.Alpha > 0;

        var srcFirstTx = FloorDiv(srcMinX, tsz);
        var srcFirstTy = FloorDiv(srcMinY, tsz);
        var srcLastTx = FloorDiv(srcMaxX - 1, tsz);
        var srcLastTy = FloorDiv(srcMaxY - 1, tsz);
        for (var ty = srcFirstTy; ty <= srcLastTy; ty++)
        {
            for (var tx = srcFirstTx; tx <= srcLastTx; tx++)
            {
                int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                if (srcSnapshots.ContainsKey(key)) continue;
                var raw = layer.Pixels.GetTileOrNull(tx, ty);
                if (raw == null)
                {
                    srcSnapshots[key] = null;
                    continue;
                }

                var copy = new byte[raw.Length];
                Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
                srcSnapshots[key] = copy;
            }
        }

        layer.Pixels.EnterPixelWriteLock();
        try
        {
            var firstTx = FloorDiv(pxMinX, tsz);
            var firstTy = FloorDiv(pxMinY, tsz);
            var lastTx = FloorDiv(pxMaxX - 1, tsz);
            var lastTy = FloorDiv(pxMaxY - 1, tsz);

            for (var ty = firstTy; ty <= lastTy; ty++)
            {
                var tilePixY = ty * tsz;
                for (var tx = firstTx; tx <= lastTx; tx++)
                {
                    var tilePixX = tx * tsz;
                    var dstTile = layer.Pixels.GetOrCreateRawTile(tx, ty);

                    var tilePxMinX = Math.Max(pxMinX, tilePixX);
                    var tilePxMinY = Math.Max(pxMinY, tilePixY);
                    var tilePxMaxX = Math.Min(pxMaxX, tilePixX + tsz);
                    var tilePxMaxY = Math.Min(pxMaxY, tilePixY + tsz);

                    for (var py = tilePxMinY; py < tilePxMaxY; py++)
                    {
                        var localY = py - top;
                        var maskRow = useFastMaskPath ? maskPtr! + localY * maskStride : null;
                        var rowBase = (py - tilePixY) * tsz * 4;

                        for (var px = tilePxMinX; px < tilePxMaxX; px++)
                        {
                            var maskA = useFastMaskPath
                                ? maskRow![px - left]
                                : SampleMaskAlpha(dab, px - left, localY);
                            if (maskA == 0) continue;
                            if (applyBlurSoften)
                                maskA = softenLut[maskA];
                            if (maskA == 0) continue;

                            var dstOffset = rowBase + (px - tilePixX) * 4;
                            var db = dstTile[dstOffset];
                            var dg = dstTile[dstOffset + 1];
                            var dr = dstTile[dstOffset + 2];
                            var da = dstTile[dstOffset + 3];

                            var srcX = px + offsetX;
                            var srcY = py + offsetY;
                            byte sb = 0, sg = 0, sr = 0, sa = 0;
                            if (srcX >= 0 && srcY >= 0 && srcX < layer.Width && srcY < layer.Height)
                            {
                                var srcTx = FloorDiv(srcX, tsz);
                                var srcTy = FloorDiv(srcY, tsz);
                                int srcKey = (srcTx & 0xFFFF) | ((srcTy & 0xFFFF) << 16);
                                srcSnapshots.TryGetValue(srcKey, out var srcTile);
                                ReadPixelFromTile(srcTile, srcTx * tsz, srcTy * tsz, srcX, srcY, out sb, out sg, out sr, out sa);
                            }

                            if (sa == 0 && da == 0 && !needsPaint)
                                continue;

                            var b = db;
                            var g = dg;
                            var r = dr;
                            var a = da;
                            var changed = false;

                            if (sa > 0 && smearRate > 0f)
                            {
                                var smearA = (int)(maskA * smearRate + 0.5f);
                                if (smearA > 0)
                                {
                                    if (smearA > 255) smearA = 255;
                                    AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a,
                                        sb, sg, sr, (byte)smearA, layer.IsAlphaLocked, SKBlendMode.SrcOver);
                                    changed = true;
                                }
                            }

                            if (needsPaint)
                            {
                                var paintA = (int)(maskA * paintRate + 0.5f);
                                if (paintA > 0)
                                {
                                    if (paintA > 255) paintA = 255;
                                    AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a,
                                        baseColor.Blue, baseColor.Green, baseColor.Red, (byte)paintA,
                                        layer.IsAlphaLocked, SKBlendMode.SrcOver);
                                    changed = true;
                                }
                            }

                            if (changed)
                            {
                                dstTile[dstOffset] = b;
                                dstTile[dstOffset + 1] = g;
                                dstTile[dstOffset + 2] = r;
                                dstTile[dstOffset + 3] = a;
                                renderedAny = true;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            layer.Pixels.ExitPixelWriteLock();
        }

        return renderedAny ? SpatialSmearResult.Rendered : SpatialSmearResult.Failed;
    }
    private SKColor SamplePigmentUnderDab(
        DrawingLayer layer,
        ActiveStroke stroke,
        StampSample stamp,
        ActiveStroke.CachedDab dab,
        PixelSampler? sampleSource)
    {
        // Sample at a sparse, fixed grid around the stamp centre rather than
        // walking the entire dab mask. The previous implementation iterated
        // ~width*height/step² mask pixels per stamp; for a 230² dab that was
        // ~3300 samples just to compute one averaged pickup colour, repeated
        // for every stamp in the batch. A small fixed grid (~37 samples)
        // gives perceptually identical results because the spatial weighting
        // already concentrates contribution near the centre.
        var cx = (int)MathF.Round(stamp.X);
        var cy = (int)MathF.Round(stamp.Y);
        var radius = MathF.Max(1f, stamp.Size * 0.35f);
        var iradius = (int)MathF.Ceiling(radius);
        // Coarse step: ~7 samples across the diameter regardless of size.
        var step = Math.Max(1, iradius / 3);

        float accR = 0, accG = 0, accB = 0, accA = 0, colorWeightSum = 0, alphaWeightSum = 0;
        for (var dy = -iradius; dy <= iradius; dy += step)
        {
            for (var dx = -iradius; dx <= iradius; dx += step)
            {
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;

                var weight = 1f - (dist / radius);
                weight *= weight; // bias toward centre
                SamplePixel(layer, sampleSource, cx + dx, cy + dy, out var b, out var g, out var r, out var a);
                alphaWeightSum += weight;
                accA += a * weight;
                if (a == 0) continue;

                var colorWeight = weight * (a / 255f);
                accR += r * colorWeight;
                accG += g * colorWeight;
                accB += b * colorWeight;
                colorWeightSum += colorWeight;
            }
        }

        if (colorWeightSum > 0.0001f)
        {
            var avgAlpha = alphaWeightSum > 0.0001f
                ? (byte)Math.Clamp(accA / alphaWeightSum, 0, 255)
                : (byte)0;
            return new SKColor(
                (byte)Math.Clamp(accR / colorWeightSum, 0, 255),
                (byte)Math.Clamp(accG / colorWeightSum, 0, 255),
                (byte)Math.Clamp(accB / colorWeightSum, 0, 255),
                avgAlpha);
        }

        SamplePixel(layer, sampleSource, cx, cy, out var fb, out var fg, out var fr, out var fa);
        return fa > 0 ? new SKColor(fr, fg, fb, fa) : SKColors.Transparent;
    }

    private static SKColor MixRgb(SKColor from, SKColor to, float t, MixingMode mode)
    {
        if (t <= 0f) return from;
        if (t >= 1f) return to;
        if (mode == MixingMode.Perceptual)
        {
            var fromLch = RgbToLCh(from.Red, from.Green, from.Blue);
            var toLch = RgbToLCh(to.Red, to.Green, to.Blue);
            var mixed = new Vector3(
                fromLch.X + (toLch.X - fromLch.X) * t,
                fromLch.Y + (toLch.Y - fromLch.Y) * t,
                MixHue(fromLch.Z, toLch.Z, t));
            var (r, g, b) = LChToRgb(mixed);
            return new SKColor((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), from.Alpha);
        }

        return new SKColor(
            (byte)Math.Clamp(from.Red * (1f - t) + to.Red * t, 0, 255),
            (byte)Math.Clamp(from.Green * (1f - t) + to.Green * t, 0, 255),
            (byte)Math.Clamp(from.Blue * (1f - t) + to.Blue * t, 0, 255),
            from.Alpha);
    }

    private static SKColor MixColors(SKColor from, SKColor to, float t, MixingMode mode)
    {
        if (t <= 0f) return from;
        if (t >= 1f) return to;
        if (mode == MixingMode.Perceptual)
        {
            var fromLch = RgbToLCh(from.Red, from.Green, from.Blue);
            var toLch = RgbToLCh(to.Red, to.Green, to.Blue);
            var mixed = new Vector3(
                fromLch.X + (toLch.X - fromLch.X) * t,
                fromLch.Y + (toLch.Y - fromLch.Y) * t,
                MixHue(fromLch.Z, toLch.Z, t));
            var (r, g, b) = LChToRgb(mixed);
            var alpha = from.Alpha + (to.Alpha - from.Alpha) * t;
            return new SKColor((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(alpha, 0, 255));
        }

        return new SKColor(
            (byte)Math.Clamp(from.Red * (1f - t) + to.Red * t, 0, 255),
            (byte)Math.Clamp(from.Green * (1f - t) + to.Green * t, 0, 255),
            (byte)Math.Clamp(from.Blue * (1f - t) + to.Blue * t, 0, 255),
            (byte)Math.Clamp(from.Alpha * (1f - t) + to.Alpha * t, 0, 255));
    }

    private static byte MixAlpha(byte from, byte to, float t)
        => (byte)Math.Clamp(from * (1f - t) + to * t, 0, 255);

    private static SKColor DecayAlpha(SKColor color, float persistence)
        => new(color.Red, color.Green, color.Blue, (byte)Math.Clamp(color.Alpha * persistence, 0, 255));

    // The sample buffer caches the BEFORE-stroke pixel state across the dirty
    // region so that PrepareStampColors can do hundreds of thousands of pixel
    // reads as O(1) array indexing rather than per-call dictionary+tile lookups.
    // Margin extends the buffer to cover blur-kernel reach outside dirty bounds.
    private const int SampleBufferMargin = 64;
    private const long MaxSampleBufferPixels = 6L * 1024 * 1024;

    private void SamplePixel(DrawingLayer layer, PixelSampler? sampleSource, int x, int y, out byte b, out byte g, out byte r, out byte a)
    {
        if (sampleSource != null)
        {
            sampleSource(x, y, out b, out g, out r, out a);
            return;
        }

        layer.Pixels.GetPixel(x, y, out b, out g, out r, out a);
    }

    // Simplified RGB <-> LCh conversion for perceptual mixing
    private static Vector3 RgbToLCh(float r, float g, float b)
    {
        float xr = r / 255f;
        float xg = g / 255f;
        float xb = b / 255f;

        float R = SrgbToLinear(xr);
        float G = SrgbToLinear(xg);
        float B = SrgbToLinear(xb);

        float X = R * 0.4124564f + G * 0.3575761f + B * 0.1804375f;
        float Y = R * 0.2126729f + G * 0.7151522f + B * 0.0721750f;
        float Z = R * 0.0193339f + G * 0.1191920f + B * 0.9503041f;

        float fx = FAST_Cbrt(X);
        float fy = FAST_Cbrt(Y);
        float fz = FAST_Cbrt(Z);

        float L = 116f * fy - 16f;
        float A = 500f * (fx - fy);
        float B_ = 200f * (fy - fz);

        float C = MathF.Sqrt(A * A + B_ * B_);
        float H = MathF.Atan2(B_, A);

        return new Vector3(L, C, H);
    }

    private static (float R, float G, float B) LChToRgb(Vector3 lch)
    {
        float L = lch.X;
        float C = lch.Y;
        float H = lch.Z;

        float A = C * MathF.Cos(H);
        float B_ = C * MathF.Sin(H);

        float fy = (L + 16f) / 116f;
        float fx = A / 500f + fy;
        float fz = fy - B_ / 200f;

        float X = FAST_Cube(fx);
        float Y = FAST_Cube(fy);
        float Z = FAST_Cube(fz);

        float R_ = X * 3.2404542f + Y * -1.5371385f + Z * -0.4985314f;
        float G_ = X * -0.9692660f + Y * 1.8760108f + Z * 0.0415560f;
        float B__ = X * 0.0556434f + Y * -0.2040259f + Z * 1.0572252f;

        float r = LinearToSrgb(R_);
        float g = LinearToSrgb(G_);
        float b = LinearToSrgb(B__);

        return (r * 255f, g * 255f, b * 255f);
    }

    private static float MixHue(float h1, float h2, float t)
    {
        float diff = h2 - h1;
        if (diff > MathF.PI) diff -= MathF.Tau;
        else if (diff < -MathF.PI) diff += MathF.Tau;
        return h1 + diff * t;
    }
}
