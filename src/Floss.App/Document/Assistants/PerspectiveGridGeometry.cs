using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Floss.App.Document.Assistants;

/// <summary>
/// Perspective guide polylines with projective (equal-world) spacing so the grid
/// foreshortens toward vanishing points. Lattice is document-stable (CSP-like):
/// zoom/pan only clips which lines are visible — it does not re-anchor spacing.
/// </summary>
internal static class PerspectiveGridGeometry
{
    internal readonly record struct GridCurve(IReadOnlyList<Point> Points, int Plane, bool IsPrimary = false);

    private const int MaxLinesPerFamily = 64;

    public static Rect GetHandleBounds(PaintingAssistant assistant)
    {
        var points = CollectHandles(assistant);
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var width = Math.Max(maxX - minX, 1);
        var height = Math.Max(maxY - minY, 1);
        return new Rect(minX, minY, width, height);
    }

    public static Rect GetViewBounds(PaintingAssistant assistant)
        => ResolveClipBounds(assistant, visibleDocumentRect: null);

    public static Rect ResolveClipBounds(PaintingAssistant assistant, Rect? visibleDocumentRect)
    {
        if (visibleDocumentRect is { } v && v.Width > 1 && v.Height > 1)
            return v;

        var handles = GetHandleBounds(assistant);
        var pad = Math.Max(Math.Max(handles.Width, handles.Height) * 4, 2000);
        return new Rect(handles.X - pad, handles.Y - pad, handles.Width + pad * 2, handles.Height + pad * 2);
    }

    /// <summary>Document-space grid step from handles + subdivisions. Independent of zoom.</summary>
    public static double ParallelLineSpacing(PaintingAssistant assistant)
        => StableDocSpacing(assistant);

    public static double ParallelLineSpacing(PaintingAssistant assistant, Rect bounds, double zoom)
    {
        _ = bounds;
        _ = zoom;
        return StableDocSpacing(assistant);
    }

    public static double StableDocSpacing(PaintingAssistant assistant)
    {
        var subdivisions = Math.Clamp(assistant.GridSubdivisions, 2, 12);
        var handles = GetHandleBounds(assistant);
        // Reference size from the ruler itself so zoom never changes the lattice.
        var refSize = Math.Max(Math.Max(handles.Width, handles.Height), 240);
        return Math.Max(refSize / subdivisions, 12);
    }

    /// <summary>Stable near-plane distance from a vanishing point along the ground.</summary>
    public static double StableFrontDistance(PaintingAssistant assistant, Point vp, Point horizonRef, double spacing)
    {
        var handleDist = Math.Max(Dist(vp, horizonRef), 1);
        var subdivisions = Math.Clamp(assistant.GridSubdivisions, 2, 12);
        return Math.Max(handleDist * 2.5, Math.Max(spacing * subdivisions * 2.5, 280));
    }

    public static IEnumerable<GridCurve> EnumerateCurves(PaintingAssistant assistant)
        => EnumerateCurves(assistant, visibleDocumentRect: null, zoom: 1.0);

    public static IEnumerable<GridCurve> EnumerateCurves(
        PaintingAssistant assistant,
        Rect? visibleDocumentRect,
        double zoom)
    {
        _ = zoom; // lattice is document-stable; zoom only affects which clip rect we draw into
        if (assistant.TypeId is not (PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType))
            yield break;

        var clip = ResolveClipBounds(assistant, visibleDocumentRect);
        var subdivisions = Math.Clamp(assistant.GridSubdivisions, 2, 12);
        var spacing = StableDocSpacing(assistant);

        switch (assistant.PerspectiveMode)
        {
            case PerspectiveAssistantMode.OnePoint:
                foreach (var curve in OnePointGroundGrid(assistant, assistant.HandleA, assistant.HandleB, clip, subdivisions, spacing))
                    yield return curve;
                break;

            case PerspectiveAssistantMode.TwoPoint:
                foreach (var curve in TwoPointGroundGrid(
                             assistant, assistant.HandleA, assistant.HandleB, assistant.HandleC, clip, subdivisions, spacing))
                    yield return curve;
                break;

            case PerspectiveAssistantMode.ThreePoint:
                foreach (var curve in ThreePointGrid(
                             assistant, assistant.HandleA, assistant.HandleB, assistant.HandleC, clip, subdivisions, spacing))
                    yield return curve;
                break;

            default:
                foreach (var curve in FreeQuadCurves(assistant, subdivisions))
                    yield return curve;
                break;
        }
    }

    public static IEnumerable<(Point A, Point B)> EnumerateSegments(PaintingAssistant assistant)
        => EnumerateSegments(assistant, visibleDocumentRect: null, zoom: 1.0);

