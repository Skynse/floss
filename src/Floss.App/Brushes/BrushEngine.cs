using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Brushes;

public sealed class BrushEngine : IDisposable
{
    private const int InitialStampCapacity = 256;
    private readonly List<StampSample> _stamps = new(InitialStampCapacity);
    private ActiveStroke? _activeStroke;

    public void BeginStroke(BrushPreset brush, bool eraser, CanvasInputSample sample)
    {
        EndStroke();
        _activeStroke = new ActiveStroke(brush, eraser, sample);
    }

    public void EndStroke()
    {
        _activeStroke?.Dispose();
        _activeStroke = null;
        _stamps.Clear();
    }

    public PixelRegion RasterizeSegment(
        DrawingLayer layer,
        BrushPreset brush,
        bool eraser,
        CanvasInputSample from,
        CanvasInputSample to)
    {
        EnsureStroke(brush, eraser, from);
        var stroke = _activeStroke!;
        _stamps.Clear();

        var dirty = BuildStamps(stroke, brush, from, to);
        if (dirty.IsEmpty || _stamps.Count == 0) return PixelRegion.Empty;

        var clippedDirty = dirty.ClipTo(layer.Width, layer.Height);
        if (clippedDirty.IsEmpty) return PixelRegion.Empty;

        layer.Pixels.RenderWithSkia(clippedDirty, canvas => RenderPreparedStamps(stroke, canvas));
        return clippedDirty;
    }

