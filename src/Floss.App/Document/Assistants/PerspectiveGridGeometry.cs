using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Floss.App.Document.Assistants;

/// <summary>Flat perspective guide polylines clipped to the assistant view bounds.</summary>
internal static class PerspectiveGridGeometry
{
    internal readonly record struct GridCurve(IReadOnlyList<Point> Points, int Plane);

    private const double BoundsPadding = 0.2;

    public static Rect GetViewBounds(PaintingAssistant assistant)
    {
        var points = CollectHandles(assistant);
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var width = Math.Max(maxX - minX, 1);
        var height = Math.Max(maxY - minY, 1);
        var padX = width * BoundsPadding;
        var padY = height * BoundsPadding;
        return new Rect(minX - padX, minY - padY, width + padX * 2, height + padY * 2);
    }

    public static double ParallelLineSpacing(PaintingAssistant assistant)
    {
        var bounds = GetViewBounds(assistant);
        var extent = Math.Min(bounds.Width, bounds.Height);
        var subdivisions = Math.Clamp(assistant.GridSubdivisions, 2, 12);
        return Math.Max(extent / subdivisions, 12);
    }

    public static IEnumerable<GridCurve> EnumerateCurves(PaintingAssistant assistant)
    {
        if (assistant.TypeId is not (PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType))
            yield break;

        var bounds = GetViewBounds(assistant);
        var subdivisions = Math.Clamp(assistant.GridSubdivisions, 2, 12);
        var spacing = ParallelLineSpacing(assistant);

        switch (assistant.PerspectiveMode)
        {
            case PerspectiveAssistantMode.OnePoint:
                foreach (var curve in RadialFamily(assistant.HandleA, assistant.HandleB, bounds, subdivisions, plane: 1))
                    yield return curve;
                foreach (var curve in ParallelFamily(assistant.HandleA, assistant.HandleB, bounds, subdivisions, spacing, plane: 0))
                    yield return curve;
                yield return new GridCurve([assistant.HandleA, assistant.HandleB], 0);
                break;

            case PerspectiveAssistantMode.TwoPoint:
                foreach (var curve in RadialFamily(assistant.HandleA, assistant.HandleC, bounds, subdivisions, plane: 1))
                    yield return curve;
                foreach (var curve in RadialFamily(assistant.HandleB, assistant.HandleC, bounds, subdivisions, plane: 2))
                    yield return curve;
                foreach (var curve in ParallelFamily(assistant.HandleA, assistant.HandleB, bounds, subdivisions, spacing, plane: 0))
                    yield return curve;
                yield return new GridCurve([assistant.HandleA, assistant.HandleB], 0);
                break;

            case PerspectiveAssistantMode.ThreePoint:
                foreach (var curve in RadialFamily(assistant.HandleA, assistant.HandleC, bounds, subdivisions, plane: 1))
                    yield return curve;
                foreach (var curve in RadialFamily(assistant.HandleB, assistant.HandleC, bounds, subdivisions, plane: 2))
                    yield return curve;
                foreach (var curve in RadialFamily(assistant.HandleC, assistant.HandleA, bounds, subdivisions, plane: 0))
                    yield return curve;
                foreach (var curve in ParallelFamily(assistant.HandleA, assistant.HandleB, bounds, subdivisions, spacing, plane: 0))
                    yield return curve;
                yield return new GridCurve([assistant.HandleA, assistant.HandleB], 0);
                break;

            default:
                foreach (var curve in FreeQuadCurves(assistant, subdivisions))
                    yield return curve;
                break;
        }
    }

    public static IEnumerable<(Point A, Point B)> EnumerateSegments(PaintingAssistant assistant)
    {
        foreach (var curve in EnumerateCurves(assistant))
        {
            for (var i = 1; i < curve.Points.Count; i++)
                yield return (curve.Points[i - 1], curve.Points[i]);
        }
    }

    private static List<Point> CollectHandles(PaintingAssistant assistant)
    {
        var points = new List<Point> { assistant.HandleA, assistant.HandleB };
        if (assistant.HandleCount > 2)
            points.Add(assistant.HandleC);
        if (assistant.HandleCount > 3)
            points.Add(assistant.HandleD);
        return points;
    }

    private static IEnumerable<GridCurve> RadialFamily(
        Point vanishingPoint,
        Point toward,
        Rect bounds,
        int subdivisions,
        int plane)
    {
        var (minAngle, maxAngle) = AngularSpan(vanishingPoint, bounds);
        var towardAngle = Math.Atan2(toward.Y - vanishingPoint.Y, toward.X - vanishingPoint.X);
        if (maxAngle - minAngle < Math.PI * 0.25)
        {
            var spread = Math.PI * 0.5;
            minAngle = towardAngle - spread * 0.5;
            maxAngle = towardAngle + spread * 0.5;
        }

        for (var i = 0; i <= subdivisions; i++)
        {
            var t = (double)i / subdivisions;
            var angle = minAngle + (maxAngle - minAngle) * t;
            if (RayToBounds(vanishingPoint, angle, bounds) is not { } hit)
                continue;

            yield return new GridCurve([vanishingPoint, hit], plane);
        }
    }