    public static IEnumerable<(Point A, Point B)> EnumerateSegments(
        PaintingAssistant assistant,
        Rect? visibleDocumentRect,
        double zoom)
    {
        foreach (var curve in EnumerateCurves(assistant, visibleDocumentRect, zoom))
        {
            for (var i = 1; i < curve.Points.Count; i++)
                yield return (curve.Points[i - 1], curve.Points[i]);
        }
    }

    private static IEnumerable<GridCurve> OnePointGroundGrid(
        PaintingAssistant assistant,
        Point vp,
        Point horizonRef,
        Rect clip,
        int subdivisions,
        double spacing)
    {
        var (horizonDir, groundDir) = HorizonAndGround(vp, horizonRef);
        yield return new GridCurve(
            LineRectClip(vp, horizonDir, clip) ?? [vp, horizonRef],
            0, IsPrimary: true);

        var frontDist = StableFrontDistance(assistant, vp, horizonRef, spacing);
        var frontCenter = new Point(vp.X + groundDir.X * frontDist, vp.Y + groundDir.Y * frontDist);

        // Cover the visible clip width at the front plane without changing lattice phase.
        var halfCount = HalfCountForCoverage(vp, frontCenter, horizonDir, spacing, clip, subdivisions);

        for (var i = -halfCount; i <= halfCount; i++)
        {
            var edge = new Point(frontCenter.X + horizonDir.X * (i * spacing),
                frontCenter.Y + horizonDir.Y * (i * spacing));
            if (RaySegment(vp, edge, clip) is { } ray)
                yield return new GridCurve(ray, 1);
        }

        // Transversals on a fixed harmonic ladder from the stable front toward the VP.
        var depthCount = Math.Clamp(subdivisions * 3, 6, MaxLinesPerFamily);
        var nearDist = frontDist;
        var farDist = Math.Max(nearDist * 0.04, spacing * 0.5);
        for (var i = 0; i <= depthCount; i++)
        {
            var t = i / (double)depthCount;
            var d = 1.0 / Lerp(1.0 / nearDist, 1.0 / farDist, t);
            var seed = new Point(vp.X + groundDir.X * d, vp.Y + groundDir.Y * d);
            if (LineRectClip(seed, horizonDir, clip) is { } seg)
                yield return new GridCurve(seg, 0, IsPrimary: i == 0);
        }
    }

    private static IEnumerable<GridCurve> TwoPointGroundGrid(
        PaintingAssistant assistant,
        Point vpL,
        Point vpR,
        Point anchor,
        Rect clip,
        int subdivisions,
        double spacing)
    {
        var horizon = vpR - vpL;
        var horizonLen = Len(horizon);
        if (horizonLen < 1e-6)
            yield break;

        var horizonDir = new Point(horizon.X / horizonLen, horizon.Y / horizonLen);
        yield return new GridCurve(
            LineRectClip(vpL, horizonDir, clip) ?? [vpL, vpR],
            0, IsPrimary: true);

        var mid = new Point((vpL.X + vpR.X) * 0.5, (vpL.Y + vpR.Y) * 0.5);
        var toAnchor = anchor - mid;
        var groundDir = Len(toAnchor) > 1e-6 ? Norm(toAnchor) : PreferDocumentDown(horizonDir);

        var count = HalfCountForCoverage(mid, anchor, horizonDir, spacing, clip, subdivisions);
        for (var i = -count; i <= count; i++)
        {
            if (i == 0)
            {
                if (RaySegment(vpL, anchor, clip) is { } a0)
                    yield return new GridCurve(a0, 1, IsPrimary: true);
                if (RaySegment(vpR, anchor, clip) is { } a1)
                    yield return new GridCurve(a1, 2, IsPrimary: true);
                continue;
            }

            var front = new Point(anchor.X + horizonDir.X * (i * spacing), anchor.Y + horizonDir.Y * (i * spacing));
            if (RaySegment(vpL, front, clip) is { } leftRay)
                yield return new GridCurve(leftRay, 1);
            if (RaySegment(vpR, front, clip) is { } rightRay)
                yield return new GridCurve(rightRay, 2);
        }

        var towardGround = Dot(anchor - mid, groundDir) >= 0 ? groundDir : new Point(-groundDir.X, -groundDir.Y);
        var nearDist = Math.Max(Dist(mid, anchor), StableFrontDistance(assistant, mid, anchor, spacing) * 0.5);
        var farDist = Math.Max(nearDist * 0.05, spacing * 0.5);
        var depthCount = Math.Clamp(subdivisions * 3, 6, MaxLinesPerFamily);
        for (var i = 0; i <= depthCount; i++)
        {
            var t = i / (double)depthCount;
            var d = 1.0 / Lerp(1.0 / nearDist, 1.0 / farDist, t);
            var seed = new Point(mid.X + towardGround.X * d, mid.Y + towardGround.Y * d);
            if (LineRectClip(seed, horizonDir, clip) is { } seg)
                yield return new GridCurve(seg, 0, IsPrimary: i == 0);
        }
    }

