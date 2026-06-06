using System;
using System.Collections.Generic;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.SmartShape;

/// <summary>
/// Maps original pen samples onto a fitted path so brush dynamics follow the drawn stroke.
/// </summary>
internal static class SmartShapeSampleRemap
{
    public static List<CanvasInputSample> RemapOntoShape(
        IReadOnlyList<Vec2> shapePath,
        IReadOnlyList<CanvasInputSample> rawSamples,
        DrawingLayer layer,
        bool strokeClosed)
    {
        if (shapePath.Count < 2 || rawSamples.Count == 0)
            return [];

        if (rawSamples.Count == 1)
        {
            return
            [
                ToDocumentSample(rawSamples[0] with
                {
                    X = shapePath[0].X,
                    Y = shapePath[0].Y,
                    Phase = CanvasInputPhase.Move
                }, layer)
            ];
        }

        var (shapePts, shapeLoop) = NormalizeClosedPath(shapePath, strokeClosed);
        var (rawPts, rawLoop) = NormalizeRawPath(rawSamples, strokeClosed);
        if (shapePts.Count < 2)
            return [];

        var shapeArc = BuildArcTable(shapePts, shapeLoop);
        var rawArc = BuildArcTable(rawPts, rawLoop);
        if (shapeArc.Total <= 1e-6 || rawArc.Total <= 1e-6)
            return FlatDocumentSamples(shapePath, rawSamples, layer);

        var phase = shapeLoop && rawLoop
            ? FindPhaseDistance(rawSamples, shapePts[0])
            : 0.0;

        var output = new List<CanvasInputSample>(shapePath.Count);
        var walked = 0.0;
        for (var i = 0; i < shapePath.Count; i++)
        {
            if (i > 0)
                walked += Dist(shapePath[i - 1], shapePath[i]);

            var fraction = shapeArc.Total <= 1e-6 ? 0.0 : walked / shapeArc.Total;
            var rawDistance = shapeLoop && rawLoop
                ? Mod(rawArc.Total, phase + fraction * rawArc.Total)
                : fraction * rawArc.Total;
            var source = SampleAtDistance(rawSamples, rawPts, rawArc, rawDistance, rawLoop);
            output.Add(ToDocumentSample(source with
            {
                X = shapePath[i].X,
                Y = shapePath[i].Y,
                Phase = CanvasInputPhase.Move
            }, layer));
        }

        return output;
    }

    public static List<CanvasInputSample> ToDocumentSamples(
        IReadOnlyList<CanvasInputSample> rawSamples,
        DrawingLayer layer)
    {
        var output = new List<CanvasInputSample>(rawSamples.Count);
        foreach (var sample in rawSamples)
            output.Add(ToDocumentSample(sample with { Phase = CanvasInputPhase.Move }, layer));
        return output;
    }

    private static List<CanvasInputSample> FlatDocumentSamples(
        IReadOnlyList<Vec2> shapePath,
        IReadOnlyList<CanvasInputSample> rawSamples,
        DrawingLayer layer)
    {
        var fallback = rawSamples[0];
        var output = new List<CanvasInputSample>(shapePath.Count);
        foreach (var pt in shapePath)
        {
            output.Add(ToDocumentSample(fallback with
            {
                X = pt.X,
                Y = pt.Y,
                Phase = CanvasInputPhase.Move
            }, layer));
        }
        return output;
    }

    private static (List<Vec2> Points, bool Closed) NormalizeClosedPath(IReadOnlyList<Vec2> path, bool strokeClosed)
    {
        if (path.Count >= 2 && NearlySame(path[0], path[^1]))
        {
            var open = new List<Vec2>(path.Count - 1);
            for (var i = 0; i < path.Count - 1; i++)
                open.Add(path[i]);
            return (open, true);
        }

        return ([.. path], strokeClosed && path.Count >= 3);
    }

    private static (List<Vec2> Points, bool Closed) NormalizeRawPath(
        IReadOnlyList<CanvasInputSample> raw,
        bool strokeClosed)
    {
        var pts = new List<Vec2>(raw.Count);
        foreach (var s in raw)
            pts.Add(new Vec2(s.X, s.Y));

        if (pts.Count >= 2 && NearlySame(pts[0], pts[^1]))
        {
            var open = new List<Vec2>(pts.Count - 1);
            for (var i = 0; i < pts.Count - 1; i++)
                open.Add(pts[i]);
            return (open, true);
        }

        return (pts, strokeClosed && pts.Count >= 3);
    }

    private readonly record struct ArcTable(double[] Cumulative, double Total, bool Closed);

    private static ArcTable BuildArcTable(IReadOnlyList<Vec2> pts, bool closed)
    {
        var cumulative = new double[pts.Count];
        var total = 0.0;
        for (var i = 1; i < pts.Count; i++)
        {
            total += Dist(pts[i - 1], pts[i]);
            cumulative[i] = total;
        }

        if (closed && pts.Count > 1)
            total += Dist(pts[^1], pts[0]);

        return new ArcTable(cumulative, total, closed);
    }

    private static double FindPhaseDistance(
        IReadOnlyList<CanvasInputSample> raw,
        Vec2 shapeStart)
    {
        var bestDist = double.MaxValue;
        var phase = 0.0;
        var walked = 0.0;
        for (var i = 0; i < raw.Count; i++)
        {
            if (i > 0)
            {
                walked += Dist(
                    new Vec2(raw[i - 1].X, raw[i - 1].Y),
                    new Vec2(raw[i].X, raw[i].Y));
            }

            var d = Dist(new Vec2(raw[i].X, raw[i].Y), shapeStart);
            if (d >= bestDist)
                continue;
            bestDist = d;
            phase = walked;
        }

        return phase;
    }

    private static CanvasInputSample SampleAtDistance(
        IReadOnlyList<CanvasInputSample> raw,
        IReadOnlyList<Vec2> rawPts,
        ArcTable rawArc,
        double distance,
        bool closed)
    {
        if (raw.Count == 1)
            return raw[0];

        distance = closed ? Mod(rawArc.Total, distance) : Math.Clamp(distance, 0, rawArc.Total);

        for (var i = 1; i < rawPts.Count; i++)
        {
            var segStart = rawArc.Cumulative[i - 1];
            var segEnd = rawArc.Cumulative[i];
            if (distance > segEnd + 1e-9)
                continue;

            var segLen = segEnd - segStart;
            var t = segLen <= 1e-6 ? 0.0 : (distance - segStart) / segLen;
            return LerpSample(raw[i - 1], raw[i], t);
        }

        if (closed && rawPts.Count > 1)
        {
            var segStart = rawArc.Cumulative[^1];
            var segLen = rawArc.Total - segStart;
            var t = segLen <= 1e-6 ? 0.0 : (distance - segStart) / segLen;
            return LerpSample(raw[^1], raw[0], t);
        }

        return raw[^1];
    }

    private static CanvasInputSample LerpSample(CanvasInputSample from, CanvasInputSample to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new CanvasInputSample(
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
    }

    private static CanvasInputSample ToDocumentSample(CanvasInputSample sample, DrawingLayer layer)
        => new(
            sample.X - layer.OffsetX,
            sample.Y - layer.OffsetY,
            sample.Pressure,
            sample.TiltX,
            sample.TiltY,
            sample.Twist,
            sample.TimeMicros,
            sample.PointerId,
            sample.Source,
            sample.Phase);

    private static double Mod(double modulus, double value)
    {
        if (modulus <= 1e-10)
            return 0.0;
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }

    private static bool NearlySame(Vec2 a, Vec2 b)
        => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

    private static double Dist(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
