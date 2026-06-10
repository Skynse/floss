using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.SmartShape;

/// <summary>
/// Port of -smart-shape/smartshape/stroke_analyzer.py — shape detection and fitting.
/// </summary>
public static class SmartShapeAnalyzer
{
    public static IReadOnlyList<Vec2> RdpSimplify(IReadOnlyList<Vec2> pts, double epsilon = 4.0)
    {
        if (pts.Count < 3)
            return pts;
        return Rdp(pts, epsilon);
    }

    public static SmartShapeModel? AnalyzeStroke(
        IReadOnlyList<Vec2> points,
        double rdpEpsilon = 4.0,
        double closureThreshold = 0.12,
        double compactnessMin = 0.75,
        double ellipseAspectMin = 1.3)
    {
        if (points.Count < 4)
            return null;

        var simplified = RdpSimplify(points, rdpEpsilon);
        var closed = IsClosed(points, closureThreshold);
        return closed
            ? ClassifyClosed(points, simplified, compactnessMin, ellipseAspectMin)
            : ClassifyOpen(points, simplified);
    }

    public static Vec2 ShapeCenter(SmartShapeModel shape) => shape switch
    {
        LineShape l => new((l.Start.X + l.End.X) * 0.5, (l.Start.Y + l.End.Y) * 0.5),
        CircleShape c => c.Center,
        EllipseShape e => e.Center,
        RectangleShape r => r.Center,
        TriangleShape t => Centroid(t.Points),
        PolygonShape p => Centroid(p.Points),
        CurveShape c => Centroid(CurveAnchorPoints(c)),
        PolylineShape pl => Centroid(pl.Points),
        _ => new Vec2(0, 0)
    };

