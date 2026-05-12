using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes;

public sealed class BrushEngine : IDisposable
{
    private const int InitialStampCapacity = 256;
    private const float MinStretchCarry = 0.02f;
    private const float MaxStretchCarry = 0.88f;
    private readonly List<StampSample> _stamps = new(InitialStampCapacity);
    private readonly List<SKColor> _stampColors = new(InitialStampCapacity);
    private ActiveStroke? _activeStroke;
    private SKBitmap? _scratch;
    private readonly SKPaint _scratchCompositePaint = new() { BlendMode = SKBlendMode.SrcOver };

    public delegate void PixelSampler(int x, int y, out byte b, out byte g, out byte r, out byte a);

    public void BeginStroke(BrushPreset brush, CanvasInputSample sample)
    {
        EndStroke();
        _activeStroke = new ActiveStroke(brush, sample);
    }

    public void EndStroke()
    {
        _activeStroke?.Dispose();
        _activeStroke = null;
        _stamps.Clear();
        _stampColors.Clear();
    }

    public PixelRegion RasterizeSegment(
        DrawingLayer layer, BrushPreset brush,
        CanvasInputSample from, CanvasInputSample to,
        PixelSampler? sampleSource = null)
    {
        return RasterizeSegmentInternal(layer, brush, from, to, ensureEndpoint: false, sampleSource);
    }

    public PixelRegion RasterizeFinalSegment(
        DrawingLayer layer, BrushPreset brush,
        CanvasInputSample from, CanvasInputSample to,
        PixelSampler? sampleSource = null)
    {
        return RasterizeSegmentInternal(layer, brush, from, to, ensureEndpoint: true, sampleSource);
    }

    private PixelRegion RasterizeSegmentInternal(
        DrawingLayer layer, BrushPreset brush,
        CanvasInputSample from, CanvasInputSample to, bool ensureEndpoint,
        PixelSampler? sampleSource)
    {
        EnsureStroke(brush, from);
        var stroke = _activeStroke!;
        _stamps.Clear();
        _stampColors.Clear();

        var dirty = BuildStamps(stroke, brush, from, to, ensureEndpoint);
        if (dirty.IsEmpty || _stamps.Count == 0) return PixelRegion.Empty;

        if (brush.BlendMode != SKBlendMode.DstOut && brush.ColorMix)
            PrepareStampColors(layer, brush, stroke, sampleSource);

        if (dirty.IsEmpty) return PixelRegion.Empty;

        // For standard SrcOver brushes without color mixing, render stamps to a
        // temporary scratch with Lighten blend so overlapping stamps within this
        // segment take the MAX alpha rather than compounding. The scratch is then
        // composited onto the layer with SrcOver once.
        // Skip scratch for few stamps (large brushes) — the overhead of allocating
        // and blitting a huge scratch bitmap exceeds the cost of direct per-stamp draw.
        bool canDirect = _stampColors.Count == 0
            && (brush.BlendMode == SKBlendMode.SrcOver || brush.BlendMode == SKBlendMode.DstOut)
            && brush.Shape == null
            && brush.Tip is ProceduralBrushTip { Shape: BrushTipShape.Circle or BrushTipShape.SoftRound or BrushTipShape.Ellipse };

        bool useScratch = !canDirect
            && brush.BlendMode == SKBlendMode.SrcOver
            && _stampColors.Count == 0 // no color mixing
            && dirty.Width <= 4096 && dirty.Height <= 4096 // guard against OOM
            && _stamps.Count > 3; // only worth it for many small stamps

        if (canDirect)
            RasterizeStampsDirect(layer, stroke, brush, dirty);
        else if (useScratch)
            RenderStampsViaScratch(layer, stroke, dirty);
        else
            layer.Pixels.RenderWithSkia(dirty, canvas => RenderPreparedStamps(stroke, canvas));

        return dirty;
    }

    public PixelRegion RasterizeDab(
        DrawingLayer layer,
        BrushPreset brush,

        CanvasInputSample sample,
        double velocity,
        PixelSampler? sampleSource = null)
    {
        EnsureStroke(brush, sample);
        var stroke = _activeStroke!;
        _stamps.Clear();
        _stampColors.Clear();

        var velocity01 = (float)Math.Clamp(velocity / 5000.0, 0, 1);
        var sp = BuildStrokePoint(stroke, sample, velocity01);
        var stamp = CreateStamp(stroke, brush, sp);
        _stamps.Add(stamp);

        if (brush.BlendMode != SKBlendMode.DstOut && brush.ColorMix)
            PrepareStampColors(layer, brush, stroke, sampleSource);

        var dirty = StampBounds(stamp);
        if (dirty.IsEmpty) return PixelRegion.Empty;

        layer.Pixels.RenderWithSkia(dirty, canvas => RenderPreparedStamps(stroke, canvas));
        return dirty;
    }

    private static float LerpAngle(float a, float b, float t)
    {
        var delta = b - a;
        if (delta > MathF.PI) delta -= MathF.Tau;
        else if (delta < -MathF.PI) delta += MathF.Tau;
        return a + delta * t;
    }