    public PixelRegion RasterizeDab(
        DrawingLayer layer,
        BrushPreset brush,
        bool eraser,
        CanvasInputSample sample,
        double velocity)
    {
        EnsureStroke(brush, eraser, sample);
        var stroke = _activeStroke!;
        _stamps.Clear();

        var velocity01 = (float)Math.Clamp(velocity / 5000.0, 0, 1);
        var sp = BuildStrokePoint(stroke, sample, velocity01);
        var stamp = CreateStamp(stroke, brush, sp);
        _stamps.Add(stamp);

        var dirty = StampBounds(stamp).ClipTo(layer.Width, layer.Height);
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
        return new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1)
            .ClipTo(layer.Width, layer.Height);
    }

    public PixelRegion EstimateDabRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample sample)
        => EstimateSegmentRegion(layer, brush, sample, sample);

    public void Dispose() => EndStroke();

    private void EnsureStroke(BrushPreset brush, bool eraser, CanvasInputSample sample)
    {
        if (_activeStroke == null || !_activeStroke.Matches(brush, eraser))
            BeginStroke(brush, eraser, sample);
    }

    private PixelRegion BuildStamps(ActiveStroke stroke, BrushPreset brush, CanvasInputSample from, CanvasInputSample to)
    {
        if (from.Pressure <= 0 && to.Pressure <= 0) return PixelRegion.Empty;

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var elapsedSeconds = Math.Max(0.001, (to.TimeMicros - from.TimeMicros) / 1_000_000.0);
        var velocity01 = Math.Clamp((float)(distance / elapsedSeconds / 5000.0), 0, 1);
        var subdivisions = Math.Max(8, Math.Min(96, (int)Math.Ceiling(distance / 2.0)));

        if (distance > 0.001)
        {
            var currentAngle = MathF.Atan2((float)dy, (float)dx);
            //stroke.State.DrawingAngle = MathF.Atan2((float)dy, (float)dx);
            //
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
        var scatter = dyn.Scatter.IsEnabled ? dyn.EvalScatter(sp) : 0f;
        var rotDeg = dyn.EvalRotationDeg(sp);
        var size = Math.Max(0.5f, (float)brush.Size * sizeMul);
        var opacity = (float)Math.Clamp(brush.Opacity * brush.Flow * opacMul * flowMul, 0, 1);
        //var angle      = (float)sp.Twist + rotDeg;
        var trajectoryDeg = sp.DrawingAngle * (180 / MathF.PI);
        var baseAngle = brush.FollowTrajectory ? trajectoryDeg : (float)sp.Twist;
        var angle = baseAngle + rotDeg;
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
            Math.Clamp(spacingMul, 0.05f, 4f));
    }

    private static double EstimateBrushRadius(BrushPreset brush)
    {
        var maxSize = brush.Size * Math.Max(1.0, brush.Dynamics.Size.MaxOutput);
        var spacing = maxSize * Math.Max(0.01, brush.Spacing) * Math.Max(1.0, brush.Dynamics.Spacing.MaxOutput);
        var scatter = brush.Dynamics.Scatter.IsEnabled ? maxSize * Math.Max(0.0, brush.Dynamics.Scatter.MaxOutput) : 0.0;
        return Math.Max(1.0, maxSize * 0.75 + spacing + scatter + 3.0);
    }

    private void RenderPreparedStamps(ActiveStroke stroke, SKCanvas canvas)
    {
        for (var i = 0; i < _stamps.Count; i++)
        {
            var stamp = _stamps[i];
            if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

            stroke.UpdateOpacity(stamp.Opacity);
            stroke.UpdateMatrix(stamp);
            var mask = stroke.MaskFor(stamp.Hardness);

            canvas.Save();
            canvas.Concat(in stroke.Matrix);
            canvas.DrawBitmap(mask, 0, 0, stroke.Paint);
            canvas.Restore();

            stroke.ReleaseMask(mask);
        }
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
        private readonly bool _eraser;
        private readonly SKColor _baseColor;

        public ActiveStroke(BrushPreset brush, bool eraser, CanvasInputSample sample)
        {
            _brush = brush;
            _eraser = eraser;
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

            BaseMaskSize = Math.Max(1, (int)Math.Ceiling(brush.Size));
            Mask = brush.Tip.GenerateMask(BaseMaskSize, (float)brush.Hardness);
            _baseColor = ToSkColor(brush.Color);
            Paint = new SKPaint
            {
                IsAntialias = true,
                BlendMode = eraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
                Color = eraser ? SKColors.White : _baseColor,
                ColorFilter = eraser ? null : SKColorFilter.CreateBlendMode(_baseColor, SKBlendMode.SrcIn)
            };
        }

        public float StrokeRandom { get; }
        public StrokeState State;
        public int BaseMaskSize { get; }
        public SKBitmap Mask { get; }
        public SKPaint Paint { get; }
        public SKMatrix Matrix;

        public bool Matches(BrushPreset brush, bool eraser)
            => ReferenceEquals(_brush, brush) && _eraser == eraser;

        public void UpdateOpacity(float opacity)
        {
            var alpha = (byte)Math.Clamp((int)(opacity * 255), 0, 255);
            Paint.Color = _eraser
                ? new SKColor(255, 255, 255, alpha)
                : new SKColor(_baseColor.Red, _baseColor.Green, _baseColor.Blue, alpha);
        }

        public void UpdateMatrix(StampSample stamp)
        {
            var scale = stamp.Size / Math.Max(1, BaseMaskSize);
            Matrix = SKMatrix.CreateTranslation(-BaseMaskSize * 0.5f, -BaseMaskSize * 0.5f);
            Matrix = Matrix.PostConcat(SKMatrix.CreateScale(scale, scale));
            if (Math.Abs(stamp.Angle) > 0.001f)
                Matrix = Matrix.PostConcat(SKMatrix.CreateRotationDegrees(stamp.Angle));
            Matrix = Matrix.PostConcat(SKMatrix.CreateTranslation(stamp.X, stamp.Y));
        }

        public SKBitmap MaskFor(float hardness) => _brush.Tip.GenerateMask(BaseMaskSize, hardness);

        public void ReleaseMask(SKBitmap mask)
        {
            if (!ReferenceEquals(mask, Mask) && _brush.Tip is CompoundBrushTip)
                mask.Dispose();
        }

        public void Dispose()
        {
            if (_brush.Tip is CompoundBrushTip)
                Mask.Dispose();
            Paint.ColorFilter?.Dispose();
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
        float SpacingMultiplier);
    private readonly record struct SplinePoint(float X, float Y, float Pressure, float TiltX, float TiltY, float Twist);
}
