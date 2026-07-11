using System;
using System.Collections.Generic;
using Avalonia;

namespace Floss.App.Document.Assistants;

/// <summary>
/// Fisheye: warp the flat perspective grid inside a lens sized to the visible view.
/// </summary>
internal static class FisheyeAssistantGeometry
{
    private const int SamplesPerSegment = 64;

    internal readonly record struct LensFrame(Point Center, double Radius, double FovRadians);

    public static LensFrame GetLensFrame(PaintingAssistant assistant)
        => GetLensFrame(assistant, visibleDocumentRect: null);

    public static LensFrame GetLensFrame(PaintingAssistant assistant, Rect? visibleDocumentRect)
    {
        _ = visibleDocumentRect;
        // Lens is fully document-stable (handle-anchored). Zoom must not re-scale the warp.
        var stable = PerspectiveGridGeometry.ResolveClipBounds(assistant, visibleDocumentRect: null);
        var center = stable.Center;
        var radius = Math.Max(Math.Sqrt(stable.Width * stable.Width + stable.Height * stable.Height) * 0.5, 1);
        var fovRad = Math.Clamp(assistant.FovDegrees, 10, 360) * Math.PI / 180;
        return new LensFrame(center, radius, fovRad);
    }

    public static IEnumerable<PerspectiveGridGeometry.GridCurve> EnumerateWarpedGridCurves(PaintingAssistant assistant)
        => EnumerateWarpedCurves(assistant, visibleDocumentRect: null, zoom: 1.0);

    public static IEnumerable<PerspectiveGridGeometry.GridCurve> EnumerateWarpedGridCurves(
        PaintingAssistant assistant,
        Rect? visibleDocumentRect,
        double zoom)
        => EnumerateWarpedCurves(assistant, visibleDocumentRect, zoom);

    public static IEnumerable<IReadOnlyList<Point>> EnumeratePlaneCurves(PaintingAssistant assistant, int plane)
    {
        foreach (var curve in EnumerateWarpedCurves(assistant, null, 1.0))
        {
            if (curve.Plane == plane)
                yield return curve.Points;
        }
    }

    public static IEnumerable<IReadOnlyList<Point>> EnumerateAllCurves(PaintingAssistant assistant)
    {
        foreach (var curve in EnumerateWarpedCurves(assistant, null, 1.0))
            yield return curve.Points;
    }

    public static IEnumerable<(Point A, Point B)> SnapSegments(PaintingAssistant assistant)
    {
        foreach (var curve in EnumerateAllCurves(assistant))
        {
            for (var i = 1; i < curve.Count; i++)
                yield return (curve[i - 1], curve[i]);
        }

        foreach (var seg in LensCircleSegments(assistant, null))
            yield return seg;
    }

    public static IEnumerable<(Point A, Point B)> BoundarySegments(PaintingAssistant assistant)
        => LensCircleSegments(assistant, null);

    public static IEnumerable<(Point A, Point B)> BoundarySegments(PaintingAssistant assistant, Rect? visibleDocumentRect)
        => LensCircleSegments(assistant, visibleDocumentRect);

    private static IEnumerable<PerspectiveGridGeometry.GridCurve> EnumerateWarpedCurves(
        PaintingAssistant assistant,
        Rect? visibleDocumentRect,
        double zoom)
    {
        var frame = GetLensFrame(assistant, visibleDocumentRect);
        foreach (var curve in PerspectiveGridGeometry.EnumerateCurves(assistant, visibleDocumentRect, zoom))
        {
            var warped = WarpPolyline(curve.Points, frame);
            if (warped.Count >= 2)
                yield return curve with { Points = warped };
        }
    }

    private static IEnumerable<(Point A, Point B)> LensCircleSegments(PaintingAssistant assistant, Rect? visibleDocumentRect)
    {
        var frame = GetLensFrame(assistant, visibleDocumentRect);
        const int segments = 128;
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
        var focal = frame.Radius / Math.Tan(halfFov);
        var incidence = Math.Atan(dist / focal);
        var warpedDist = frame.Radius * incidence / halfFov;
        warpedDist = Math.Min(warpedDist, frame.Radius * 1.02);
        return new Point(frame.Center.X + dx / dist * warpedDist, frame.Center.Y + dy / dist * warpedDist);
    }

    private static Point Lerp(Point a, Point b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
