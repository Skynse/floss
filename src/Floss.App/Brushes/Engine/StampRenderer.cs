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
    private unsafe void RenderCurrentStamps(DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, PixelRegion dirty)
    {
        var primaryTip = stroke.TipFor(0);
        if (UsesProceduralStampEvaluation(brush, primaryTip, _stampColors.Count))
        {
            RasterizeStampsDirect(layer, stroke, brush, dirty);
            return;
        }

        if (SupportsCpuRasterBlendMode(brush.BlendMode))
        {
            RasterizeNonProceduralDabsTileMajor(layer, stroke, brush, brush.BlendMode, dirty);
            layer.Pixels.PruneRegion(dirty);
            return;
        }

        _lastRasterPath = "SkiaFallback";
        RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, canvas));
    }

    public PixelRegion RasterizeDab(
        DrawingLayer layer,
        BrushPreset brush,

        CanvasInputSample sample,
        double velocity,
        PixelSampler? sampleSource = null)
    {
        lock (_gate)
        {
            EnsureStroke(brush, sample);
            var stroke = _activeStroke!;
            _stamps.Clear();
            _stampColors.Clear();
    
            var velocity01 = (float)Math.Clamp(velocity / 5000.0, 0, 1);
            var sp = BuildStrokePoint(stroke, sample, velocity01);
            var stamp = CreateStamp(stroke, brush, sp);
            _stamps.Add(stamp);

            if (brush.ColorMix)
            {
                try
                {
                    try
                    {
                        PrepareStampColors(layer, brush, stroke, sampleSource);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "BrushEngine.PrepareStampColors(Dab)", flushToDisk: true);
                        _stampColors.Clear();
                    }

                    var dirty = StampBounds(stamp);
                    if (dirty.IsEmpty) return PixelRegion.Empty;

                    try
                    {
                        RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, canvas));
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write(ex, "BrushEngine.RasterizeDab", flushToDisk: true);
                    }
                    return dirty;
                }
                finally
                {
                }
            }

            var dabDirty = StampBounds(stamp);
            if (dabDirty.IsEmpty) return PixelRegion.Empty;

            try
            {
                RenderWithSkiaOnLayer(layer, dabDirty, canvas => RenderPreparedStamps(stroke, canvas));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "BrushEngine.RasterizeDab", flushToDisk: true);
            }
            return dabDirty;
        }
    }


    /// <summary>Krita-style direct dab compositing: render each dab to a
    /// dab-sized buffer (Alpha8 mask × color), bucket by tile, composite
    /// directly onto layer tiles. No scratch buffer, no double-write.</summary>
    private unsafe void RasterizeNonProceduralDabsTileMajor(
        DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, SKBlendMode blendMode, PixelRegion dirty)
    {
        var blurSoftening = Math.Clamp((float)brush.BlurAmount, 0f, 1f);
        byte* softenLut = stackalloc byte[256];
        bool applyBlurSoften = blurSoftening >= 0.35f;
        if (applyBlurSoften)
        {
            var soften = (blurSoftening - 0.35f) / 0.65f;
            var exponent = 1f - soften * 0.75f;
            for (int v = 0; v < 256; v++)
            {
                var a = v / 255f;
                var transformed = MathF.Pow(a, exponent);
                softenLut[v] = (byte)Math.Clamp((int)(transformed * 255f + 0.5f), 0, 255);
            }
        }

        if (stroke.HasAnyColorTip && _stampColors.Count == 0)
        {
            RasterizeColorTipsTileMajor(layer, stroke, brush, blendMode, dirty);
            return;
        }

        const int tsz = TiledPixelBuffer.TileSize;
        _dabBuckets.Clear();
        var buckets = _dabBuckets;
        buckets.EnsureCapacity(Math.Max(16, Math.Min(_stamps.Count * 4, 4096)));

        try
        {
            for (var i = 0; i < _stamps.Count; i++)
            {
                var stamp = _stamps[i];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                var colorB = stroke.BaseColor.Blue;
                var colorG = stroke.BaseColor.Green;
                var colorR = stroke.BaseColor.Red;
                var colorA = stroke.BaseColor.Alpha;
                if (!UsesMaskOpacity(blendMode) && _stampColors.Count > i)
                {
                    var color = _stampColors[i];
                    if (color.Alpha == 0) continue;
                    colorB = color.Blue;
                    colorG = color.Green;
                    colorR = color.Red;
                    colorA = color.Alpha;
                }

                if (!stroke.TryGetCachedDab(stamp, out var dab))
                {
                    _lastRasterPath = "SkiaFallback";
                    RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, canvas));
                    return;
                }

                _lastCachedDabCount++;
                var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
                var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
                var right = left + dab.LogicalWidth;
                var bottom = top + dab.LogicalHeight;
                if (right <= dirty.X || bottom <= dirty.Y || left >= dirty.Right || top >= dirty.Bottom)
                    continue;

                var placed = new PlacedDab(stamp, dab, left, top, right, bottom, colorB, colorG, colorR, colorA);
                var firstTx = FloorDiv(left, tsz);
                var firstTy = FloorDiv(top, tsz);
                var lastTx = FloorDiv(right - 1, tsz);
                var lastTy = FloorDiv(bottom - 1, tsz);
                for (var ty = firstTy; ty <= lastTy; ty++)
                {
                    for (var tx = firstTx; tx <= lastTx; tx++)
                    {
                        var tileLeft = tx * tsz;
                        var tileTop = ty * tsz;
                        if (right <= tileLeft || bottom <= tileTop || left >= tileLeft + tsz || top >= tileTop + tsz)
                            continue;
                        int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                        if (!buckets.TryGetValue(key, out var list))
                        {
                            list = new List<PlacedDab>(8);
                            buckets.Add(key, list);
                        }
                        list.Add(placed);
                    }
                }
            }

            if (buckets.Count == 0) return;
            _lastTileBucketCount = buckets.Count;
            _lastRasterPath = "TileMajor";

            layer.Pixels.EnterPixelWriteLock();
            try
            {
                foreach (var (key, dabs) in buckets)
                {
                    int tx = (short)key;
                    int ty = (short)(key >> 16);
                    var tile = layer.Pixels.GetOrCreateRawTile(tx, ty);
                    var tilePixX = tx * tsz;
                    var tilePixY = ty * tsz;
                    foreach (var placed in dabs)
                    {
                        ApplyCachedDabToTile(tile, tilePixX, tilePixY, dirty, null,
                            placed, blendMode, 0f, null, 0, 0, 0,
                            layer.IsAlphaLocked, applyBlurSoften ? softenLut : (byte*)null);
                    }
                }
            }
            finally { layer.Pixels.ExitPixelWriteLock(); }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "BrushEngine.RasterizeNonProceduralDabsTileMajor", flushToDisk: true);
            _lastRasterPath = "SkiaFallback";
            RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, canvas));
        }
    }

    private unsafe void RasterizeColorTipsTileMajor(
        DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, SKBlendMode blendMode, PixelRegion dirty)
    {
        const int tsz = TiledPixelBuffer.TileSize;
        _colorDabBuckets.Clear();
        var buckets = _colorDabBuckets;
        buckets.EnsureCapacity(Math.Max(16, Math.Min(_stamps.Count * 4, 4096)));

        try
        {
            for (var i = 0; i < _stamps.Count; i++)
            {
                var stamp = _stamps[i];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                if (!stroke.TryGetCachedColorDab(stamp, out var dab))
                {
                    _lastRasterPath = "SkiaFallback";
                    RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, canvas));
                    return;
                }

                _lastCachedDabCount++;
                var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
                var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
                var right = left + dab.LogicalWidth;
                var bottom = top + dab.LogicalHeight;
                if (right <= dirty.X || bottom <= dirty.Y || left >= dirty.Right || top >= dirty.Bottom)
                    continue;

                var placed = new PlacedColorDab(stamp, dab, left, top, right, bottom);
                var firstTx = FloorDiv(left, tsz);
                var firstTy = FloorDiv(top, tsz);
                var lastTx = FloorDiv(right - 1, tsz);
                var lastTy = FloorDiv(bottom - 1, tsz);
                for (var ty = firstTy; ty <= lastTy; ty++)
                {
                    for (var tx = firstTx; tx <= lastTx; tx++)
                    {
                        var tileLeft = tx * tsz;
                        var tileTop = ty * tsz;
                        if (right <= tileLeft || bottom <= tileTop || left >= tileLeft + tsz || top >= tileTop + tsz)
                            continue;
                        int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                        if (!buckets.TryGetValue(key, out var list))
                        {
                            list = new List<PlacedColorDab>(8);
                            buckets.Add(key, list);
                        }
                        list.Add(placed);
                    }
                }
            }

            if (buckets.Count == 0) return;
            _lastTileBucketCount = buckets.Count;
            _lastRasterPath = "ColorTileMajor";

            layer.Pixels.EnterPixelWriteLock();
            try
            {
                foreach (var (key, dabs) in buckets)
                {
                    int tx = (short)key;
                    int ty = (short)(key >> 16);
                    var tile = layer.Pixels.GetOrCreateRawTile(tx, ty);
                    var tilePixX = tx * tsz;
                    var tilePixY = ty * tsz;
                    foreach (var placed in dabs)
                    {
                        ApplyCachedColorDabToTile(tile, tilePixX, tilePixY, dirty, null,
                            placed, blendMode, 0f, null, 0, 0, 0, layer.IsAlphaLocked);
                    }
                }
            }
            finally { layer.Pixels.ExitPixelWriteLock(); }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "BrushEngine.RasterizeColorTipsTileMajor", flushToDisk: true);
            _lastRasterPath = "SkiaFallback";
            RenderWithSkiaOnLayer(layer, dirty, canvas => RenderPreparedStamps(stroke, canvas));
        }
    }
    private unsafe void RasterizeStampsDirect(DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, PixelRegion dirty)
    {
        var procTip = brush.Tip as ProceduralBrushTip;
        bool isImageTip = brush.Tip is ImageBrushTip or NodeBrushTip { IsDirectImageSampler: true };
        BrushTipNodeGraph? stampGraph = brush.Tip switch
        {
            ProceduralBrushTip p => p.Graph,
            NodeBrushTip n when !n.IsDirectImageSampler => n.Graph,
            _ => null
        };
        bool useGraphEval = stampGraph != null && BrushTipStampContext.CanEvaluate(stampGraph, BrushMaterialTips.ForPreset(brush));
        BrushTipStampFastPath.AlphaAt? fastAlpha = null;
        if (useGraphEval && stampGraph != null &&
            BrushTipStampFastPath.TryCreate(stampGraph, (float)brush.Hardness, out var fast))
            fastAlpha = fast;
        bool isSoft = procTip?.Shape == BrushTipShape.SoftRound;
        bool isCircle = procTip?.Shape is BrushTipShape.Circle or BrushTipShape.SoftRound;
        float aspect = procTip != null && !isCircle
            ? MathF.Max(0.05f, MathF.Min(20f, procTip.AspectRatio))
            : stampGraph?.BuiltInAspectRatio is > 0f and var ar
                ? MathF.Max(0.05f, MathF.Min(20f, ar))
                : 1f;

        _lastRasterPath = fastAlpha != null ? "ProceduralStampFast"
            : useGraphEval ? "ProceduralStamp"
            : "AnalyticalDirect";

        var baseColor = stroke.BaseColor;
        int brushB = baseColor.Blue, brushG = baseColor.Green, brushR = baseColor.Red;
        float baseAlpha = baseColor.Alpha;
        var blendMode = brush.BlendMode;
        float brushGrain = 0f;
        byte* texPx = null!;
        int texW = 0, texH = 0, texStride = 0;
        var grainTable = PrecomputeGrain(dirty, texPx, texW, texH, texStride, brushGrain);

        float brushThickness = MathF.Max(0.01f, MathF.Min(4f, (float)brush.TipThickness));
        bool isHorizontal = brush.TipDirection == BrushTipDirection.Horizontal;
        int baseMaskSize = stroke.BaseMaskSize;
        float halfBms = baseMaskSize * 0.5f;
        const int tsz = TiledPixelBuffer.TileSize;

        layer.Pixels.EnterPixelWriteLock();
        try
        {
            for (int si = 0; si < _stamps.Count; si++)
            {
                var stamp = _stamps[si];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                BrushTipStampContext? stampEval = null;
                SKBitmap? maskBmp = null;
                if (useGraphEval && stampGraph != null && fastAlpha == null)
                    stampEval = new BrushTipStampContext(stampGraph, stamp.Hardness, BrushMaterialTips.ForPreset(brush));
                try
                {

                    float thickMul = MathF.Max(0.01f, MathF.Min(4f, brushThickness * stamp.TipThicknessMultiplier));
                    float scale = stamp.Size / MathF.Max(1f, baseMaskSize);

                    // scaleX/scaleY: pixels per mask-pixel in each axis (used by image tip path)
                    float scaleX = isHorizontal ? scale : scale * thickMul;
                    float scaleY = isHorizontal ? scale * thickMul : scale;
                    if (brush.FlipHorizontal) scaleX = -scaleX;
                    if (brush.FlipVertical) scaleY = -scaleY;

                    // rx/ry: physical half-axes in pixels (used by procedural path + bbox)
                    float maxR = (baseMaskSize * 0.5f - 0.5f) * scale;
                    float rxBase = aspect >= 1f ? maxR : maxR * aspect;
                    float ryBase = aspect >= 1f ? maxR / aspect : maxR;
                    float rx = isHorizontal ? rxBase : rxBase * thickMul;
                    float ry = isHorizontal ? ryBase * thickMul : ryBase;

                    // For image tips the effective half-extent is the full mask half-size × scale
                    float bboxHalfX = isImageTip ? halfBms * MathF.Abs(scaleX) : rx;
                    float bboxHalfY = isImageTip ? halfBms * MathF.Abs(scaleY) : ry;
                    if (bboxHalfX < 0.5f || bboxHalfY < 0.5f) continue;

                    // Always rotate for image tips (texture has directionality); skip for rotationally-symmetric circles
                    bool hasRot = MathF.Abs(stamp.Angle) > 0.1f && (isImageTip || useGraphEval || !isCircle);
                    float cosA = 1f, sinA = 0f;
                    if (hasRot)
                    {
                        float rad = stamp.Angle * MathF.PI / 180f;
                        cosA = MathF.Cos(rad);
                        sinA = MathF.Sin(rad);
                    }

                    float stampOpacity255 = StampOpacity255(blendMode, stamp.Opacity, baseAlpha);
                    if (stampOpacity255 <= 0) continue;

                    // Procedural-only params
                    float hardness = stamp.Hardness;
                    float hardnessRange = 1f - hardness;
                    float h2 = hardness * hardness;
                    bool hardEdge = !isImageTip && hardness >= 0.999f;

                    // Composite strategy — hoist outside pixel loops
                    bool isSrcOver = blendMode == SKBlendMode.SrcOver;
                    bool alphaLocked = layer.IsAlphaLocked;

                    // Grain strategy — precompute base value and nullity
                    float grainBase = 1f - brushGrain;
                    bool hasGrainTable = grainTable != null;
                    bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
                    bool hasTexGrain = hasProceduralGrain && texPx != null;

                    // Image tip: get the cached Alpha8 mask and pin its pixels
                    byte* maskPx = null;
                    int maskStride = 0;
                    if (isImageTip)
                    {
                        maskBmp = stroke.MaskFor(stamp.Hardness);
                        maskPx = (byte*)maskBmp.GetPixels().ToPointer();
                        maskStride = maskBmp.RowBytes;
                    }

                    // Tight bounding box — for rotated image tips use the rotated-rectangle formula
                    float boxHX = hasRot ? (bboxHalfX * MathF.Abs(cosA) + bboxHalfY * MathF.Abs(sinA)) : bboxHalfX;
                    float boxHY = hasRot ? (bboxHalfX * MathF.Abs(sinA) + bboxHalfY * MathF.Abs(cosA)) : bboxHalfY;
                    float margin = 1.5f;
                    int bLeft = (int)MathF.Floor(stamp.X - boxHX - margin);
                    int bTop = (int)MathF.Floor(stamp.Y - boxHY - margin);
                    int bRight = (int)MathF.Ceiling(stamp.X + boxHX + margin);
                    int bBottom = (int)MathF.Ceiling(stamp.Y + boxHY + margin);

                    int firstTx = (int)Math.Floor((double)bLeft / tsz);
                    int firstTy = (int)Math.Floor((double)bTop / tsz);
                    int lastTx = (int)Math.Floor((double)(bRight - 1) / tsz);
                    int lastTy = (int)Math.Floor((double)(bBottom - 1) / tsz);

                    for (int ty = firstTy; ty <= lastTy; ty++)
                    {
                        int tilePixY = ty * tsz;
                        int pxMinY = Math.Max(bTop, tilePixY);
                        int pxMaxY = Math.Min(bBottom, tilePixY + tsz);
                        if (pxMinY >= pxMaxY) continue;

                        for (int tx = firstTx; tx <= lastTx; tx++)
                        {
                            int tilePixX = tx * tsz;
                            int pxMinX = Math.Max(bLeft, tilePixX);
                            int pxMaxX = Math.Min(bRight, tilePixX + tsz);
                            if (pxMinX >= pxMaxX) continue;

                            var tile = layer.Pixels.GetOrCreateRawTile(tx, ty);

                            for (int py = pxMinY; py < pxMaxY; py++)
                            {
                                int ly = py - tilePixY;
                                int rowBase = ly * tsz * 4;
                                float dy = py + 0.5f - stamp.Y;

                                for (int px = pxMinX; px < pxMaxX; px++)
                                {
                                    float dx = px + 0.5f - stamp.X;

                                    // Apply inverse rotation to get brush-local coords
                                    float fdx, fdy;
                                    if (hasRot)
                                    {
                                        fdx = dx * cosA + dy * sinA;
                                        fdy = -dx * sinA + dy * cosA;
                                    }
                                    else { fdx = dx; fdy = dy; }

                                    float alpha;
                                    if (isImageTip)
                                    {
                                        // Map brush-local coords to mask pixel coords via inverse scale
                                        float mx = fdx / scaleX + halfBms;
                                        float my = fdy / scaleY + halfBms;
                                        if (mx < 0f || my < 0f || mx >= baseMaskSize || my >= baseMaskSize) continue;

                                        // Bilinear sample the Alpha8 mask
                                        int ix0 = (int)mx, iy0 = (int)my;
                                        int ix1 = Math.Min(ix0 + 1, baseMaskSize - 1);
                                        int iy1 = Math.Min(iy0 + 1, baseMaskSize - 1);
                                        float fx = mx - ix0, fy = my - iy0;
                                        float a00 = maskPx[iy0 * maskStride + ix0];
                                        float a10 = maskPx[iy0 * maskStride + ix1];
                                        float a01 = maskPx[iy1 * maskStride + ix0];
                                        float a11 = maskPx[iy1 * maskStride + ix1];
                                        alpha = (a00 + (a10 - a00) * fx + (a01 - a00) * fy + (a00 - a10 - a01 + a11) * fx * fy) / 255f;
                                    }
                                    else if (useGraphEval)
                                    {
                                        float u = 0.5f + fdx / (rx * 2f);
                                        float v = 0.5f + fdy / (ry * 2f);
                                        if (u < 0f || v < 0f || u > 1f || v > 1f) continue;
                                        alpha = fastAlpha != null
                                            ? fastAlpha(u, v)
                                            : stampEval!.EvaluateAlpha(u, v);
                                        if (alpha <= 0f) continue;
                                    }
                                    else
                                    {
                                        // Analytical radial alpha — squared comparison avoids Sqrt for core pixels
                                        float ndx = fdx / rx;
                                        float ndy = fdy / ry;
                                        float t2 = ndx * ndx + ndy * ndy;
                                        if (t2 >= 1f) continue;

                                        if (hardEdge)
                                        {
                                            alpha = 1f;
                                        }
                                        else if (t2 <= h2)
                                        {
                                            alpha = 1f;
                                        }
                                        else
                                        {
                                            float t = MathF.Sqrt(t2);
                                            float fade = hardnessRange > 0.001f ? (t - hardness) / hardnessRange : 1f;
                                            var smooth = fade * fade * (3f - 2f * fade);
                                            var exponent = 1f + hardness * 5f;
                                            alpha = isSoft
                                                ? 1f - MathF.Pow(smooth, exponent * 0.7f)
                                                : 1f - MathF.Pow(smooth, exponent);
                                        }
                                    }

                                    if (hasGrainTable)
                                        alpha *= grainTable![(py - dirty.Y) * dirty.Width + (px - dirty.X)];
                                    else if (hasProceduralGrain)
                                    {
                                        if (texPx != null)
                                            alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                                        else
                                            alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                                    }

                                    int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                                    if (stampA <= 0) continue;
                                    if (stampA > 255) stampA = 255;

                                    int lx = px - tilePixX;
                                    int offset = rowBase + lx * 4;
                                    if (isSrcOver)
                                    {
                                        fixed (byte* tp = tile)
                                            Canvas.Engine.SimdPixelOps.StampSrcOver(tp + offset,
                                                (byte)brushB, (byte)brushG, (byte)brushR, stampA, alphaLocked);
                                    }
                                    else
                                    {
                                        WriteCompositeStamp(tile, offset,
                                            (byte)brushB, (byte)brushG, (byte)brushR, (byte)stampA,
                                            alphaLocked, blendMode);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (maskBmp != null)
                        stroke.ReleaseMask(maskBmp);
                    stampEval?.Dispose();
                }
            }
        }
        finally
        {
            layer.Pixels.ExitPixelWriteLock();
        }

        layer.Pixels.PruneRegion(dirty);
    }

    private unsafe bool TryRasterizeCachedDabsTileMajor(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        SKBlendMode blendMode,
        int brushB,
        int brushG,
        int brushR,
        float baseAlpha,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        PixelRegion dirty,
        float[]? grainTable)
    {
        if (_stamps.Count == 0)
            return false;

        const int tsz = TiledPixelBuffer.TileSize;
        _dabBuckets.Clear();
        var buckets = _dabBuckets;
        buckets.EnsureCapacity(Math.Max(16, Math.Min(_stamps.Count * 4, 4096)));

        try
        {
            for (var i = 0; i < _stamps.Count; i++)
            {
                var stamp = _stamps[i];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                var colorB = brushB;
                var colorG = brushG;
                var colorR = brushR;
                var colorA = baseAlpha;
                if (!UsesMaskOpacity(blendMode) && _stampColors.Count > i)
                {
                    var color = _stampColors[i];
                    if (color.Alpha == 0) continue;
                    colorB = color.Blue;
                    colorG = color.Green;
                    colorR = color.Red;
                    colorA = color.Alpha;
                }

                if (!stroke.TryGetCachedDab(stamp, out var dab))
                {
                    buckets.Clear();
                    return false;
                }
                _lastCachedDabCount++;

                var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
                var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
                var right = left + dab.LogicalWidth;
                var bottom = top + dab.LogicalHeight;
                if (right <= dirty.X || bottom <= dirty.Y || left >= dirty.Right || top >= dirty.Bottom)
                    continue;

                var placed = new PlacedDab(stamp, dab, left, top, right, bottom, colorB, colorG, colorR, colorA);
                var firstTx = FloorDiv(left, tsz);
                var firstTy = FloorDiv(top, tsz);
                var lastTx = FloorDiv(right - 1, tsz);
                var lastTy = FloorDiv(bottom - 1, tsz);

                for (var ty = firstTy; ty <= lastTy; ty++)
                {
                    for (var tx = firstTx; tx <= lastTx; tx++)
                    {
                        var tileLeft = tx * tsz;
                        var tileTop = ty * tsz;
                        if (right <= tileLeft || bottom <= tileTop || left >= tileLeft + tsz || top >= tileTop + tsz)
                            continue;

                        int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                        if (!buckets.TryGetValue(key, out var list))
                        {
                            list = new List<PlacedDab>(8);
                            buckets.Add(key, list);
                        }
                        list.Add(placed);
                    }
                }
            }

            if (buckets.Count == 0)
                return true;
            _lastTileBucketCount = buckets.Count;

            layer.Pixels.EnterPixelWriteLock();
            try
            {
                foreach (var (key, dabs) in buckets)
                {
                    int tx = (short)key;
                    int ty = (short)(key >> 16);
                    var tile = layer.Pixels.GetOrCreateRawTile(tx, ty);
                    var tilePixX = tx * tsz;
                    var tilePixY = ty * tsz;

                    foreach (var placed in dabs)
                    {
                        ApplyCachedDabToTile(
                            tile, tilePixX, tilePixY, dirty, grainTable,
                            placed, blendMode,
                            brushGrain, texPx, texW, texH, texStride,
                            layer.IsAlphaLocked);
                    }
                }
            }
            finally { layer.Pixels.ExitPixelWriteLock(); }

            return true;
        }
        finally
        {
        }
    }

    private unsafe bool TryRasterizeCachedColorDabsTileMajor(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        PixelRegion dirty,
        float[]? grainTable)
    {
        if (_stamps.Count == 0)
            return false;

        const int tsz = TiledPixelBuffer.TileSize;
        _colorDabBuckets.Clear();
        var buckets = _colorDabBuckets;
        buckets.EnsureCapacity(Math.Max(16, Math.Min(_stamps.Count * 4, 4096)));

        try
        {
            for (var i = 0; i < _stamps.Count; i++)
            {
                var stamp = _stamps[i];
                if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

                if (!stroke.TryGetCachedColorDab(stamp, out var dab))
                {
                    buckets.Clear();
                    return false;
                }
                _lastCachedDabCount++;

                var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
                var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
                var right = left + dab.LogicalWidth;
                var bottom = top + dab.LogicalHeight;
                if (right <= dirty.X || bottom <= dirty.Y || left >= dirty.Right || top >= dirty.Bottom)
                    continue;

                var placed = new PlacedColorDab(stamp, dab, left, top, right, bottom);
                var firstTx = FloorDiv(left, tsz);
                var firstTy = FloorDiv(top, tsz);
                var lastTx = FloorDiv(right - 1, tsz);
                var lastTy = FloorDiv(bottom - 1, tsz);

                for (var ty = firstTy; ty <= lastTy; ty++)
                {
                    for (var tx = firstTx; tx <= lastTx; tx++)
                    {
                        var tileLeft = tx * tsz;
                        var tileTop = ty * tsz;
                        if (right <= tileLeft || bottom <= tileTop || left >= tileLeft + tsz || top >= tileTop + tsz)
                            continue;

                        int key = (tx & 0xFFFF) | ((ty & 0xFFFF) << 16);
                        if (!buckets.TryGetValue(key, out var list))
                        {
                            list = new List<PlacedColorDab>(8);
                            buckets.Add(key, list);
                        }
                        list.Add(placed);
                    }
                }
            }

            if (buckets.Count == 0)
                return true;
            _lastTileBucketCount = buckets.Count;

            layer.Pixels.EnterPixelWriteLock();
            try
            {
                foreach (var (key, dabs) in buckets)
                {
                    int tx = (short)key;
                    int ty = (short)(key >> 16);
                    var tile = layer.Pixels.GetOrCreateRawTile(tx, ty);
                    var tilePixX = tx * tsz;
                    var tilePixY = ty * tsz;

                    foreach (var placed in dabs)
                    {
                        ApplyCachedColorDabToTile(
                            tile, tilePixX, tilePixY, dirty, grainTable,
                            placed, blendMode,
                            brushGrain, texPx, texW, texH, texStride,
                            layer.IsAlphaLocked);
                    }
                }
            }
            finally { layer.Pixels.ExitPixelWriteLock(); }

            return true;
        }
        finally
        {
        }
    }

    private static unsafe void ApplyCachedColorDabToTile(
        byte[] tile,
        int tilePixX,
        int tilePixY,
        PixelRegion dirty,
        float[]? grainTable,
        PlacedColorDab placed,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        bool alphaLocked)
    {
        var stamp = placed.Stamp;
        var opacity = stamp.Opacity;
        if (opacity <= 0) return;

        const int tsz = TiledPixelBuffer.TileSize;
        var pxMinX = Math.Max(placed.Left, tilePixX);
        var pxMinY = Math.Max(placed.Top, tilePixY);
        var pxMaxX = Math.Min(placed.Right, tilePixX + tsz);
        var pxMaxY = Math.Min(placed.Bottom, tilePixY + tsz);
        if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) return;

        var srcPtr = (byte*)placed.Dab.Bitmap.GetPixels().ToPointer();
        var srcStride = placed.Dab.Bitmap.RowBytes;
        var useFastPath = !placed.Dab.IsScaled && grainTable == null && brushGrain <= 0f;

        bool isSrcOver = blendMode == SKBlendMode.SrcOver;
        float grainBase = 1f - brushGrain;
        bool hasGrainTable = grainTable != null;
        bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
        bool hasTexGrain = hasProceduralGrain && texPx != null;

        for (int py = pxMinY; py < pxMaxY; py++)
        {
            var localY = py - placed.Top;
            var ly = py - tilePixY;
            var rowBase = ly * tsz * 4;
            var srcRow = useFastPath ? srcPtr + localY * srcStride : null;

            for (int px = pxMinX; px < pxMaxX; px++)
            {
                byte srcB, srcG, srcR, srcA;
                if (useFastPath)
                {
                    var srcOffset = (px - placed.Left) * 4;
                    srcB = srcRow![srcOffset];
                    srcG = srcRow[srcOffset + 1];
                    srcR = srcRow[srcOffset + 2];
                    srcA = srcRow[srcOffset + 3];
                }
                else
                {
                    SampleColorDabPixel(placed.Dab, px - placed.Left, localY, out srcB, out srcG, out srcR, out srcA);
                }
                if (srcA == 0) continue;

                float alpha = srcA / 255f;
                if (hasGrainTable)
                {
                    int gy = py - dirty.Y, gx = px - dirty.X;
                    if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                        alpha *= grainTable![gy * dirty.Width + gx];
                }
                else if (hasProceduralGrain)
                {
                    if (hasTexGrain)
                        alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                    else
                        alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                }

                int stampA = (int)(alpha * opacity * 255f + 0.5f);
                if (stampA <= 0) continue;
                if (stampA > 255) stampA = 255;

                var lx = px - tilePixX;
                var offset = rowBase + lx * 4;
                if (isSrcOver)
                {
                    fixed (byte* tp = tile)
                        Canvas.Engine.SimdPixelOps.StampSrcOver(tp + offset,
                            srcB, srcG, srcR, stampA, alphaLocked);
                }
                else
                {
                    WriteCompositeStamp(tile, offset, srcB, srcG, srcR, (byte)stampA, alphaLocked, blendMode);
                }
            }
        }
    }

    private static unsafe int SampleMaskAlpha(ActiveStroke.CachedDab dab, int localX, int localY)
    {
        if ((uint)localX >= (uint)dab.LogicalWidth || (uint)localY >= (uint)dab.LogicalHeight || dab.Mask.IsEmpty)
            return 0;

        var maskPtr = (byte*)dab.Mask.GetPixels().ToPointer();
        var maskW = dab.Mask.Width;
        var maskH = dab.Mask.Height;
        var stride = dab.Mask.RowBytes;
        if (!dab.IsScaled)
            return maskPtr[localY * stride + localX];

        var fx = localX / dab.MaskScaleX;
        var fy = localY / dab.MaskScaleY;
        if (fx < 0f || fy < 0f || fx >= maskW || fy >= maskH)
            return 0;

        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, maskW - 1);
        var y1 = Math.Min(y0 + 1, maskH - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        var a00 = maskPtr[y0 * stride + x0];
        var a10 = maskPtr[y0 * stride + x1];
        var a01 = maskPtr[y1 * stride + x0];
        var a11 = maskPtr[y1 * stride + x1];
        var top = a00 + (a10 - a00) * tx;
        var bottom = a01 + (a11 - a01) * tx;
        return (int)(top + (bottom - top) * ty + 0.5f);
    }

    private static unsafe void SampleColorDabPixel(
        ActiveStroke.CachedColorDab dab,
        int localX,
        int localY,
        out byte b,
        out byte g,
        out byte r,
        out byte a)
    {
        b = g = r = a = 0;
        if ((uint)localX >= (uint)dab.LogicalWidth || (uint)localY >= (uint)dab.LogicalHeight || dab.Bitmap.IsEmpty)
            return;

        var srcPtr = (byte*)dab.Bitmap.GetPixels().ToPointer();
        var srcW = dab.Bitmap.Width;
        var srcH = dab.Bitmap.Height;
        var stride = dab.Bitmap.RowBytes;
        if (!dab.IsScaled)
        {
            var offset = localY * stride + localX * 4;
            b = srcPtr[offset];
            g = srcPtr[offset + 1];
            r = srcPtr[offset + 2];
            a = srcPtr[offset + 3];
            return;
        }

        var fx = localX / dab.MaskScaleX;
        var fy = localY / dab.MaskScaleY;
        if (fx < 0f || fy < 0f || fx >= srcW || fy >= srcH)
            return;

        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, srcW - 1);
        var y1 = Math.Min(y0 + 1, srcH - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        var o00 = y0 * stride + x0 * 4;
        var o10 = y0 * stride + x1 * 4;
        var o01 = y1 * stride + x0 * 4;
        var o11 = y1 * stride + x1 * 4;
        b = (byte)(srcPtr[o00] + (srcPtr[o10] - srcPtr[o00]) * tx + ((srcPtr[o01] + (srcPtr[o11] - srcPtr[o01]) * tx) - (srcPtr[o00] + (srcPtr[o10] - srcPtr[o00]) * tx)) * ty + 0.5f);
        g = (byte)(srcPtr[o00 + 1] + (srcPtr[o10 + 1] - srcPtr[o00 + 1]) * tx + ((srcPtr[o01 + 1] + (srcPtr[o11 + 1] - srcPtr[o01 + 1]) * tx) - (srcPtr[o00 + 1] + (srcPtr[o10 + 1] - srcPtr[o00 + 1]) * tx)) * ty + 0.5f);
        r = (byte)(srcPtr[o00 + 2] + (srcPtr[o10 + 2] - srcPtr[o00 + 2]) * tx + ((srcPtr[o01 + 2] + (srcPtr[o11 + 2] - srcPtr[o01 + 2]) * tx) - (srcPtr[o00 + 2] + (srcPtr[o10 + 2] - srcPtr[o00 + 2]) * tx)) * ty + 0.5f);
        a = (byte)(srcPtr[o00 + 3] + (srcPtr[o10 + 3] - srcPtr[o00 + 3]) * tx + ((srcPtr[o01 + 3] + (srcPtr[o11 + 3] - srcPtr[o01 + 3]) * tx) - (srcPtr[o00 + 3] + (srcPtr[o10 + 3] - srcPtr[o00 + 3]) * tx)) * ty + 0.5f);
    }

    private static unsafe void ApplyCachedDabToTile(
        byte[] tile,
        int tilePixX,
        int tilePixY,
        PixelRegion dirty,
        float[]? grainTable,
        PlacedDab placed,
        SKBlendMode blendMode,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        bool alphaLocked,
        byte* softenLut = null)
    {
        var stamp = placed.Stamp;
        float stampOpacity255 = StampOpacity255(blendMode, stamp.Opacity, placed.ColorA);
        if (stampOpacity255 <= 0) return;

        const int tsz = TiledPixelBuffer.TileSize;
        var pxMinX = Math.Max(placed.Left, tilePixX);
        var pxMinY = Math.Max(placed.Top, tilePixY);
        var pxMaxX = Math.Min(placed.Right, tilePixX + tsz);
        var pxMaxY = Math.Min(placed.Bottom, tilePixY + tsz);
        if (pxMinX >= pxMaxX || pxMinY >= pxMaxY) return;

        var maskPtr = (byte*)placed.Dab.Mask.GetPixels().ToPointer();
        var maskStride = placed.Dab.Mask.RowBytes;
        var useFastPath = !placed.Dab.IsScaled && grainTable == null && brushGrain <= 0f && softenLut == null;

        bool isSrcOver = blendMode == SKBlendMode.SrcOver;
        float grainBase = 1f - brushGrain;
        bool hasGrainTable = grainTable != null;
        bool hasProceduralGrain = !hasGrainTable && brushGrain > 0f;
        bool hasTexGrain = hasProceduralGrain && texPx != null;
        int brushB = placed.ColorB, brushG = placed.ColorG, brushR = placed.ColorR;

        for (int py = pxMinY; py < pxMaxY; py++)
        {
            var localY = py - placed.Top;
            var ly = py - tilePixY;
            var rowBase = ly * tsz * 4;

            if (useFastPath && isSrcOver)
            {
                var maskRow = maskPtr + localY * maskStride;
                var dstOffset = rowBase + (pxMinX - tilePixX) * 4;
                var maskOffset = pxMinX - placed.Left;
                var opacity255 = (int)(stampOpacity255 + 0.5f);
                fixed (byte* tp = tile)
                    Canvas.Engine.SimdPixelOps.StampSrcOverMaskedRow(
                        tp + dstOffset, maskRow + maskOffset,
                        pxMaxX - pxMinX, brushB, brushG, brushR, opacity255, alphaLocked);
                continue;
            }

            for (int px = pxMinX; px < pxMaxX; px++)
            {
                int maskA = SampleMaskAlpha(placed.Dab, px - placed.Left, localY);
                if (maskA == 0) continue;

                if (softenLut != null)
                    maskA = softenLut[maskA];

                float alpha = maskA / 255f;
                if (hasGrainTable)
                {
                    int gy = py - dirty.Y, gx = px - dirty.X;
                    if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                        alpha *= grainTable![gy * dirty.Width + gx];
                }
                else if (hasProceduralGrain)
                {
                    if (hasTexGrain)
                        alpha *= grainBase + (texPx[((py % texH + texH) % texH) * texStride + ((px % texW + texW) % texW)] / 255.0f) * brushGrain;
                    else
                        alpha *= grainBase + GrainNoise(px, py) * brushGrain;
                }

                int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                if (stampA <= 0) continue;
                if (stampA > 255) stampA = 255;

                var lx = px - tilePixX;
                var offset = rowBase + lx * 4;
                if (isSrcOver)
                {
                    fixed (byte* tp = tile)
                        Canvas.Engine.SimdPixelOps.StampSrcOver(tp + offset,
                            (byte)brushB, (byte)brushG, (byte)brushR, stampA, alphaLocked);
                }
                else
                {
                    WriteCompositeStamp(tile, offset,
                        (byte)brushB, (byte)brushG, (byte)brushR, (byte)stampA,
                        alphaLocked, blendMode);
                }
            }
        }
    }

    private unsafe bool TryRasterizeCachedDab(
        DrawingLayer layer,
        ActiveStroke stroke,
        BrushPreset brush,
        StampSample stamp,
        SKBlendMode blendMode,
        int brushB,
        int brushG,
        int brushR,
        float baseAlpha,
        float brushGrain,
        byte* texPx,
        int texW,
        int texH,
        int texStride,
        float[]? grainTable,
        PixelRegion dirty)
    {
        if (!stroke.TryGetCachedDab(stamp, out var dab))
            return false;
        _lastCachedDabCount++;

        float stampOpacity255 = StampOpacity255(blendMode, stamp.Opacity, baseAlpha);
        if (stampOpacity255 <= 0) return true;

        var left = (int)MathF.Round(stamp.X) + dab.OffsetX;
        var top = (int)MathF.Round(stamp.Y) + dab.OffsetY;
        var right = left + dab.LogicalWidth;
        var bottom = top + dab.LogicalHeight;
        const int tsz = TiledPixelBuffer.TileSize;

        var maskPtr = (byte*)dab.Mask.GetPixels().ToPointer();
        var maskStride = dab.Mask.RowBytes;
        var useFastPath = !dab.IsScaled && grainTable == null && brushGrain <= 0f;

        int firstTx = (int)Math.Floor((double)left / tsz);
        int firstTy = (int)Math.Floor((double)top / tsz);
        int lastTx = (int)Math.Floor((double)(right - 1) / tsz);
        int lastTy = (int)Math.Floor((double)(bottom - 1) / tsz);

        layer.Pixels.EnterPixelWriteLock();
        try
        {
            for (int ty = firstTy; ty <= lastTy; ty++)
            {
                int tilePixY = ty * tsz;
                int pxMinY = Math.Max(top, tilePixY);
                int pxMaxY = Math.Min(bottom, tilePixY + tsz);
                if (pxMinY >= pxMaxY) continue;

                for (int tx = firstTx; tx <= lastTx; tx++)
                {
                    int tilePixX = tx * tsz;
                    int pxMinX = Math.Max(left, tilePixX);
                    int pxMaxX = Math.Min(right, tilePixX + tsz);
                    if (pxMinX >= pxMaxX) continue;

                    var tile = layer.Pixels.GetOrCreateRawTile(tx, ty);

                    for (int py = pxMinY; py < pxMaxY; py++)
                    {
                        int localY = py - top;
                        int ly = py - tilePixY;
                        int rowBase = ly * tsz * 4;
                        var maskRow = useFastPath ? maskPtr + localY * maskStride : null;

                        for (int px = pxMinX; px < pxMaxX; px++)
                        {
                            int maskA = useFastPath
                                ? maskRow![px - left]
                                : SampleMaskAlpha(dab, px - left, localY);
                            if (maskA == 0) continue;

                            float alpha = maskA / 255f;
                            if (grainTable != null)
                            {
                                int gy = py - dirty.Y, gx = px - dirty.X;
                                if (gy >= 0 && gy < dirty.Height && gx >= 0 && gx < dirty.Width)
                                    alpha *= grainTable[gy * dirty.Width + gx];
                            }
                            else if (brushGrain > 0f)
                            {
                                float noise;
                                if (texPx != null)
                                {
                                    int gtx = px % texW; if (gtx < 0) gtx += texW;
                                    int gty = py % texH; if (gty < 0) gty += texH;
                                    noise = texPx[gty * texStride + gtx] / 255.0f;
                                }
                                else
                                    noise = GrainNoise(px, py);
                                alpha *= 1f - brushGrain + noise * brushGrain;
                            }

                            int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                            if (stampA <= 0) continue;
                            if (stampA > 255) stampA = 255;

                            int lx = px - tilePixX;
                            int offset = rowBase + lx * 4;
                            WriteCompositeStamp(tile, offset,
                                (byte)brushB, (byte)brushG, (byte)brushR, (byte)stampA,
                                layer.IsAlphaLocked, blendMode);
                        }
                    }
                }
            }
        }
        finally { layer.Pixels.ExitPixelWriteLock(); }

        return true;
    }

    private static double ElapsedMs(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private void RenderPreparedStamps(ActiveStroke stroke, SKCanvas canvas)
    {
        using var colorStampPaint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = stroke.Paint.BlendMode
        };
        using var stackingPaint = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver
        };

        for (var i = 0; i < _stamps.Count; i++)
        {
            var stamp = _stamps[i];
            if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

            if (_stampColors.Count > i)
            {
                var color = _stampColors[i];
                if (color.Alpha == 0) continue;
                stroke.UpdateColor(color);
            }

            stroke.UpdateOpacity(stamp.Opacity);
            stroke.UpdateMatrix(stamp);

            var tipIndices = stroke.TipIndicesFor(stamp.TipIndex);
            for (var ti = 0; ti < tipIndices.Length; ti++)
            {
                var tipIndex = tipIndices[ti];
                var tip = stroke.TipFor(tipIndex);

                if (tip.HasColor && tip.GenerateColorStamp(stroke.BaseMaskSize) is { } colorStamp)
                {
                    var alpha = (byte)Math.Clamp((int)(stamp.Opacity * 255), 0, 255);
                    colorStampPaint.BlendMode = ti == 0 ? stroke.Paint.BlendMode : SKBlendMode.SrcOver;
                    colorStampPaint.Color = new SKColor(255, 255, 255, alpha);
                    canvas.Save();
                    canvas.Concat(in stroke.Matrix);
                    canvas.DrawBitmap(colorStamp, 0f, 0f, colorStampPaint);
                    canvas.Restore();
                    continue;
                }

                if (ti > 0)
                {
                    stackingPaint.Color = stroke.Paint.Color;
                    stackingPaint.ColorFilter = stroke.Paint.ColorFilter;
                }
                var mask = stroke.MaskFor(tipIndex, stamp);
                var paint = ti == 0 ? stroke.Paint : stackingPaint;

                canvas.Save();
                canvas.Concat(in stroke.Matrix);
                canvas.DrawBitmap(mask, 0f, 0f, paint);
                canvas.Restore();

                stroke.ReleaseMask(mask);
            }
        }
    }
    private static void ReadPixelFromTile(byte[]? tile, int tilePixX, int tilePixY, int px, int py,
        out byte b, out byte g, out byte r, out byte a)
    {
        if (tile == null)
        {
            b = g = r = a = 0;
            return;
        }

        const int tsz = TiledPixelBuffer.TileSize;
        var lx = px - tilePixX;
        var ly = py - tilePixY;
        if ((uint)lx >= tsz || (uint)ly >= tsz)
        {
            b = g = r = a = 0;
            return;
        }

        var offset = (ly * tsz + lx) * 4;
        b = tile[offset];
        g = tile[offset + 1];
        r = tile[offset + 2];
        a = tile[offset + 3];
    }

    private static void WriteCompositeStamp(
        byte[] tile, int offset,
        byte srcB, byte srcG, byte srcR, byte stampA,
        bool alphaLocked, SKBlendMode blendMode)
    {
        byte db = tile[offset], dg = tile[offset + 1], dr = tile[offset + 2], da = tile[offset + 3];
        AlphaLockPixelOps.CompositeBrushPixel(ref db, ref dg, ref dr, ref da, srcB, srcG, srcR, stampA, alphaLocked, blendMode);
        tile[offset] = db;
        tile[offset + 1] = dg;
        tile[offset + 2] = dr;
        tile[offset + 3] = da;
    }

    private static bool SupportsCpuRasterBlendMode(SKBlendMode mode) =>
        mode is SKBlendMode.SrcOver or SKBlendMode.DstOut or SKBlendMode.Clear
            or SKBlendMode.Multiply or SKBlendMode.Screen or SKBlendMode.Overlay
            or SKBlendMode.Darken or SKBlendMode.Lighten or SKBlendMode.ColorDodge
            or SKBlendMode.ColorBurn or SKBlendMode.HardLight or SKBlendMode.SoftLight
            or SKBlendMode.Difference or SKBlendMode.Exclusion;

    private static bool UsesMaskOpacity(SKBlendMode mode)
        => mode is SKBlendMode.DstOut or SKBlendMode.Clear;

    private static float StampOpacity255(SKBlendMode mode, float stampOpacity, float colorAlpha)
        => UsesMaskOpacity(mode) ? stampOpacity * 255f : stampOpacity * colorAlpha;

    private static void RenderWithSkiaOnLayer(DrawingLayer layer, PixelRegion dirty, Action<SKCanvas> render)
    {
        if (!layer.IsAlphaLocked)
        {
            layer.Pixels.RenderWithSkia(dirty, render);
            return;
        }

        var before = layer.Pixels.CaptureTiles(dirty);
        layer.Pixels.RenderWithSkia(dirty, render);
        layer.Pixels.EnterPixelWriteLock();
        try
        {
            AlphaLockPixelOps.RestoreLockedTransparentPixels(layer.Pixels, dirty, before);
        }
        finally
        {
            layer.Pixels.ExitPixelWriteLock();
        }
    }
}