    public PixelRegion EstimateSegmentRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample from, CanvasInputSample to)
    {
        var radius = EstimateBrushRadius(brush);
        var minX = (int)Math.Floor(Math.Min(from.X, to.X) - radius);
        var minY = (int)Math.Floor(Math.Min(from.Y, to.Y) - radius);
        var maxX = (int)Math.Ceiling(Math.Max(from.X, to.X) + radius);
        var maxY = (int)Math.Ceiling(Math.Max(from.Y, to.Y) + radius);
        return new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public PixelRegion EstimateDabRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample sample)
        => EstimateSegmentRegion(layer, brush, sample, sample);

    public void Dispose()
    {
        EndStroke();
        _scratch?.Dispose();
        _scratch = null;
        _scratchCompositePaint.Dispose();
    }

    private void EnsureStroke(BrushPreset brush, CanvasInputSample sample)
    {
        if (_activeStroke == null || !_activeStroke.Matches(brush))
            BeginStroke(brush, sample);
    }

    private PixelRegion BuildStamps(ActiveStroke stroke, BrushPreset brush, CanvasInputSample from, CanvasInputSample to, bool ensureEndpoint = false)
    {
        if (from.Pressure <= 0 && to.Pressure <= 0) return PixelRegion.Empty;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var elapsedSeconds = Math.Max(0.001, (to.TimeMicros - from.TimeMicros) / 1_000_000.0);
        var velocity01 = Math.Clamp((float)(distance / elapsedSeconds / 5000.0), 0, 1);
        // Subdivide based on expected stamp count, not raw pixel distance.
        // A 1000px brush with 250px spacing needs ~4 stamps; 96 subdivisions
        // would waste time on 90+ pointless Catmull-Rom evaluations.
        var stampSpacing = Math.Max(1, stroke.State.NextStampDistance);
        var estimatedStamps = distance / stampSpacing;
        var subdivisions = Math.Max(8, Math.Min(96, (int)Math.Ceiling(estimatedStamps * 4)));

        if (distance > 0.001)
        {
            var currentAngle = MathF.Atan2((float)dy, (float)dx);
            stroke.State.DrawingAngle = LerpAngle(stroke.State.DrawingAngle, currentAngle, 0.5f);
        }
        var p0 = new SplinePoint(
            stroke.State.LastX, stroke.State.LastY, stroke.State.LastPressure,
            stroke.State.LastTiltX, stroke.State.LastTiltY, (float)from.Twist);
        var p1 = ToSplinePoint(from);
        var p2 = ToSplinePoint(to);
        var p3 = new SplinePoint(
            (float)(to.X + (to.X - from.X)), (float)(to.Y + (to.Y - from.Y)),
            (float)to.Pressure, (float)to.TiltX, (float)to.TiltY, (float)to.Twist);

        var previous = p1;
        var dirty = PixelRegion.Empty;

        for (var i = 1; i <= subdivisions; i++)
        {
            var t = i / (float)subdivisions;
            var current = CatmullRom(p0, p1, p2, p3, t);
            var segDx = current.X - previous.X;
            var segDy = current.Y - previous.Y;
            var segLen = MathF.Sqrt(segDx * segDx + segDy * segDy);

            if (segLen > 0.0001f)
            {
                var consumed = stroke.State.NextStampDistance - stroke.State.DistanceLeftover;
                while (consumed <= segLen)
                {
                    var ratio = consumed / segLen;
                    var sample = Lerp(previous, current, ratio, from, to);
                    var sp = BuildStrokePoint(stroke, sample, velocity01);
                    var stamp = CreateStamp(stroke, brush, sp);
                    _stamps.Add(stamp);
                    dirty = dirty.Union(StampBounds(stamp));
                    stroke.State.TotalDistance += stroke.State.NextStampDistance;
                    stroke.State.DabSeqNo++;
                    stroke.State.NextStampDistance = StampSpacing(brush, stamp);
                    consumed += stroke.State.NextStampDistance;
                }

                stroke.State.DistanceLeftover = segLen - (consumed - stroke.State.NextStampDistance);
                if (stroke.State.DistanceLeftover >= stroke.State.NextStampDistance)
                    stroke.State.DistanceLeftover = 0;
            }

            previous = current;
        }

        // On the final segment, the spacing accumulator may leave a small gap to
        // the pen-up endpoint. Expand the dirty region by one stamp radius to
        // ensure soft brush edges cover any sub-spacing gap.
        if (ensureEndpoint && _stamps.Count > 0)
        {
            dirty = dirty.Inflate((int)(_stamps[^1].Size * 0.25f + 1));
        }

        stroke.State.LastX = (float)to.X;
        stroke.State.LastY = (float)to.Y;
        stroke.State.LastPressure = (float)to.Pressure;
        stroke.State.LastTiltX = (float)to.TiltX;
        stroke.State.LastTiltY = (float)to.TiltY;

        return dirty;
    }

    private static StrokePoint BuildStrokePoint(ActiveStroke stroke, CanvasInputSample sample, float velocity01)
        => new(
            x: (float)sample.X, y: (float)sample.Y,
            pressure: (float)sample.Pressure,
            tiltX: (float)sample.TiltX, tiltY: (float)sample.TiltY, twist: (float)sample.Twist,
            drawingAngle: stroke.State.DrawingAngle,
            speed: velocity01,
            totalDistance: stroke.State.TotalDistance,
            dabSeqNo: stroke.State.DabSeqNo,
            random: Hash01((int)(sample.X * 7919f), (int)(sample.Y * 6353f)),
            strokeRandom: stroke.StrokeRandom);

    private static float StampSpacing(BrushPreset brush, StampSample stamp)
    {
        var flow = Math.Clamp((float)brush.Flow, 0.01f, 1.0f);
        var spacing = Math.Clamp((float)brush.Spacing * stamp.SpacingMultiplier, 0.005f, 4f);
        return Math.Max(0.5f, stamp.Size * spacing * MathF.Sqrt(flow));
    }

    private static StampSample CreateStamp(ActiveStroke stroke, BrushPreset brush, in StrokePoint sp)
    {
        var dyn = brush.Dynamics;
        var sizeMul = dyn.EvalSize(sp);
        var opacMul = dyn.EvalOpacity(sp);
        var flowMul = dyn.EvalFlow(sp);
        var hardness = dyn.Hardness.IsEnabled ? dyn.EvalHardness(sp) : (float)brush.Hardness;
        var spacingMul = dyn.EvalSpacing(sp);
        var tipDensityMul = dyn.TipDensity.IsEnabled ? dyn.EvalTipDensity(sp) : 1f;
        var tipThicknessMul = dyn.TipThickness.IsEnabled ? dyn.EvalTipThickness(sp) : 1f;
        var scatter = dyn.Scatter.IsEnabled ? dyn.EvalScatter(sp) : 0f;
        var rotDeg = dyn.EvalRotationDeg(sp);
        var size = Math.Max(0.5f, (float)brush.Size * sizeMul);
        // Opacity is independent of AmountOfPaint. AmountOfPaint controls how much
        // brush color is deposited, not the visibility of the stamp.
        var opacity = (float)Math.Clamp(brush.Opacity * brush.Flow * brush.TipDensity * tipDensityMul * opacMul * flowMul, 0, 1);
        var trajectoryDeg = sp.DrawingAngle * (180f / MathF.PI);
        float directionContrib = brush.BaseAngleSource switch
        {
            AngleSource.DirectionOfLine => trajectoryDeg,
            AngleSource.PenTilt => MathF.Atan2(sp.TiltX, sp.TiltY) * (180f / MathF.PI),
            AngleSource.PenTwist => sp.Twist * (180f / MathF.PI),
            _ => 0f
        };
        var jitter = brush.AngleJitter > 0.001f
            ? (sp.Random * 2f - 1f) * brush.AngleJitter * 180f
            : 0f;
        var angle = (float)brush.Angle + directionContrib + rotDeg + jitter;
        var x = sp.X;
        var y = sp.Y;
        if (scatter > 0.001f)
        {
            var radians = sp.Random * MathF.Tau;
            var amount = (Hash01(sp.DabSeqNo, (int)(sp.StrokeRandom * 100_000f)) * 2f - 1f) * scatter * size;
            x += MathF.Cos(radians) * amount;
            y += MathF.Sin(radians) * amount;
        }

        return new StampSample(
            x, y, size, opacity, angle,
            Math.Clamp(hardness, 0.001f, 1f),
            Math.Clamp(spacingMul, 0.05f, 4f),
            Math.Clamp(tipThicknessMul, 0.01f, 4f));
    }

    private static double EstimateBrushRadius(BrushPreset brush)
    {
        var maxSize = brush.Size * Math.Max(1.0, brush.Dynamics.Size.MaxOutput);
        var spacing = maxSize * Math.Max(0.01, brush.Spacing) * Math.Max(1.0, brush.Dynamics.Spacing.MaxOutput);
        var scatter = brush.Dynamics.Scatter.IsEnabled ? maxSize * Math.Max(0.0, brush.Dynamics.Scatter.MaxOutput) : 0.0;
        return Math.Max(1.0, maxSize * 0.75 + spacing + scatter + 3.0);
    }

    private void RenderStampsViaScratch(DrawingLayer layer, ActiveStroke stroke, PixelRegion dirty)
    {
        var w = dirty.Width;
        var h = dirty.Height;

        // Round up to nearest 512 to reduce reallocation churn.
        var needW = (w + 511) & ~511;
        var needH = (h + 511) & ~511;

        // Grow scratch bitmap on demand; shrink if grossly oversized.
        if (_scratch == null || _scratch.Width < needW || _scratch.Height < needH)
        {
            var oldW = _scratch?.Width ?? 0;
            var oldH = _scratch?.Height ?? 0;
            _scratch?.Dispose();
            _scratch = new SKBitmap(new SKImageInfo(
                Math.Max(needW, oldW),
                Math.Max(needH, oldH),
                SKColorType.Bgra8888, SKAlphaType.Unpremul));
        }
        else if (_scratch.Width > needW * 4 || _scratch.Height > needH * 4)
        {
            _scratch.Dispose();
            _scratch = new SKBitmap(new SKImageInfo(needW, needH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        }

        // Clear then render stamps into the scratch with Lighten blend so that
        // overlapping stamps within this segment take the max alpha per pixel
        // rather than compounding via SrcOver.
        using (var sc = new SKCanvas(_scratch))
        {
            sc.Save();
            sc.ClipRect(SKRect.Create(0, 0, w, h));
            sc.Clear(SKColors.Transparent);
            sc.Restore();

            sc.Save();
            sc.Translate(-dirty.X, -dirty.Y);
            sc.ClipRect(SKRect.Create(dirty.X, dirty.Y, w, h));
            stroke.Paint.BlendMode = SKBlendMode.Lighten;
            RenderPreparedStamps(stroke, sc);
            stroke.Paint.BlendMode = SKBlendMode.SrcOver;
            sc.Restore();
        }

        // Composite the scratch result onto the layer tiles with SrcOver.
        // We snapshot the bitmap into an SKImage before the RenderWithSkia call
        // so SkiaSharp doesn't mutate it while the canvas is live on another thread.
        using var scratchImage = SKImage.FromBitmap(_scratch);
        var srcRect = SKRect.Create(0, 0, w, h);
        var dstRect = SKRect.Create(dirty.X, dirty.Y, w, h);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        layer.Pixels.RenderWithSkia(dirty, canvas =>
        {
            canvas.DrawImage(scratchImage, srcRect, dstRect, sampling, _scratchCompositePaint);
        });
    }

    private void RasterizeStampsDirect(DrawingLayer layer, ActiveStroke stroke, BrushPreset brush, PixelRegion dirty)
    {
        var procTip = (ProceduralBrushTip)brush.Tip;
        bool isSoft = procTip.Shape == BrushTipShape.SoftRound;
        bool isCircle = procTip.Shape is BrushTipShape.Circle or BrushTipShape.SoftRound;
        float aspect = isCircle ? 1f : MathF.Max(0.05f, MathF.Min(20f, procTip.AspectRatio));

        var baseColor = stroke.BaseColor;
        int brushB = baseColor.Blue, brushG = baseColor.Green, brushR = baseColor.Red;
        float baseAlpha = baseColor.Alpha;
        bool isDstOut = brush.BlendMode == SKBlendMode.DstOut;

        float brushThickness = MathF.Max(0.01f, MathF.Min(4f, (float)brush.TipThickness));
        bool isHorizontal = brush.TipDirection == BrushTipDirection.Horizontal;
        int baseMaskSize = stroke.BaseMaskSize;
        const int tsz = TiledPixelBuffer.TileSize;

        for (int si = 0; si < _stamps.Count; si++)
        {
            var stamp = _stamps[si];
            if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

            float thickMul = MathF.Max(0.01f, MathF.Min(4f, brushThickness * stamp.TipThicknessMultiplier));
            float scale = stamp.Size / MathF.Max(1f, baseMaskSize);
            float maxR = (baseMaskSize * 0.5f - 0.5f) * scale;
            if (maxR < 0.5f) continue;

            float rxBase = aspect >= 1f ? maxR : maxR * aspect;
            float ryBase = aspect >= 1f ? maxR / aspect : maxR;
            float rx = isHorizontal ? rxBase : rxBase * thickMul;
            float ry = isHorizontal ? ryBase * thickMul : ryBase;
            if (rx < 0.5f || ry < 0.5f) continue;

            bool hasRot = !isCircle && MathF.Abs(stamp.Angle) > 0.1f;
            float cosA = 1f, sinA = 0f;
            if (hasRot)
            {
                float rad = stamp.Angle * MathF.PI / 180f;
                cosA = MathF.Cos(rad);
                sinA = MathF.Sin(rad);
            }

            float stampOpacity255 = isDstOut ? stamp.Opacity * 255f : stamp.Opacity * baseAlpha;
            if (stampOpacity255 <= 0) continue;

            float hardness = stamp.Hardness;
            float hardnessRange = 1f - hardness;
            bool hardEdge = hardness >= 0.999f;

            float maxHalf = MathF.Max(rx, ry) + 1.5f;
            int bLeft = (int)MathF.Floor(stamp.X - maxHalf);
            int bTop  = (int)MathF.Floor(stamp.Y - maxHalf);
            int bRight  = (int)MathF.Ceiling(stamp.X + maxHalf);
            int bBottom = (int)MathF.Ceiling(stamp.Y + maxHalf);

            int firstTx = (int)Math.Floor((double)bLeft    / tsz);
            int firstTy = (int)Math.Floor((double)bTop     / tsz);
            int lastTx  = (int)Math.Floor((double)(bRight  - 1) / tsz);
            int lastTy  = (int)Math.Floor((double)(bBottom - 1) / tsz);

            for (int ty = firstTy; ty <= lastTy; ty++)
            {
                int tilePixY = ty * tsz;
                int pxMinY = Math.Max(bTop,  tilePixY);
                int pxMaxY = Math.Min(bBottom, tilePixY + tsz);
                if (pxMinY >= pxMaxY) continue;

                for (int tx = firstTx; tx <= lastTx; tx++)
                {
                    int tilePixX = tx * tsz;
                    int pxMinX = Math.Max(bLeft,  tilePixX);
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
                            float fdx, fdy;
                            if (hasRot)
                            {
                                fdx = dx * cosA + dy * sinA;
                                fdy = -dx * sinA + dy * cosA;
                            }
                            else { fdx = dx; fdy = dy; }

                            float ndx = fdx / rx;
                            float ndy = fdy / ry;
                            float t2 = ndx * ndx + ndy * ndy;
                            if (t2 >= 1f) continue;

                            float alpha;
                            if (hardEdge)
                            {
                                alpha = 1f;
                            }
                            else
                            {
                                float t = MathF.Sqrt(t2);
                                if (t <= hardness)
                                {
                                    alpha = 1f;
                                }
                                else
                                {
                                    float fade = hardnessRange > 0.001f ? (t - hardness) / hardnessRange : 1f;
                                    alpha = isSoft
                                        ? 1f - fade * fade * (3f - 2f * fade)
                                        : (MathF.Cos(fade * MathF.PI) + 1f) * 0.5f;
                                }
                            }

                            int stampA = (int)(alpha * stampOpacity255 + 0.5f);
                            if (stampA <= 0) continue;
                            if (stampA > 255) stampA = 255;

                            int lx = px - tilePixX;
                            int offset = rowBase + lx * 4;

                            if (isDstOut)
                            {
                                int dstA = tile[offset + 3];
                                if (dstA == 0) continue;
                                tile[offset + 3] = (byte)(dstA * (255 - stampA) / 255);
                            }
                            else
                            {
                                int dstA = tile[offset + 3];
                                int dstW = dstA * (255 - stampA) / 255;
                                int outA = stampA + dstW;
                                tile[offset + 0] = (byte)((brushB * stampA + tile[offset + 0] * dstW) / outA);
                                tile[offset + 1] = (byte)((brushG * stampA + tile[offset + 1] * dstW) / outA);
                                tile[offset + 2] = (byte)((brushR * stampA + tile[offset + 2] * dstW) / outA);
                                tile[offset + 3] = (byte)outA;
                            }
                        }
                    }
                }
            }
        }

        layer.Pixels.PruneRegion(dirty);
    }

    private void RenderPreparedStamps(ActiveStroke stroke, SKCanvas canvas)
    {
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
            var mask = stroke.MaskFor(stamp.Hardness);

            canvas.Save();
            canvas.Concat(in stroke.Matrix);
            canvas.DrawBitmap(mask, 0f, 0f, stroke.Paint);
            canvas.Restore();

            stroke.ReleaseMask(mask);
        }
    }

    private void PrepareStampColors(DrawingLayer layer, BrushPreset brush, ActiveStroke stroke, PixelSampler? sampleSource)
    {
        _stampColors.Clear();
        var amount = Math.Clamp((float)brush.AmountOfPaint, 0f, 1f);
        var density = Math.Clamp((float)brush.DensityOfPaint, 0f, 1f);
        var stretch = Math.Clamp((float)brush.ColorStretch, 0f, 1f);
        var stretchCarry = MinStretchCarry + (MaxStretchCarry - MinStretchCarry) * stretch;
        var blur = brush.SmudgeMode == SmudgeMode.Smudge ? Math.Clamp((float)brush.BlurAmount, 0f, 1f) : 0f;

        for (var i = 0; i < _stamps.Count; i++)
        {
            var stamp = _stamps[i];
            var existing = SampleExistingPigment(layer, brush, stroke, stamp, blur, sampleSource);
            if (existing.Alpha == 0 && stroke.CarriedColor.Alpha == 0 && amount <= 0f)
            {
                _stampColors.Add(SKColors.Transparent);
                continue;
            }

            var pigment = MixColors(existing, stroke.CarriedColor, stretch);
            var baseColor = stroke.BaseColor;
            var mixedRgb = MixRgb(pigment, baseColor, amount);
            var alpha = brush.SmudgeMode == SmudgeMode.Smear
                ? pigment.Alpha
                : MixAlpha(pigment.Alpha, baseColor.Alpha, density);

            if (brush.SmudgeMode == SmudgeMode.Smear && amount > 0f)
                alpha = Math.Max(alpha, (byte)Math.Clamp(baseColor.Alpha * amount, 0, 255));

            var dab = new SKColor(mixedRgb.Red, mixedRgb.Green, mixedRgb.Blue, alpha);
            _stampColors.Add(dab);

            stroke.CarriedColor = brush.SmudgeMode switch
            {
                SmudgeMode.Blend => SKColors.Transparent,
                SmudgeMode.Smudge => DecayAlpha(MixColors(stroke.CarriedColor, existing, 1f), stretchCarry),
                _ => DecayAlpha(MixColors(MixColors(stroke.CarriedColor, existing, 1f), baseColor, amount), stretchCarry)
            };
        }
    }

    private static SKColor SampleExistingPigment(DrawingLayer layer, BrushPreset brush, ActiveStroke stroke, StampSample stamp, float blur, PixelSampler? sampleSource)
    {
        if (blur > 0.001f)
        {
            var (r, g, b, a) = SampleBlurred(layer, (int)stamp.X, (int)stamp.Y, blur, stroke, sampleSource);
            return new SKColor(
                (byte)Math.Clamp(r, 0, 255),
                (byte)Math.Clamp(g, 0, 255),
                (byte)Math.Clamp(b, 0, 255),
                (byte)Math.Clamp(a, 0, 255));
        }

        SamplePixel(layer, sampleSource, (int)stamp.X, (int)stamp.Y, out var sb, out var sg, out var sr, out var sa);
        return new SKColor(sr, sg, sb, sa);
    }

    private static SKColor MixRgb(SKColor from, SKColor to, float t)
        => new(
            (byte)Math.Clamp(from.Red * (1f - t) + to.Red * t, 0, 255),
            (byte)Math.Clamp(from.Green * (1f - t) + to.Green * t, 0, 255),
            (byte)Math.Clamp(from.Blue * (1f - t) + to.Blue * t, 0, 255),
            from.Alpha);

    private static SKColor MixColors(SKColor from, SKColor to, float t)
        => new(
            (byte)Math.Clamp(from.Red * (1f - t) + to.Red * t, 0, 255),
            (byte)Math.Clamp(from.Green * (1f - t) + to.Green * t, 0, 255),
            (byte)Math.Clamp(from.Blue * (1f - t) + to.Blue * t, 0, 255),
            (byte)Math.Clamp(from.Alpha * (1f - t) + to.Alpha * t, 0, 255));

    private static byte MixAlpha(byte from, byte to, float t)
        => (byte)Math.Clamp(from * (1f - t) + to * t, 0, 255);

    private static SKColor DecayAlpha(SKColor color, float persistence)
        => new(color.Red, color.Green, color.Blue, (byte)Math.Clamp(color.Alpha * persistence, 0, 255));

    private static (float R, float G, float B, float A) SampleBlurred(DrawingLayer layer, int cx, int cy, float blur, ActiveStroke stroke, PixelSampler? sampleSource)
    {
        float accR = 0, accG = 0, accB = 0, accA = 0;
        float colorWeightSum = 0;   // accumulate alpha-weighted spatial mass for RGB
        float spatialWeightSum = 0; // accumulate spatial mass for alpha

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx;
                int y = cy + dy;
                int lx = x - layer.OffsetX;
                int ly = y - layer.OffsetY;

                SamplePixel(layer, sampleSource, lx, ly, out byte b, out byte g, out byte r, out byte a);

                float w = (dx == 0 && dy == 0) ? 4.0f : 1.0f;

                // Alpha is averaged over the full kernel (transparent
                // pixels must contribute to avoid alpha-boundary inflation).
                spatialWeightSum += w;
                accA += a * w;

                // RGB is accumulated with alpha pre-multiplication so that
                // transparent pixels do not leak their hidden colour data.
                if (a > 0)
                {
                    float alphaWeight = (a / 255f) * w;
                    accR += r * alphaWeight;
                    accG += g * alphaWeight;
                    accB += b * alphaWeight;
                    colorWeightSum += alphaWeight;
                }
            }
        }

        float blurR = 0, blurG = 0, blurB = 0, blurA = 0;

        if (spatialWeightSum > 0)
            blurA = accA / spatialWeightSum;

        int clx = cx - layer.OffsetX;
        int cly = cy - layer.OffsetY;
        SamplePixel(layer, sampleSource, clx, cly, out byte cb, out byte cg, out byte cr, out byte ca);

        float centerR, centerG, centerB;
        if (ca > 0)
        {
            centerR = cr; centerG = cg; centerB = cb;
        }
        else
        {
            centerR = stroke.CarriedColor.Alpha > 0 ? stroke.CarriedColor.Red : stroke.BaseColor.Red;
            centerG = stroke.CarriedColor.Alpha > 0 ? stroke.CarriedColor.Green : stroke.BaseColor.Green;
            centerB = stroke.CarriedColor.Alpha > 0 ? stroke.CarriedColor.Blue : stroke.BaseColor.Blue;
        }

        if (colorWeightSum > 0)
        {
            blurR = accR / colorWeightSum;
            blurG = accG / colorWeightSum;
            blurB = accB / colorWeightSum;
        }
        else
        {
            // No opaque neighbours — keep the centre colour to avoid
            // darkening the edge toward black when blurring alpha.
            blurR = centerR;
            blurG = centerG;
            blurB = centerB;
        }

        // Interpolate both colour and alpha by the blur strength so they
        // soften in lock-step.
        return (
            centerR + (blurR - centerR) * blur,
            centerG + (blurG - centerG) * blur,
            centerB + (blurB - centerB) * blur,
            ca + (blurA - ca) * blur);
    }

    private static void SamplePixel(DrawingLayer layer, PixelSampler? sampleSource, int x, int y, out byte b, out byte g, out byte r, out byte a)
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
        // RGB to XYZ (sRGB, D65)
        float xr = r / 255f;
        float xg = g / 255f;
        float xb = b / 255f;

        float R = xr > 0.04045f ? MathF.Pow((xr + 0.055f) / 1.055f, 2.4f) : xr / 12.92f;
        float G = xg > 0.04045f ? MathF.Pow((xg + 0.055f) / 1.055f, 2.4f) : xg / 12.92f;
        float B = xb > 0.04045f ? MathF.Pow((xb + 0.055f) / 1.055f, 2.4f) : xb / 12.92f;

        float X = R * 0.4124564f + G * 0.3575761f + B * 0.1804375f;
        float Y = R * 0.2126729f + G * 0.7151522f + B * 0.0721750f;
        float Z = R * 0.0193339f + G * 0.1191920f + B * 0.9503041f;

        // XYZ to LAB
        float fx = X > 0.008856f ? MathF.Pow(X, 1f / 3f) : (7.787f * X + 16f / 116f);
        float fy = Y > 0.008856f ? MathF.Pow(Y, 1f / 3f) : (7.787f * Y + 16f / 116f);
        float fz = Z > 0.008856f ? MathF.Pow(Z, 1f / 3f) : (7.787f * Z + 16f / 116f);

        float L = 116f * fy - 16f;
        float A = 500f * (fx - fy);
        float B_ = 200f * (fy - fz);

        // LAB to LCh
        float C = MathF.Sqrt(A * A + B_ * B_);
        float H = MathF.Atan2(B_, A);

        return new Vector3(L, C, H);
    }

    private static (float R, float G, float B) LChToRgb(Vector3 lch)
    {
        float L = lch.X;
        float C = lch.Y;
        float H = lch.Z;

        // LCh to LAB
        float A = C * MathF.Cos(H);
        float B_ = C * MathF.Sin(H);

        // LAB to XYZ
        float fy = (L + 16f) / 116f;
        float fx = A / 500f + fy;
        float fz = fy - B_ / 200f;

        float X = (fx > 0.206897f) ? fx * fx * fx : (fx - 16f / 116f) / 7.787f;
        float Y = (fy > 0.206897f) ? fy * fy * fy : (fy - 16f / 116f) / 7.787f;
        float Z = (fz > 0.206897f) ? fz * fz * fz : (fz - 16f / 116f) / 7.787f;

        // XYZ to RGB
        float R = X * 3.2404542f + Y * -1.5371385f + Z * -0.4985314f;
        float G = X * -0.9692660f + Y * 1.8760108f + Z * 0.0415560f;
        float B = X * 0.0556434f + Y * -0.2040259f + Z * 1.0572252f;

        float r = R > 0.0031308f ? (1.055f * MathF.Pow(R, 1f / 2.4f) - 0.055f) : (12.92f * R);
        float g = G > 0.0031308f ? (1.055f * MathF.Pow(G, 1f / 2.4f) - 0.055f) : (12.92f * G);
        float b = B > 0.0031308f ? (1.055f * MathF.Pow(B, 1f / 2.4f) - 0.055f) : (12.92f * B);

        return (r * 255f, g * 255f, b * 255f);
    }

    private static float MixHue(float h1, float h2, float t)
    {
        float diff = h2 - h1;
        if (diff > MathF.PI) diff -= MathF.Tau;
        else if (diff < -MathF.PI) diff += MathF.Tau;
        return h1 + diff * t;
    }

    private static PixelRegion StampBounds(StampSample stamp)
    {
        var radius = stamp.Size * 0.75f + 2.0f;
        var left = (int)MathF.Floor(stamp.X - radius);
        var top = (int)MathF.Floor(stamp.Y - radius);
        var right = (int)MathF.Ceiling(stamp.X + radius);
        var bottom = (int)MathF.Ceiling(stamp.Y + radius);
        return new PixelRegion(left, top, right - left + 1, bottom - top + 1);
    }

    private static SplinePoint ToSplinePoint(CanvasInputSample s)
        => new((float)s.X, (float)s.Y, (float)s.Pressure, (float)s.TiltX, (float)s.TiltY, (float)s.Twist);

    private static CanvasInputSample Lerp(SplinePoint a, SplinePoint b, float t, CanvasInputSample from, CanvasInputSample to)
    {
        var time = (long)(from.TimeMicros + (to.TimeMicros - from.TimeMicros) * t);
        return new CanvasInputSample(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
            a.Pressure + (b.Pressure - a.Pressure) * t,
            a.TiltX + (b.TiltX - a.TiltX) * t,
            a.TiltY + (b.TiltY - a.TiltY) * t,
            a.Twist + (b.Twist - a.Twist) * t,
            time, to.PointerId, to.Source, to.Phase);
    }

    private static SplinePoint CatmullRom(SplinePoint p0, SplinePoint p1, SplinePoint p2, SplinePoint p3, float t)
    {
        var t2 = t * t; var t3 = t2 * t;
        return new SplinePoint(
            Catmull(p0.X, p1.X, p2.X, p3.X, t, t2, t3),
            Catmull(p0.Y, p1.Y, p2.Y, p3.Y, t, t2, t3),
            Catmull(p0.Pressure, p1.Pressure, p2.Pressure, p3.Pressure, t, t2, t3),
            Catmull(p0.TiltX, p1.TiltX, p2.TiltX, p3.TiltX, t, t2, t3),
            Catmull(p0.TiltY, p1.TiltY, p2.TiltY, p3.TiltY, t, t2, t3),
            Catmull(p0.Twist, p1.Twist, p2.Twist, p3.Twist, t, t2, t3));
    }

    private static float Catmull(float p0, float p1, float p2, float p3, float t, float t2, float t3)
        => 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337);
            h ^= h >> 17; h *= 0xbf324c81u;
            h ^= h >> 13; h *= 0x9b2e1515u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0f;
        }
    }

    private sealed class ActiveStroke : IDisposable
    {
        private readonly BrushPreset _brush;
        private const int MaxCachedMasks = 16;
        private readonly Dictionary<int, SKBitmap> _maskCache = new();
        private readonly HashSet<SKBitmap> _ownedMasks = new();

        private readonly SKColor _baseColor;
        private SKColor _currentColor;
        private SKColorFilter? _mixedFilter;

        public ActiveStroke(BrushPreset brush, CanvasInputSample sample)
        {
            _brush = brush;

            StrokeRandom = Hash01((int)(sample.X * 997), (int)(sample.Y * 991));
            State = new StrokeState(
                (float)sample.X, (float)sample.Y,
                (float)sample.Pressure, (float)sample.TiltX, (float)sample.TiltY);

            var sp = new StrokePoint(
                (float)sample.X, (float)sample.Y, (float)sample.Pressure,
                (float)sample.TiltX, (float)sample.TiltY, (float)sample.Twist,
                0, 0, 0, 0, 0, StrokeRandom);
            var initSizeMul = brush.Dynamics.EvalSize(sp);
            var initSpacingMul = brush.Dynamics.EvalSpacing(sp);
            State.NextStampDistance = Math.Max(0.5f,
                (float)brush.Size
                * Math.Max(0.5f, initSizeMul)
                * Math.Max(0.01f, (float)brush.Spacing)
                * Math.Clamp(initSpacingMul, 0.05f, 4f));

            BaseMaskSize = Math.Max(1, Math.Min(256, (int)Math.Ceiling(brush.Size)));
            Mask = brush.Tip.GenerateMask(BaseMaskSize, (float)brush.Hardness);
            _baseColor = ToSkColor(brush.Color);
            _currentColor = _baseColor;
            var initDensity = (float)brush.DensityOfPaint;
            CarriedColor = SKColors.Transparent;
            Paint = new SKPaint
            {
                IsAntialias = true,
#pragma warning disable CS0618
                FilterQuality = brush.Quality == BrushQuality.High ? SKFilterQuality.High : SKFilterQuality.Low,
#pragma warning restore CS0618
                BlendMode = brush.BlendMode,
                Color = brush.BlendMode == SKBlendMode.DstOut ? SKColors.White : _baseColor,
                ColorFilter = brush.BlendMode == SKBlendMode.DstOut ? null : SKColorFilter.CreateBlendMode(_baseColor, SKBlendMode.SrcIn)
            };
        }

        public float StrokeRandom { get; }
        public StrokeState State;
        public int BaseMaskSize { get; }
        public SKBitmap Mask { get; }
        public SKPaint Paint { get; }
        public SKMatrix Matrix;
        public SKColor CarriedColor;
        public SKColor BaseColor => _baseColor;

        public bool Matches(BrushPreset brush)
            => ReferenceEquals(_brush, brush);

        public void UpdateColor(SKColor color)
        {
            if (_currentColor == color) return;
            _currentColor = color;
            _mixedFilter?.Dispose();
            _mixedFilter = SKColorFilter.CreateBlendMode(new SKColor(color.Red, color.Green, color.Blue, 255), SKBlendMode.SrcIn);
            Paint.ColorFilter = _mixedFilter;
        }

        public void UpdateOpacity(float opacity)
        {
            var colorAlphaRatio = _currentColor.Alpha / 255f;
            var alpha = (byte)Math.Clamp((int)(opacity * 255 * colorAlphaRatio), 0, 255);
            Paint.Color = _brush.BlendMode == SKBlendMode.DstOut
                ? new SKColor(255, 255, 255, alpha)
                : new SKColor(_currentColor.Red, _currentColor.Green, _currentColor.Blue, alpha);
        }

        public void UpdateMatrix(StampSample stamp)
        {
            var scale = stamp.Size / Math.Max(1, BaseMaskSize);
            var thickness = Math.Clamp((float)_brush.TipThickness * stamp.TipThicknessMultiplier, 0.01f, 1f);
            var scaleX = scale;
            var scaleY = scale;
            if (_brush.TipDirection == BrushTipDirection.Horizontal)
                scaleY *= thickness;
            else
                scaleX *= thickness;
            Matrix = SKMatrix.CreateTranslation(-BaseMaskSize * 0.5f, -BaseMaskSize * 0.5f);
            Matrix = Matrix.PostConcat(SKMatrix.CreateScale(scaleX, scaleY));
            if (Math.Abs(stamp.Angle) > 0.001f)
                Matrix = Matrix.PostConcat(SKMatrix.CreateRotationDegrees(stamp.Angle));
            Matrix = Matrix.PostConcat(SKMatrix.CreateTranslation(stamp.X, stamp.Y));
        }

        public SKBitmap MaskFor(float hardness)
        {
            var key = QuantizeHardness(hardness);
            if (_maskCache.TryGetValue(key, out var cached))
                return cached;

            var normalizedHardness = key / 255f;
            var tipMask = key == QuantizeHardness((float)_brush.Hardness)
                ? Mask
                : _brush.Tip.GenerateMask(BaseMaskSize, normalizedHardness);

            // CompoundBrushTip allocates a fresh bitmap each call; caller owns it.
            // Procedural/ImageBrushTip return their internal cache; tip owns it.
            if (!ReferenceEquals(tipMask, Mask) && _brush.Tip is CompoundBrushTip)
                _ownedMasks.Add(tipMask);

            if (_brush.Shape == null)
                return CacheOrReturnTemporary(key, tipMask);

            var shapeMask = _brush.Shape.GenerateMask(BaseMaskSize, normalizedHardness);
            var combined = MultiplyMasks(tipMask, shapeMask, BaseMaskSize);
            _ownedMasks.Add(combined);
            // shapeMask is ProceduralBrushTip-owned; tip holds it internally, never dispose
            DisposeTemporary(tipMask);
            return CacheOrReturnTemporary(key, combined);
        }

        public void ReleaseMask(SKBitmap mask)
        {
            if (_maskCache.ContainsValue(mask)) return;
            if (_ownedMasks.Remove(mask))
                mask.Dispose();
        }

        private void DisposeTemporary(SKBitmap mask)
        {
            if (ReferenceEquals(mask, Mask)) return;
            if (_maskCache.ContainsValue(mask)) return;
            if (_ownedMasks.Remove(mask))
                mask.Dispose();
        }

        private SKBitmap CacheOrReturnTemporary(int key, SKBitmap mask)
        {
            if (_maskCache.Count >= MaxCachedMasks)
                return mask;

            _maskCache[key] = mask;
            return mask;
        }

        private static int QuantizeHardness(float hardness)
            => Math.Clamp((int)MathF.Round(Math.Clamp(hardness, 0.001f, 1f) * 255f), 0, 255);

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

        public void Dispose()
        {
            foreach (var mask in _maskCache.Values)
                if (_ownedMasks.Remove(mask))
                    mask.Dispose();
            foreach (var mask in _ownedMasks)
                mask.Dispose();
            _maskCache.Clear();
            _ownedMasks.Clear();
            // Mask is caller-owned only when Tip is CompoundBrushTip (creates fresh each call)
            if (_brush.Tip is CompoundBrushTip)
                Mask.Dispose();
            Paint.Dispose();
        }

        private static SKColor ToSkColor(Color c) => new(c.R, c.G, c.B, c.A);

        private static float Hash01(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 1619 + y * 31337);
                h ^= h >> 17; h *= 0xbf324c81u;
                h ^= h >> 13; h *= 0x9b2e1515u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535.0f;
            }
        }
    }

    private readonly record struct StampSample(
        float X,
        float Y,
        float Size,
        float Opacity,
        float Angle,
        float Hardness,
        float SpacingMultiplier,
        float TipThicknessMultiplier);
    private readonly record struct SplinePoint(float X, float Y, float Pressure, float TiltX, float TiltY, float Twist);
}
