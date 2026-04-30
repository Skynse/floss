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
        var stamp = CreateStamp(stroke, brush, sample, velocity01);
        _stamps.Add(stamp);

        var dirty = StampBounds(stamp).ClipTo(layer.Width, layer.Height);
        if (dirty.IsEmpty) return PixelRegion.Empty;

        layer.Pixels.RenderWithSkia(dirty, canvas => RenderPreparedStamps(stroke, canvas));
        return dirty;
    }

    public PixelRegion EstimateSegmentRegion(DrawingLayer layer, BrushPreset brush, CanvasInputSample from, CanvasInputSample to)
    {
        var radius = Math.Max(1.0, brush.Size * 0.5 + brush.Size * Math.Max(0, brush.Spacing) + 2.0);
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
        {
            BeginStroke(brush, eraser, sample);
        }
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
        var p0 = new SplinePoint(
            stroke.State.LastX,
            stroke.State.LastY,
            stroke.State.LastPressure,
            stroke.State.LastTiltX,
            stroke.State.LastTiltY,
            (float)from.Twist);
        var p1 = ToSplinePoint(from);
        var p2 = ToSplinePoint(to);
        var p3 = new SplinePoint(
            (float)(to.X + (to.X - from.X)),
            (float)(to.Y + (to.Y - from.Y)),
            (float)to.Pressure,
            (float)to.TiltX,
            (float)to.TiltY,
            (float)to.Twist);

        var previous = p1;
        var dirty = PixelRegion.Empty;

        for (var i = 1; i <= subdivisions; i++)
        {
            var t = i / (float)subdivisions;
            var current = CatmullRom(p0, p1, p2, p3, t);
            var segmentDx = current.X - previous.X;
            var segmentDy = current.Y - previous.Y;
            var segmentLength = MathF.Sqrt(segmentDx * segmentDx + segmentDy * segmentDy);

            if (segmentLength > 0.0001f)
            {
                var consumed = stroke.State.NextStampDistance - stroke.State.DistanceLeftover;
                while (consumed <= segmentLength)
                {
                    var ratio = consumed / segmentLength;
                    var sample = Lerp(previous, current, ratio, from, to);
                    var stamp = CreateStamp(stroke, brush, sample, velocity01);
                    _stamps.Add(stamp);
                    dirty = dirty.Union(StampBounds(stamp));
                    stroke.State.NextStampDistance = StampSpacing(brush, stamp);
                    consumed += stroke.State.NextStampDistance;
                }

                stroke.State.DistanceLeftover = segmentLength - (consumed - stroke.State.NextStampDistance);
                if (stroke.State.DistanceLeftover >= stroke.State.NextStampDistance) stroke.State.DistanceLeftover = 0;
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

    private static float StampSpacing(BrushPreset brush, StampSample stamp)
    {
        var flow = Math.Clamp((float)brush.Flow, 0.01f, 1.0f);
        var flowCompensation = MathF.Sqrt(flow);
        return Math.Max(0.5f, stamp.Size * Math.Max(0.01f, (float)brush.Spacing) * flowCompensation);
    }

    private StampSample CreateStamp(ActiveStroke stroke, BrushPreset brush, CanvasInputSample sample, float velocity01)
    {
        var sizeModifier = stroke.Dynamics.Evaluate(DynamicOutputTarget.Size, sample, velocity01);
        var opacityModifier = stroke.Dynamics.Evaluate(DynamicOutputTarget.Opacity, sample, velocity01);
        var scatter = stroke.Dynamics.Evaluate(DynamicOutputTarget.Scatter, sample, velocity01) - 1.0f;
        var size = Math.Max(0.5f, (float)brush.Size * sizeModifier);
        var opacity = (float)Math.Clamp(brush.Opacity * brush.Flow * opacityModifier, 0, 1);
        var angle = (float)sample.Twist + stroke.Dynamics.Evaluate(DynamicOutputTarget.Angle, sample, velocity01) - 1.0f;

        var x = (float)sample.X;
        var y = (float)sample.Y;
        if (scatter > 0.001f)
        {
            var noise = Hash01((int)(x * 8), (int)(y * 8));
            var radians = noise * MathF.Tau;
            var amount = scatter * size;
            x += MathF.Cos(radians) * amount;
            y += MathF.Sin(radians) * amount;
        }

        return new StampSample(x, y, size, opacity, angle);
    }

    private void RenderPreparedStamps(ActiveStroke stroke, SKCanvas canvas)
    {
        for (var i = 0; i < _stamps.Count; i++)
        {
            var stamp = _stamps[i];
            if (stamp.Opacity <= 0 || stamp.Size <= 0) continue;

            stroke.UpdateOpacity(stamp.Opacity);
            stroke.UpdateMatrix(stamp);

            canvas.Save();
            canvas.Concat(in stroke.Matrix);
            canvas.DrawBitmap(stroke.Mask, 0, 0, stroke.Paint);
            canvas.Restore();
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

    private static SplinePoint ToSplinePoint(CanvasInputSample sample)
        => new((float)sample.X, (float)sample.Y, (float)sample.Pressure, (float)sample.TiltX, (float)sample.TiltY, (float)sample.Twist);

    private static CanvasInputSample Lerp(SplinePoint a, SplinePoint b, float t, CanvasInputSample from, CanvasInputSample to)
    {
        var pressure = a.Pressure + (b.Pressure - a.Pressure) * t;
        var tiltX = a.TiltX + (b.TiltX - a.TiltX) * t;
        var tiltY = a.TiltY + (b.TiltY - a.TiltY) * t;
        var twist = a.Twist + (b.Twist - a.Twist) * t;
        var time = (long)(from.TimeMicros + (to.TimeMicros - from.TimeMicros) * t);
        return new CanvasInputSample(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            pressure,
            tiltX,
            tiltY,
            twist,
            time,
            to.PointerId,
            to.Source,
            to.Phase);
    }

    private static SplinePoint CatmullRom(SplinePoint p0, SplinePoint p1, SplinePoint p2, SplinePoint p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return new SplinePoint(
            Catmull(p0.X, p1.X, p2.X, p3.X, t, t2, t3),
            Catmull(p0.Y, p1.Y, p2.Y, p3.Y, t, t2, t3),
            Catmull(p0.Pressure, p1.Pressure, p2.Pressure, p3.Pressure, t, t2, t3),
            Catmull(p0.TiltX, p1.TiltX, p2.TiltX, p3.TiltX, t, t2, t3),
            Catmull(p0.TiltY, p1.TiltY, p2.TiltY, p3.TiltY, t, t2, t3),
            Catmull(p0.Twist, p1.Twist, p2.Twist, p3.Twist, t, t2, t3));
    }

    private static float Catmull(float p0, float p1, float p2, float p3, float t, float t2, float t3)
        => 0.5f * (2.0f * p1 + (-p0 + p2) * t + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2 + (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3);

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337);
            h ^= h >> 17;
            h *= 0xbf324c81u;
            h ^= h >> 13;
            h *= 0x9b2e1515u;
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
            State = new StrokeState((float)sample.X, (float)sample.Y, (float)sample.Pressure, (float)sample.TiltX, (float)sample.TiltY);
            Dynamics = DynamicsMatrix.FromBrush(brush);
            State.NextStampDistance = StampSpacing(
                brush,
                new StampSample(0, 0, Math.Max(0.5f, (float)brush.Size * Dynamics.Evaluate(DynamicOutputTarget.Size, sample, 0)), 1, 0));
            Mask = brush.Tip.GenerateMask(Math.Max(1, (int)Math.Ceiling(brush.Size)), (float)brush.Hardness);
            _baseColor = ToSkColor(brush.Color);
            Paint = new SKPaint
            {
                IsAntialias = true,
                BlendMode = eraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
                Color = eraser ? SKColors.White : _baseColor,
                ColorFilter = eraser ? null : SKColorFilter.CreateBlendMode(_baseColor, SKBlendMode.SrcIn)
            };
        }

        public StrokeState State;
        public DynamicsMatrix Dynamics { get; }
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
            var scale = stamp.Size / Math.Max(1, Mask.Width);
            Matrix = SKMatrix.CreateTranslation(-Mask.Width * 0.5f, -Mask.Height * 0.5f);
            Matrix = Matrix.PostConcat(SKMatrix.CreateScale(scale, scale));
            if (Math.Abs(stamp.Angle) > 0.001f)
                Matrix = Matrix.PostConcat(SKMatrix.CreateRotationDegrees(stamp.Angle));
            Matrix = Matrix.PostConcat(SKMatrix.CreateTranslation(stamp.X, stamp.Y));
        }

        public void Dispose()
        {
            if (_brush.Tip is CompoundBrushTip)
                Mask.Dispose();
            Paint.ColorFilter?.Dispose();
            Paint.Dispose();
        }

        private static SKColor ToSkColor(Color color)
            => new(color.R, color.G, color.B, color.A);
    }

    private readonly record struct StampSample(float X, float Y, float Size, float Opacity, float Angle);
    private readonly record struct SplinePoint(float X, float Y, float Pressure, float TiltX, float TiltY, float Twist);
}