    private static IEnumerable<GridCurve> ThreePointGrid(
        PaintingAssistant assistant,
        Point vpL,
        Point vpR,
        Point vpV,
        Rect clip,
        int subdivisions,
        double spacing)
    {
        foreach (var curve in TwoPointGroundGrid(assistant, vpL, vpR, vpV, clip, subdivisions, spacing))
            yield return curve;

        var mid = new Point((vpL.X + vpR.X) * 0.5, (vpL.Y + vpR.Y) * 0.5);
        var horizon = Norm(vpR - vpL);
        var count = HalfCountForCoverage(vpV, mid, horizon, spacing, clip, subdivisions);
        for (var i = -count; i <= count; i++)
        {
            var p = new Point(mid.X + horizon.X * (i * spacing), mid.Y + horizon.Y * (i * spacing));
            if (RaySegment(vpV, p, clip) is { } ray)
                yield return new GridCurve(ray, 0, IsPrimary: i == 0);
        }
    }

    /// <summary>
    /// How many lattice steps (±) are needed to cover the clip rect, without
    /// changing the i*spacing phase — zoom only adds/removes outer lines.
    /// </summary>
    private static int HalfCountForCoverage(
        Point origin,
        Point frontRef,
        Point measureDir,
        double spacing,
        Rect clip,
        int subdivisions)
    {
        var minHalf = Math.Clamp(subdivisions * 2, 4, MaxLinesPerFamily / 2);
        if (spacing < 1e-6)
            return minHalf;

        // Distance from frontRef to the farthest clip corner along ±measureDir.
        var corners = new[]
        {
            new Point(clip.Left, clip.Top),
            new Point(clip.Right, clip.Top),
            new Point(clip.Right, clip.Bottom),
            new Point(clip.Left, clip.Bottom),
        };
        var maxAlong = 0.0;
        foreach (var c in corners)
        {
            var along = Math.Abs(Dot(c - frontRef, measureDir));
            if (along > maxAlong) maxAlong = along;
        }

        // Also consider span from origin for radial fans.
        foreach (var c in corners)
        {
            var along = Math.Abs(Dot(c - origin, measureDir));
            if (along > maxAlong) maxAlong = along;
        }

        var needed = (int)Math.Ceiling(maxAlong / spacing) + 1;
        return Math.Clamp(Math.Max(minHalf, needed), minHalf, MaxLinesPerFamily / 2);
    }

    private static (Point HorizonDir, Point GroundDir) HorizonAndGround(Point vp, Point horizonRef)
    {
        var h = horizonRef - vp;
        var hLen = Len(h);
        var horizonDir = hLen > 1e-6 ? new Point(h.X / hLen, h.Y / hLen) : new Point(1, 0);
        // Document +Y is down — keep ground side stable across zoom/pan.
        var groundDir = PreferDocumentDown(horizonDir);
        return (horizonDir, groundDir);
    }

    private static Point PreferDocumentDown(Point horizonDir)
    {
        var a = new Point(-horizonDir.Y, horizonDir.X);
        var b = new Point(horizonDir.Y, -horizonDir.X);
        // Pick the perpendicular with positive document Y (downward on canvas).
        return a.Y >= b.Y ? a : b;
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

    private static IEnumerable<GridCurve> FreeQuadCurves(PaintingAssistant assistant, int subdivisions)
    {
        var a = assistant.HandleA;
        var b = assistant.HandleB;
        var c = assistant.HandleC;
        var d = assistant.HandleD;
        yield return new GridCurve([a, b], 0, IsPrimary: true);
        yield return new GridCurve([b, c], 0, IsPrimary: true);
        yield return new GridCurve([c, d], 0, IsPrimary: true);
        yield return new GridCurve([d, a], 0, IsPrimary: true);

        for (var t = 1; t < subdivisions; t++)
        {
            var u = t / (double)subdivisions;
            yield return new GridCurve([LerpPt(a, d, u), LerpPt(b, c, u)], 1);
            yield return new GridCurve([LerpPt(a, b, u), LerpPt(d, c, u)], 2);
        }
    }

    private static IReadOnlyList<Point>? RaySegment(Point from, Point toward, Rect bounds)
    {
        var dx = toward.X - from.X;
        var dy = toward.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
            return null;

        var angle = Math.Atan2(dy, dx);
        if (RayToBounds(from, angle, bounds) is not { } hit)
            return null;

        return [from, hit];
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

    private static Point LerpPt(Point a, Point b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Len(Point p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
    private static double Dist(Point a, Point b) => Len(new Point(b.X - a.X, b.Y - a.Y));
    private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y;
    private static Point Norm(Point p)
    {
        var l = Len(p);
        return l < 1e-9 ? new Point(1, 0) : new Point(p.X / l, p.Y / l);
    }
}
