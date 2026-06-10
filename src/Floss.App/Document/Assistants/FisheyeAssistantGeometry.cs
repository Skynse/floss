using System;
using System.Collections.Generic;
using Avalonia;

namespace Floss.App.Document.Assistants;

/// <summary>
/// fisheye: warp the flat perspective grid inside a lens circle (not a separate sphere).
/// </summary>
internal static class FisheyeAssistantGeometry
{
    private const int SamplesPerSegment = 40;

    internal readonly record struct LensFrame(Point Center, double Radius, double FovRadians);

    public static LensFrame GetLensFrame(PaintingAssistant assistant)
    {
        var bounds = PerspectiveGridGeometry.GetViewBounds(assistant);
        var center = bounds.Center;
        var radius = Math.Max(Math.Max(bounds.Width, bounds.Height) * 0.5, 1);
        var fovRad = Math.Clamp(assistant.FovDegrees, 10, 360) * Math.PI / 180;
        return new LensFrame(center, radius, fovRad);
    }

    public static IEnumerable<PerspectiveGridGeometry.GridCurve> EnumerateWarpedGridCurves(PaintingAssistant assistant)
        => EnumerateWarpedCurves(assistant);

    public static IEnumerable<IReadOnlyList<Point>> EnumeratePlaneCurves(PaintingAssistant assistant, int plane)
    {
        foreach (var curve in EnumerateWarpedCurves(assistant))
        {
            if (curve.Plane == plane)
                yield return curve.Points;
        }
    }

    public static IEnumerable<IReadOnlyList<Point>> EnumerateAllCurves(PaintingAssistant assistant)
    {
        foreach (var curve in EnumerateWarpedCurves(assistant))
            yield return curve.Points;
    }

    public static IEnumerable<(Point A, Point B)> SnapSegments(PaintingAssistant assistant)
    {
        foreach (var curve in EnumerateAllCurves(assistant))
        {
            for (var i = 1; i < curve.Count; i++)
                yield return (curve[i - 1], curve[i]);
        }

        foreach (var seg in LensCircleSegments(assistant))
            yield return seg;
    }

    public static IEnumerable<(Point A, Point B)> BoundarySegments(PaintingAssistant assistant)
        => LensCircleSegments(assistant);

    private static IEnumerable<PerspectiveGridGeometry.GridCurve> EnumerateWarpedCurves(PaintingAssistant assistant)
    {
        var frame = GetLensFrame(assistant);
        foreach (var curve in PerspectiveGridGeometry.EnumerateCurves(assistant))
        {
            var warped = WarpPolyline(curve.Points, frame);
            if (warped.Count >= 2)
                yield return curve with { Points = warped };
        }
    }

    private static IEnumerable<(Point A, Point B)> LensCircleSegments(PaintingAssistant assistant)
    {
        var frame = GetLensFrame(assistant);
        const int segments = 96;
        Point? prev = null;
        for (var i = 0; i <= segments; i++)
        {
            var angle = i * Math.PI * 2 / segments;
            var p = new Point(
                frame.Center.X + Math.Cos(angle) * frame.Radius,
                frame.Center.Y + Math.Sin(angle) * frame.Radius);
            if (prev is { } a)
                yield return (a, p);
            prev = p;
        }
    }

    public static List<Point> WarpPolyline(IReadOnlyList<Point> points, LensFrame frame)
    {
        if (points.Count == 0)
            return [];

        if (points.Count == 1)
            return [WarpPoint(points[0], frame)];

        var result = new List<Point>(points.Count * SamplesPerSegment);
        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            for (var s = 0; s <= SamplesPerSegment; s++)
            {
                var t = s / (double)SamplesPerSegment;
                result.Add(WarpPoint(Lerp(a, b, t), frame));
            }
        }

        return result;
    }

    public static Point WarpPoint(Point p, LensFrame frame)
    {
        var dx = p.X - frame.Center.X;
        var dy = p.Y - frame.Center.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1e-9)
            return p;

        var halfFov = Math.Min(frame.FovRadians * 0.5, Math.PI * 0.49);
        if (halfFov < 1e-6)
            return p;

        // Equidistant fisheye: map flat tangent-plane distance to incidence angle, then r ∝ θ.
        // Straight perspective lines become curves (globe projection).
        var focal = frame.Radius / Math.Tan(halfFov);
        var incidence = Math.Atan(dist / focal);
        var warpedDist = frame.Radius * incidence / halfFov;
        warpedDist = Math.Min(warpedDist, frame.Radius * 1.02);
        return new Point(frame.Center.X + dx / dist * warpedDist, frame.Center.Y + dy / dist * warpedDist);
    }

    private static Point Lerp(Point a, Point b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