    private static (double Min, double Max) AngularSpan(Point origin, Rect bounds)
    {
        var corners = new[]
        {
            new Point(bounds.Left, bounds.Top),
            new Point(bounds.Right, bounds.Top),
            new Point(bounds.Right, bounds.Bottom),
            new Point(bounds.Left, bounds.Bottom),
        };

        var angles = corners
            .Select(c => Math.Atan2(c.Y - origin.Y, c.X - origin.X))
            .OrderBy(a => a)
            .ToArray();

        var maxGap = 0.0;
        var gapEnd = angles[0];
        for (var i = 0; i < angles.Length; i++)
        {
            var cur = angles[i];
            var next = angles[(i + 1) % angles.Length];
            var gap = i == angles.Length - 1
                ? (next + Math.PI * 2) - cur
                : next - cur;
            if (gap > maxGap)
            {
                maxGap = gap;
                gapEnd = next;
            }
        }

        var min = gapEnd;
        var max = gapEnd + (Math.PI * 2 - maxGap);
        return (min, max);
    }

    private static IEnumerable<GridCurve> ParallelFamily(
        Point horizonA,
        Point horizonB,
        Rect bounds,
        int subdivisions,
        double spacing,
        int plane)
    {
        var dx = horizonB.X - horizonA.X;
        var dy = horizonB.Y - horizonA.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
            yield break;

        var dirX = dx / len;
        var dirY = dy / len;
        var perpX = -dirY;
        var perpY = dirX;

        for (var i = -subdivisions; i <= subdivisions; i++)
        {
            if (i == 0)
                continue;

            var offset = i * spacing;
            var seed = new Point(
                horizonA.X + perpX * offset,
                horizonA.Y + perpY * offset);
            if (LineRectClip(seed, new Point(dirX, dirY), bounds) is not { } segment)
                continue;

            yield return new GridCurve(segment, plane);
        }
    }

    private static IEnumerable<GridCurve> FreeQuadCurves(PaintingAssistant assistant, int subdivisions)
    {
        var a = assistant.HandleA;
        var b = assistant.HandleB;
        var c = assistant.HandleC;
        var d = assistant.HandleD;
        yield return new GridCurve([a, b], 0);
        yield return new GridCurve([b, c], 0);
        yield return new GridCurve([c, d], 0);
        yield return new GridCurve([d, a], 0);

        for (var t = 1; t < subdivisions; t++)
        {
            var u = t / (double)subdivisions;
            yield return new GridCurve([Lerp(a, d, u), Lerp(b, c, u)], 1);
            yield return new GridCurve([Lerp(a, b, u), Lerp(d, c, u)], 2);
        }
    }

    private static Point? RayToBounds(Point origin, double angle, Rect bounds)
    {
        var dirX = Math.Cos(angle);
        var dirY = Math.Sin(angle);
        var bestT = double.NegativeInfinity;

        if (Math.Abs(dirX) > 1e-9)
        {
            Consider(origin, dirX, dirY, (bounds.Left - origin.X) / dirX, bounds, vertical: true, ref bestT);
            Consider(origin, dirX, dirY, (bounds.Right - origin.X) / dirX, bounds, vertical: true, ref bestT);
        }

        if (Math.Abs(dirY) > 1e-9)
        {
            Consider(origin, dirX, dirY, (bounds.Top - origin.Y) / dirY, bounds, vertical: false, ref bestT);
            Consider(origin, dirX, dirY, (bounds.Bottom - origin.Y) / dirY, bounds, vertical: false, ref bestT);
        }

        if (bestT <= 1e-6)
            return null;

        return new Point(origin.X + dirX * bestT, origin.Y + dirY * bestT);
    }

    private static void Consider(
        Point origin,
        double dirX,
        double dirY,
        double t,
        Rect bounds,
        bool vertical,
        ref double bestT)
    {
        if (t <= 1e-6)
            return;

        var x = origin.X + dirX * t;
        var y = origin.Y + dirY * t;
        if (vertical)
        {
            if (y < bounds.Top - 1e-6 || y > bounds.Bottom + 1e-6)
                return;
        }
        else
        {
            if (x < bounds.Left - 1e-6 || x > bounds.Right + 1e-6)
                return;
        }

        if (t > bestT)
            bestT = t;
    }

    private static IReadOnlyList<Point>? LineRectClip(Point seed, Point dirUnit, Rect bounds)
    {
        var dirX = dirUnit.X;
        var dirY = dirUnit.Y;
        var minT = double.PositiveInfinity;
        var maxT = double.NegativeInfinity;

        void ConsiderBoth(double t)
        {
            if (double.IsInfinity(t) || double.IsNaN(t))
                return;

            var x = seed.X + dirX * t;
            var y = seed.Y + dirY * t;
            if (x < bounds.Left - 1e-6 || x > bounds.Right + 1e-6)
                return;
            if (y < bounds.Top - 1e-6 || y > bounds.Bottom + 1e-6)
                return;

            minT = Math.Min(minT, t);
            maxT = Math.Max(maxT, t);
        }

        if (Math.Abs(dirX) > 1e-9)
        {
            ConsiderBoth((bounds.Left - seed.X) / dirX);
            ConsiderBoth((bounds.Right - seed.X) / dirX);
        }

        if (Math.Abs(dirY) > 1e-9)
        {
            ConsiderBoth((bounds.Top - seed.Y) / dirY);
            ConsiderBoth((bounds.Bottom - seed.Y) / dirY);
        }

        if (maxT <= minT || double.IsInfinity(minT))
            return null;

        return
        [
            new Point(seed.X + dirX * minT, seed.Y + dirY * minT),
            new Point(seed.X + dirX * maxT, seed.Y + dirY * maxT),
        ];
    }

    private static Point Lerp(Point a, Point b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