    private static List<Vec2> Rdp(IReadOnlyList<Vec2> pts, double eps)
    {
        var start = pts[0];
        var end = pts[^1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var denom = Math.Sqrt(dx * dx + dy * dy);

        List<double> dists;
        if (denom < 1e-10)
        {
            dists = new List<double>(pts.Count - 2);
            for (var i = 1; i < pts.Count - 1; i++)
                dists.Add(Dist(pts[i], start));
        }
        else
        {
            var nx = -dy / denom;
            var ny = dx / denom;
            dists = new List<double>(pts.Count - 2);
            for (var i = 1; i < pts.Count - 1; i++)
                dists.Add(Math.Abs((pts[i].X - start.X) * nx + (pts[i].Y - start.Y) * ny));
        }

        if (dists.Count == 0)
            return [start, end];

        var maxD = 0.0;
        var maxI = 0;
        for (var i = 0; i < dists.Count; i++)
        {
            if (dists[i] > maxD)
            {
                maxD = dists[i];
                maxI = i;
            }
        }

        var split = maxI + 1;
        if (maxD > eps)
        {
            var left = new List<Vec2>();
            for (var i = 0; i <= split; i++)
                left.Add(pts[i]);
            var right = new List<Vec2>();
            for (var i = split; i < pts.Count; i++)
                right.Add(pts[i]);
            var l = Rdp(left, eps);
            var r = Rdp(right, eps);
            var merged = new List<Vec2>(l);
            for (var i = 1; i < r.Count; i++)
                merged.Add(r[i]);
            return merged;
        }

        return [start, end];
    }

    private static bool IsStraight(IReadOnlyList<Vec2> pts, double threshold = 0.20)
    {
        var start = pts[0];
        var end = pts[^1];
        var chord = Dist(start, end);
        if (chord < 1e-6)
            return true;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var nx = -dy / chord;
        var ny = dx / chord;
        var maxDev = 0.0;
        foreach (var p in pts)
        {
            var d = Math.Abs((p.X - start.X) * nx + (p.Y - start.Y) * ny);
            if (d > maxDev)
                maxDev = d;
        }
        return maxDev / chord < threshold;
    }

    private static bool IsClosed(IReadOnlyList<Vec2> pts, double threshold)
    {
        var total = 0.0;
        for (var i = 0; i < pts.Count - 1; i++)
            total += Dist(pts[i], pts[i + 1]);
        if (total < 1)
            return false;
        return Dist(pts[^1], pts[0]) / total < threshold;
    }

    private static SmartShapeModel? ClassifyClosed(
        IReadOnlyList<Vec2> pts,
        IReadOnlyList<Vec2> simplified,
        double compactnessMin,
        double ellipseAspectMin)
    {
        var comp = Compactness(pts);

        if (comp >= compactnessMin)
        {
            var (cx, cy, rx, ry, angle) = FitEllipseAlgebraic(pts);
            var aspect = Math.Max(rx, ry) / (Math.Min(rx, ry) + 1e-6);
            if (aspect < ellipseAspectMin)
                return new CircleShape(new Vec2(cx, cy), (rx + ry) * 0.5);
            return new EllipseShape(new Vec2(cx, cy), rx, ry, angle);
        }

        var obb = FitObb(pts);
        var size = Math.Max(obb.Width, obb.Height);
        if (size >= 1.0 && MaxObbEdgeDeviation(pts, obb) / size <= 0.12)
            return new RectangleShape(obb.Center, obb.Width, obb.Height, obb.Angle);

        var nc = simplified.Count;

        if (nc <= 3)
        {
            var triangleCorners = FindCorners(pts, simplified, 3);
            return new TriangleShape(triangleCorners);
        }

        if (nc == 4)
            return new RectangleShape(obb.Center, obb.Width, obb.Height, obb.Angle);

        var polygonCorners = FindCorners(pts, simplified, Math.Min(nc, 8));
        return new PolygonShape(polygonCorners);
    }

    private static SmartShapeModel ClassifyOpen(IReadOnlyList<Vec2> pts, IReadOnlyList<Vec2> simplified)
    {
        if (IsStraight(pts, 0.06))
            return new LineShape(pts[0], pts[^1]);
        if (simplified.Count <= 2)
            return new LineShape(pts[0], pts[^1]);

        // Fit on simplified polyline — not every raw tablet sample (avoids segment spam).
        var fitPts = simplified.Count >= 4 ? simplified : RdpSimplify(pts, 6.0);
        if (fitPts.Count < 2)
            return new LineShape(pts[0], pts[^1]);

        var curves = FitBezierCurves(fitPts, 12.0);
        curves = ConsolidateBezierCurves(curves, maxSegments: 4);
        if (curves.Count == 0)
            return new LineShape(pts[0], pts[^1]);
        if (curves.Count == 1 && IsNearlyStraightBezier(curves[0]))
            return new LineShape(curves[0].P0, curves[0].P3);
        return new CurveShape(curves);
    }

    private static bool IsNearlyStraightBezier((Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3) seg)
    {
        var lineLen = Dist(seg.P0, seg.P3);
        if (lineLen < 1e-6)
            return true;
        var d1 = Dist(seg.P1, Lerp(seg.P0, seg.P3, 0.33));
        var d2 = Dist(seg.P2, Lerp(seg.P0, seg.P3, 0.66));
        return (d1 + d2) / lineLen < 0.08;
    }

    private static Vec2 Lerp(Vec2 a, Vec2 b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> ConsolidateBezierCurves(
        List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> curves,
        int maxSegments)
    {
        if (curves.Count <= maxSegments)
            return curves;

        // Re-fit the whole stroke as one cubic when subdivision over-ran.
        var samples = new List<Vec2>(curves.Count * 3 + 1) { curves[0].P0 };
        foreach (var c in curves)
        {
            samples.Add(c.P1);
            samples.Add(c.P2);
            samples.Add(c.P3);
        }
        var single = FitBezierCurves(samples, 16.0);
        if (single.Count > 0 && single.Count <= maxSegments)
            return single;

        // Keep endpoints; merge interior segments pairwise until under cap.
        while (curves.Count > maxSegments)
        {
            var best = 0;
            var bestCost = double.MaxValue;
            for (var i = 0; i < curves.Count - 1; i++)
            {
                var cost = Dist(curves[i].P3, curves[i + 1].P0);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = i;
                }
            }

            var a = curves[best];
            var b = curves[best + 1];
            curves[best] = (a.P0, a.P1, b.P2, b.P3);
            curves.RemoveAt(best + 1);
        }
        return curves;
    }

    private static double MaxObbEdgeDeviation(
        IReadOnlyList<Vec2> pts,
        (Vec2 Center, double Width, double Height, double Angle) obb)
    {
        var angleRad = obb.Angle * Math.PI / 180.0;
        var cos = Math.Cos(-angleRad);
        var sin = Math.Sin(-angleRad);
        var hw = obb.Width * 0.5;
        var hh = obb.Height * 0.5;
        var maxDev = 0.0;
        foreach (var p in pts)
        {
            var dx = p.X - obb.Center.X;
            var dy = p.Y - obb.Center.Y;
            var lx = dx * cos - dy * sin;
            var ly = dx * sin + dy * cos;
            var dev = DistanceToRectBoundary(lx, ly, hw, hh);
            if (dev > maxDev)
                maxDev = dev;
        }
        return maxDev;
    }

    private static double DistanceToRectBoundary(double lx, double ly, double hw, double hh)
    {
        var dx = Math.Max(0, Math.Abs(lx) - hw);
        var dy = Math.Max(0, Math.Abs(ly) - hh);
        if (dx <= 0)
            return dy;
        if (dy <= 0)
            return dx;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Compactness(IReadOnlyList<Vec2> pts)
    {
        var perim = 0.0;
        for (var i = 0; i < pts.Count; i++)
            perim += Dist(pts[i], pts[(i + 1) % pts.Count]);
        var xs = new double[pts.Count];
        var ys = new double[pts.Count];
        for (var i = 0; i < pts.Count; i++)
        {
            xs[i] = pts[i].X;
            ys[i] = pts[i].Y;
        }
        var n = xs.Length;
        var area = 0.0;
        for (var i = 0; i < n; i++)
            area += xs[i] * ys[(i + 1) % n] - xs[(i + 1) % n] * ys[i];
        area = Math.Abs(area) * 0.5;
        return 4 * Math.PI * area / (perim * perim + 1e-10);
    }

    internal static List<Vec2> FindCornersForFit(IReadOnlyList<Vec2> pts, IReadOnlyList<Vec2> simplified, int n)
        => FindCorners(pts, simplified, n);

    internal static (Vec2 Center, double Width, double Height, double Angle) FitObbForFit(IReadOnlyList<Vec2> pts)
        => FitObb(pts);

    internal static (double Cx, double Cy, double Rx, double Ry, double Angle) FitEllipseAlgebraicForFit(IReadOnlyList<Vec2> pts)
        => FitEllipseAlgebraic(pts);

    internal static List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> FitBezierCurvesForFit(IReadOnlyList<Vec2> pts, double error)
        => FitBezierCurves(pts, error);

    internal static List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> ConsolidateBezierCurvesForFit(
        List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> curves, int maxSegments)
        => ConsolidateBezierCurves(curves, maxSegments);

    internal static bool IsNearlyStraightBezierForFit((Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3) seg)
        => IsNearlyStraightBezier(seg);

    private static List<Vec2> FindCorners(IReadOnlyList<Vec2> pts, IReadOnlyList<Vec2> simplified, int? target = null)
    {
        var n = target ?? simplified.Count;
        if (n <= 0)
            return [.. simplified];

        var arcs = new List<double>(pts.Count) { 0.0 };
        for (var i = 0; i < pts.Count - 1; i++)
            arcs.Add(arcs[^1] + Dist(pts[i], pts[i + 1]));
        var total = arcs[^1];
        if (total < 1e-6)
            return Enumerable.Repeat(pts[0], n).ToList();

        var result = new List<Vec2>(n);
        for (var k = 0; k < n; k++)
        {
            var targetArc = n > 1 ? total * k / (n - 1) : 0.0;
            var lo = 0;
            var hi = arcs.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (arcs[mid] < targetArc)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            result.Add(pts[lo]);
        }
        return result;
    }

    private static (double Cx, double Cy, double Rx, double Ry, double Angle) FitEllipseAlgebraic(IReadOnlyList<Vec2> pts)
    {
        var (mx, my, cxx, cxy, cyy) = Covariance2d(pts);
        var (l1, _, v1, v2) = Eigh2x2(cxx, cxy, cyy);
        _ = l1;

        var c = new Vec2(mx, my);
        var proj1 = new List<double>(pts.Count);
        var proj2 = new List<double>(pts.Count);
        foreach (var p in pts)
        {
            proj1.Add(Dot(Sub(p, c), v1));
            proj2.Add(Dot(Sub(p, c), v2));
        }
        var rx = (proj1.Max() - proj1.Min()) * 0.5;
        var ry = (proj2.Max() - proj2.Min()) * 0.5;
        var angle = Math.Atan2(v1.Y, v1.X) * (180.0 / Math.PI);
        return (mx, my, Math.Max(rx, ry), Math.Min(rx, ry), angle);
    }

    private static (Vec2 Center, double Width, double Height, double Angle) FitObb(IReadOnlyList<Vec2> pts)
    {
        var (mx, my, cxx, cxy, cyy) = Covariance2d(pts);
        var (_, _, v1, v2) = Eigh2x2(cxx, cxy, cyy);

        var proj1 = new List<double>(pts.Count);
        var proj2 = new List<double>(pts.Count);
        var origin = new Vec2(mx, my);
        foreach (var p in pts)
        {
            proj1.Add(Dot(Sub(p, origin), v1));
            proj2.Add(Dot(Sub(p, origin), v2));
        }

        var c1 = (proj1.Max() + proj1.Min()) * 0.5;
        var c2 = (proj2.Max() + proj2.Min()) * 0.5;
        var cx = mx + c1 * v1.X + c2 * v2.X;
        var cy = my + c1 * v1.Y + c2 * v2.Y;
        var w = proj1.Max() - proj1.Min();
        var h = proj2.Max() - proj2.Min();
        var angle = Math.Atan2(v1.Y, v1.X) * (180.0 / Math.PI);
        return (new Vec2(cx, cy), w, h, angle);
    }

    private static List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> FitBezierCurves(IReadOnlyList<Vec2> pts, double error)
    {
        var n = pts.Count;
        if (n < 2)
            return [];
        if (n == 2)
        {
            var d = Scale(Sub(pts[1], pts[0]), 1.0 / 3.0);
            return [(pts[0], Add(pts[0], d), Sub(pts[1], d), pts[1])];
        }

        var t = ChordParams(pts);
        var tan1 = Normalize(Sub(pts[1], pts[0]));
        var tan2 = Normalize(Sub(pts[^2], pts[^1]));
        var outSegs = new List<(Vec2, Vec2, Vec2, Vec2)>();
        FitSegment(pts, t, tan1, tan2, error, outSegs, 0);
        return outSegs;
    }

    private static void FitSegment(
        IReadOnlyList<Vec2> pts,
        IReadOnlyList<double> t,
        Vec2 tan1,
        Vec2 tan2,
        double error,
        List<(Vec2, Vec2, Vec2, Vec2)> output,
        int depth)
    {
        var seg = GenBezier(pts, t, tan1, tan2);
        var (merr, msplit) = MaxError(pts, t, seg);
        if (merr < error || depth >= 5)
        {
            output.Add(seg);
            return;
        }
        if (msplit <= 0 || msplit >= pts.Count - 1)
        {
            output.Add(seg);
            return;
        }

        var mid = msplit;
        var leftPts = Slice(pts, 0, mid + 1);
        var rightPts = Slice(pts, mid, pts.Count - mid);
        var tanMid = Normalize(Sub(leftPts[^2], rightPts[1]));
        FitSegment(leftPts, ChordParams(leftPts), tan1, tanMid, error, output, depth + 1);
        FitSegment(rightPts, ChordParams(rightPts), Neg(tanMid), tan2, error, output, depth + 1);
    }

    private static (Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3) GenBezier(
        IReadOnlyList<Vec2> pts,
        IReadOnlyList<double> t,
        Vec2 tan1,
        Vec2 tan2)
    {
        var p0 = pts[0];
        var p3 = pts[^1];
        var n = pts.Count;
        double a11 = 0, a12 = 0, a22 = 0, b1x = 0, b1y = 0, b2x = 0, b2y = 0;
        for (var i = 0; i < n; i++)
        {
            var b = Bern(t[i]);
            var c1 = Scale(tan1, b[1]);
            var c2 = Scale(tan2, b[2]);
            var rx = pts[i].X - (b[0] + b[1]) * p0.X - (b[2] + b[3]) * p3.X;
            var ry = pts[i].Y - (b[0] + b[1]) * p0.Y - (b[2] + b[3]) * p3.Y;
            a11 += Dot(c1, c1);
            a12 += Dot(c1, c2);
            a22 += Dot(c2, c2);
            b1x += rx * c1.X;
            b1y += ry * c1.Y;
            b2x += rx * c2.X;
            b2y += ry * c2.Y;
        }

        var det = a11 * a22 - a12 * a12;
        double alpha1, alpha2;
        if (Math.Abs(det) < 1e-10)
        {
            var d3 = Dist(p0, p3) / 3.0;
            alpha1 = alpha2 = d3;
        }
        else
        {
            var s1 = b1x + b1y;
            var s2 = b2x + b2y;
            alpha1 = (a22 * s1 - a12 * s2) / det;
            alpha2 = (a11 * s2 - a12 * s1) / det;
        }

        alpha1 = Math.Max(alpha1, 1e-3);
        alpha2 = Math.Max(alpha2, 1e-3);
        return (p0, Add(p0, Scale(tan1, alpha1)), Add(p3, Scale(tan2, alpha2)), p3);
    }

    private static (double MaxErr, int Split) MaxError(
        IReadOnlyList<Vec2> pts,
        IReadOnlyList<double> t,
        (Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3) seg)
    {
        var bestErr = 0.0;
        var bestSplit = pts.Count / 2;
        for (var i = 0; i < pts.Count; i++)
        {
            var b = Bern(t[i]);
            var px = b[0] * seg.P0.X + b[1] * seg.P1.X + b[2] * seg.P2.X + b[3] * seg.P3.X;
            var py = b[0] * seg.P0.Y + b[1] * seg.P1.Y + b[2] * seg.P2.Y + b[3] * seg.P3.Y;
            var e = Dist(pts[i], new Vec2(px, py));
            if (e > bestErr)
            {
                bestErr = e;
                bestSplit = i;
            }
        }
        return (bestErr, bestSplit);
    }

    private static List<double> ChordParams(IReadOnlyList<Vec2> pts)
    {
        var dists = new List<double>(pts.Count - 1);
        for (var i = 0; i < pts.Count - 1; i++)
            dists.Add(Dist(pts[i], pts[i + 1]));
        var t = new List<double>(pts.Count) { 0.0 };
        foreach (var d in dists)
            t.Add(t[^1] + d);
        var total = t[^1];
        if (total > 1e-10)
        {
            for (var i = 0; i < t.Count; i++)
                t[i] /= total;
            return t;
        }
        var n = pts.Count;
        var uniform = new List<double>(n);
        for (var i = 0; i < n; i++)
            uniform.Add(n > 1 ? (double)i / (n - 1) : 0.0);
        return uniform;
    }

    private static double[] Bern(double t)
    {
        var u = 1 - t;
        return [u * u * u, 3 * t * u * u, 3 * t * t * u, t * t * t];
    }

    private static (double Mx, double My, double Cxx, double Cxy, double Cyy) Covariance2d(IReadOnlyList<Vec2> pts)
    {
        var n = pts.Count;
        var mx = pts.Sum(p => p.X) / n;
        var my = pts.Sum(p => p.Y) / n;
        var cxx = pts.Sum(p => (p.X - mx) * (p.X - mx)) / n;
        var cyy = pts.Sum(p => (p.Y - my) * (p.Y - my)) / n;
        var cxy = pts.Sum(p => (p.X - mx) * (p.Y - my)) / n;
        return (mx, my, cxx, cxy, cyy);
    }

    private static (double L1, double L2, Vec2 V1, Vec2 V2) Eigh2x2(double cxx, double cxy, double cyy)
    {
        var tr = cxx + cyy;
        var disc = Math.Sqrt(Math.Max(0.0, (cxx - cyy) * (cxx - cyy) + 4 * cxy * cxy));
        var l1 = (tr + disc) * 0.5;
        var l2 = (tr - disc) * 0.5;
        Vec2 v1, v2;
        if (Math.Abs(cxy) > 1e-10)
        {
            v1 = Normalize(new Vec2(l1 - cyy, cxy));
            v2 = Normalize(new Vec2(l2 - cyy, cxy));
        }
        else if (cxx >= cyy)
        {
            v1 = new Vec2(1, 0);
            v2 = new Vec2(0, 1);
        }
        else
        {
            v1 = new Vec2(0, 1);
            v2 = new Vec2(1, 0);
        }
        return (l1, l2, v1, v2);
    }

    private static Vec2 Centroid(IReadOnlyList<Vec2> points)
    {
        if (points.Count == 0)
            return new Vec2(0, 0);
        var sx = points.Sum(p => p.X);
        var sy = points.Sum(p => p.Y);
        return new Vec2(sx / points.Count, sy / points.Count);
    }

    private static List<Vec2> CurveAnchorPoints(CurveShape curve)
    {
        var pts = new List<Vec2>();
        foreach (var seg in curve.Curves)
        {
            if (pts.Count == 0)
                pts.Add(seg.P0);
            pts.Add(seg.P3);
        }
        return pts;
    }

    private static List<Vec2> Slice(IReadOnlyList<Vec2> pts, int start, int count)
    {
        var list = new List<Vec2>(count);
        for (var i = start; i < start + count && i < pts.Count; i++)
            list.Add(pts[i]);
        return list;
    }

    private static double Dist(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
    private static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    private static Vec2 Sub(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    private static Vec2 Add(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    private static Vec2 Scale(Vec2 v, double s) => new(v.X * s, v.Y * s);
    private static Vec2 Neg(Vec2 v) => new(-v.X, -v.Y);

    private static Vec2 Normalize(Vec2 v)
    {
        var n = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return n > 1e-10 ? new Vec2(v.X / n, v.Y / n) : new Vec2(1, 0);
    }
}
