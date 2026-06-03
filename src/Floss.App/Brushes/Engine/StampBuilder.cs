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
    private PixelRegion BuildStamps(ActiveStroke stroke, BrushPreset brush, CanvasInputSample from, CanvasInputSample to, bool ensureEndpoint = false)
    {
        if (from.Pressure <= 0 && to.Pressure <= 0) return PixelRegion.Empty;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var elapsedSeconds = Math.Max(0.001, (to.TimeMicros - from.TimeMicros) / 1_000_000.0);
        var velocity01 = Math.Clamp((float)(distance / elapsedSeconds / 5000.0), 0, 1);

        if (distance <= 0.001)
            return BuildStampsContinuous(stroke, brush, from, to, velocity01);

        var currentAngle = MathF.Atan2((float)dy, (float)dx);
        stroke.State.DrawingAngle = LerpAngle(stroke.State.DrawingAngle, currentAngle, 0.5f);

        if (IsStraightSegment(stroke, from, to))
            return BuildStampsLinear(stroke, brush, from, to, distance, velocity01, ensureEndpoint);

        // Huge brushes: one dab per input segment when the footprint already
        // covers the move distance (Krita-style — avoids dozens of redundant
        // full-size stamps along a short stroke).
        if (distance > 0.001)
        {
            var endpointSp = BuildStrokePoint(stroke, to, velocity01);
            var endpointStamp = CreateStamp(stroke, brush, endpointSp);
            if (ShouldCollapseToSingleStamp(endpointStamp, (float)distance, brush))
            {
                var collapsedDirty = PixelRegion.Empty;
                if (!BrushSpacing.IsStampTooSmall(endpointStamp.Size))
                {
                    _stamps.Add(endpointStamp);
                    collapsedDirty = StampBounds(endpointStamp);
                    stroke.State.TotalDistance += (float)distance;
                    stroke.State.DabSeqNo++;
                }

                stroke.State.DistanceLeftover = 0;
                stroke.State.NextStampDistance = StampSpacing(brush, endpointStamp);
                stroke.State.LastX = (float)to.X;
                stroke.State.LastY = (float)to.Y;
                stroke.State.LastPressure = (float)to.Pressure;
                stroke.State.LastTiltX = (float)to.TiltX;
                stroke.State.LastTiltY = (float)to.TiltY;
                return collapsedDirty;
            }
        }

        // Subdivide based on expected stamp count, not raw pixel distance.
        // A 1000px brush with 250px spacing needs ~4 stamps; 96 subdivisions
        // would waste time on 90+ pointless Catmull-Rom evaluations.
        var stampSpacing = Math.Max(1, stroke.State.NextStampDistance);
        var estimatedStamps = distance / stampSpacing;
        var subdivisions = Math.Max(4, Math.Min(48, (int)Math.Ceiling(estimatedStamps * 2)));

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

                    if (!BrushSpacing.IsStampTooSmall(stamp.Size))
                    {
                        _stamps.Add(stamp);
                        dirty = dirty.Union(StampBounds(stamp));
                        stroke.State.TotalDistance += stroke.State.NextStampDistance;
                        stroke.State.DabSeqNo++;
                    }

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

    private static bool IsStraightSegment(ActiveStroke stroke, CanvasInputSample from, CanvasInputSample to)
    {
        var segDx = to.X - from.X;
        var segDy = to.Y - from.Y;
        var segLenSq = segDx * segDx + segDy * segDy;
        if (segLenSq < 1e-6) return true;

        var prevDx = from.X - stroke.State.LastX;
        var prevDy = from.Y - stroke.State.LastY;
        if (prevDx * prevDx + prevDy * prevDy < 1e-4) return true;

        var cross = prevDx * segDy - prevDy * segDx;
        var tol = Math.Sqrt(segLenSq) * 0.01 + 0.5;
        return Math.Abs(cross) <= tol;
    }

    private PixelRegion BuildStampsLinear(
        ActiveStroke stroke,
        BrushPreset brush,
        CanvasInputSample from,
        CanvasInputSample to,
        double distance,
        float velocity01,
        bool ensureEndpoint)
    {
        if (distance > 0.001)
        {
            var endpointSp = BuildStrokePoint(stroke, to, velocity01);
            var endpointStamp = CreateStamp(stroke, brush, endpointSp);
            if (ShouldCollapseToSingleStamp(endpointStamp, (float)distance, brush))
            {
                var collapsedDirty = PixelRegion.Empty;
                if (!BrushSpacing.IsStampTooSmall(endpointStamp.Size))
                {
                    _stamps.Add(endpointStamp);
                    collapsedDirty = StampBounds(endpointStamp);
                    stroke.State.TotalDistance += (float)distance;
                    stroke.State.DabSeqNo++;
                }

                stroke.State.DistanceLeftover = 0;
                stroke.State.NextStampDistance = StampSpacing(brush, endpointStamp);
                stroke.State.LastX = (float)to.X;
                stroke.State.LastY = (float)to.Y;
                stroke.State.LastPressure = (float)to.Pressure;
                stroke.State.LastTiltX = (float)to.TiltX;
                stroke.State.LastTiltY = (float)to.TiltY;
                return collapsedDirty;
            }
        }

        var dirty = PixelRegion.Empty;
        var consumed = stroke.State.NextStampDistance - stroke.State.DistanceLeftover;
        while (consumed <= distance)
        {
            var ratio = (float)(consumed / distance);
            var sample = LerpCanvas(from, to, ratio);
            var sp = BuildStrokePoint(stroke, sample, velocity01);
            var stamp = CreateStamp(stroke, brush, sp);

            if (!BrushSpacing.IsStampTooSmall(stamp.Size))
            {
                _stamps.Add(stamp);
                dirty = dirty.Union(StampBounds(stamp));
                stroke.State.TotalDistance += stroke.State.NextStampDistance;
                stroke.State.DabSeqNo++;
            }

            stroke.State.NextStampDistance = StampSpacing(brush, stamp);
            consumed += stroke.State.NextStampDistance;
        }

        stroke.State.DistanceLeftover = (float)distance - (consumed - stroke.State.NextStampDistance);
        if (stroke.State.DistanceLeftover >= stroke.State.NextStampDistance)
            stroke.State.DistanceLeftover = 0;

        if (ensureEndpoint && _stamps.Count > 0)
            dirty = dirty.Inflate((int)(_stamps[^1].Size * 0.25f + 1));

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
        => BrushSpacing.EffectiveDistance(brush, stamp.Size, stamp.SpacingMultiplier);

    private PixelRegion BuildStampsContinuous(
        ActiveStroke stroke,
        BrushPreset brush,
        CanvasInputSample from,
        CanvasInputSample to,
        float velocity01)
    {
        if (!brush.ContinuousSpraying)
            return PixelRegion.Empty;

        var elapsedMs = Math.Max(0, (to.TimeMicros - from.TimeMicros) / 1000.0);
        if (elapsedMs <= 0)
            return PixelRegion.Empty;

        var sp = BuildStrokePoint(stroke, to, velocity01);
        var stamp = CreateStamp(stroke, brush, sp);
        var spacing = StampSpacing(brush, stamp);

        const float referenceVelocity = 1000f;
        var msPerDab = spacing / referenceVelocity * 1000f;
        msPerDab = Math.Clamp(msPerDab, 12, 400);

        stroke.State.TimeLeftoverMs += elapsedMs;
        var dirty = PixelRegion.Empty;
        while (stroke.State.TimeLeftoverMs >= msPerDab)
        {
            stroke.State.TimeLeftoverMs -= msPerDab;
            sp = BuildStrokePoint(stroke, to, velocity01);
            stamp = CreateStamp(stroke, brush, sp);
            if (!BrushSpacing.IsStampTooSmall(stamp.Size))
            {
                _stamps.Add(stamp);
                dirty = dirty.Union(StampBounds(stamp));
                stroke.State.DabSeqNo++;
            }
        }

        stroke.State.LastX = (float)to.X;
        stroke.State.LastY = (float)to.Y;
        stroke.State.LastPressure = (float)to.Pressure;
        stroke.State.LastTiltX = (float)to.TiltX;
        stroke.State.LastTiltY = (float)to.TiltY;
        return dirty;
    }

    private static bool ShouldCollapseToSingleStamp(StampSample stamp, float distance, BrushPreset brush)
    {
        if (distance < 0.5f)
            return false;

        var spacing = BrushSpacing.EffectiveDistance(brush, stamp.Size, stamp.SpacingMultiplier);
        if (distance <= spacing * 0.85f)
            return true;

        return stamp.Size >= 64f && distance <= stamp.Size * 0.75f;
    }

    private static StampSample CreateStamp(ActiveStroke stroke, BrushPreset brush, in StrokePoint sp)
    {
        var dyn = brush.Dynamics;
        var paramLookup = stroke.ParamGraphLookup;
        var sizeMul = EvalParameter(paramLookup, BrushParameterTarget.Size, sp, dyn.EvalSize(sp));
        var opacMul = EvalParameter(paramLookup, BrushParameterTarget.Opacity, sp, dyn.EvalOpacity(sp));
        var flowMul = EvalParameter(paramLookup, BrushParameterTarget.Flow, sp, dyn.EvalFlow(sp));
        var hardness = EvalParameter(paramLookup, BrushParameterTarget.Hardness, sp,
            dyn.Hardness.IsEnabled ? dyn.EvalHardness(sp) : (float)brush.Hardness);
        var spacingMul = EvalParameter(paramLookup, BrushParameterTarget.Spacing, sp, dyn.EvalSpacing(sp));
        var tipDensityMul = EvalParameter(paramLookup, BrushParameterTarget.TipDensity, sp,
            dyn.TipDensity.IsEnabled ? dyn.EvalTipDensity(sp) : 1f);
        var tipThicknessMul = EvalParameter(paramLookup, BrushParameterTarget.TipThickness, sp,
            dyn.TipThickness.IsEnabled ? dyn.EvalTipThickness(sp) : 1f);
        var scatter = EvalParameter(paramLookup, BrushParameterTarget.Scatter, sp,
            dyn.Scatter.IsEnabled ? dyn.EvalScatter(sp) : 0f);
        var rotDeg = EvalParameter(paramLookup, BrushParameterTarget.Angle, sp, dyn.EvalRotationDeg(sp));
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
            Math.Clamp(tipThicknessMul, 0.01f, 4f),
            SelectTipIndex(brush, sp));
    }

    private static float EvalParameter(Dictionary<BrushParameterTarget, BrushParameterGraph> lookup, BrushParameterTarget target, in StrokePoint sp, float fallback)
        => lookup.TryGetValue(target, out var graph) ? graph.Evaluate(sp, fallback) : fallback;

    private static int SelectTipIndex(BrushPreset brush, in StrokePoint sp)
    {
        if (brush.TipSelectionMode == BrushTipSelectionMode.Single || brush.Tips.Count <= 1)
            return 0;

        return brush.TipSelectionMode switch
        {
            BrushTipSelectionMode.Sequential => sp.DabSeqNo % brush.Tips.Count,
            BrushTipSelectionMode.Random => Math.Clamp((int)(Hash01(sp.DabSeqNo, (int)(sp.StrokeRandom * 100_000f)) * brush.Tips.Count), 0, brush.Tips.Count - 1),
            _ => 0
        };
    }

    private static double EstimateBrushRadius(BrushPreset brush)
    {
        var maxSize = brush.Size * Math.Max(1.0, brush.Dynamics.Size.MaxOutput);
        var spacing = BrushSpacing.EstimateDistance(brush, (float)maxSize)
            * Math.Max(1.0f, (float)brush.Dynamics.Spacing.MaxOutput);
        var scatter = brush.Dynamics.Scatter.IsEnabled ? maxSize * Math.Max(0.0, brush.Dynamics.Scatter.MaxOutput) : 0.0;
        return Math.Max(1.0, maxSize * 0.75 + spacing + scatter + 3.0);
    }
    private static float LerpAngle(float a, float b, float t)
    {
        var delta = b - a;
        if (delta > MathF.PI) delta -= MathF.Tau;
        else if (delta < -MathF.PI) delta += MathF.Tau;
        return a + delta * t;
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

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor(value / (double)divisor);

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

    private static CanvasInputSample LerpCanvas(CanvasInputSample from, CanvasInputSample to, float t)
        => new(
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            from.Pressure + (to.Pressure - from.Pressure) * t,
            from.TiltX + (to.TiltX - from.TiltX) * t,
            from.TiltY + (to.TiltY - from.TiltY) * t,
            from.Twist + (to.Twist - from.Twist) * t,
            (long)(from.TimeMicros + (to.TimeMicros - from.TimeMicros) * t),
            to.PointerId,
            to.Source,
            to.Phase);

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
}
