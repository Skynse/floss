using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.SmartShape;

/// <summary>Force-fit raw stroke samples to a CSP launcher shape type.</summary>
public static class SmartShapeFitter
{
    public static SmartShapeModel? Fit(IReadOnlyList<Vec2> points, SmartShapeFitKind kind)
    {
        if (points.Count < 4)
            return null;

        if (kind == SmartShapeFitKind.Auto)
            return SmartShapeAnalyzer.AnalyzeStroke(points);

        return kind switch
        {
            SmartShapeFitKind.StraightLine => FitLine(points),
            SmartShapeFitKind.Polyline => FitPolyline(points),
            SmartShapeFitKind.Curve => (SmartShapeModel?)FitCurve(points, maxSegments: 4, error: 12.0) ?? FitLine(points),
            SmartShapeFitKind.ContinuousCurve => (SmartShapeModel?)FitCurve(points, maxSegments: 12, error: 8.0) ?? FitLine(points),
            SmartShapeFitKind.Triangle => FitTriangle(points, equilateral: false),
            SmartShapeFitKind.EquilateralTriangle => FitTriangle(points, equilateral: true),
            SmartShapeFitKind.Quadrilateral => FitQuadrilateral(points),
            SmartShapeFitKind.Rectangle => FitRectangle(points, square: false),
            SmartShapeFitKind.Square => FitRectangle(points, square: true),
            SmartShapeFitKind.Ellipse => FitEllipse(points, circle: false),
            SmartShapeFitKind.Circle => FitEllipse(points, circle: true),
            SmartShapeFitKind.Polygon => FitPolygon(points, regular: false),
            SmartShapeFitKind.RegularPolygon => FitPolygon(points, regular: true),
            _ => SmartShapeAnalyzer.AnalyzeStroke(points)
        };
    }

    public static SmartShapeFitKind DetectFitKind(SmartShapeModel shape, bool strokeClosed)
    {
        if (shape is LineShape)
            return SmartShapeFitKind.StraightLine;
        if (shape is PolylineShape)
            return SmartShapeFitKind.Polyline;
        if (shape is CurveShape c)
            return c.Curves.Count <= 4 ? SmartShapeFitKind.Curve : SmartShapeFitKind.ContinuousCurve;
        if (shape is CircleShape)
            return SmartShapeFitKind.Circle;
        if (shape is EllipseShape)
            return SmartShapeFitKind.Ellipse;
        if (shape is RectangleShape r)
            return Math.Abs(r.Width - r.Height) < 1.0 ? SmartShapeFitKind.Square : SmartShapeFitKind.Rectangle;
        if (shape is TriangleShape)
            return SmartShapeFitKind.Triangle;
        if (shape is PolygonShape p)
        {
            if (p.Points.Count == 4)
                return SmartShapeFitKind.Quadrilateral;
            return IsRegularPolygon(p.Points) ? SmartShapeFitKind.RegularPolygon : SmartShapeFitKind.Polygon;
        }

        return strokeClosed ? SmartShapeFitKind.Ellipse : SmartShapeFitKind.Curve;
    }

    public static bool StrokeIsClosed(IReadOnlyList<Vec2> points, double threshold = 0.12)
    {
        if (points.Count < 4)
            return false;
        var total = 0.0;
        for (var i = 0; i < points.Count - 1; i++)
            total += Dist(points[i], points[i + 1]);
        if (total < 1)
            return false;
        return Dist(points[^1], points[0]) / total < threshold;
    }

    private static LineShape FitLine(IReadOnlyList<Vec2> pts) => new(pts[0], pts[^1]);

    private static PolylineShape FitPolyline(IReadOnlyList<Vec2> pts)
    {
        var simplified = SmartShapeAnalyzer.RdpSimplify(pts, 6.0);
        return new PolylineShape(simplified.Count >= 2 ? simplified : [pts[0], pts[^1]]);
    }

    private static CurveShape? FitCurve(IReadOnlyList<Vec2> pts, int maxSegments, double error)
    {
        var simplified = SmartShapeAnalyzer.RdpSimplify(pts, 6.0);
        var fitPts = simplified.Count >= 4 ? simplified : SmartShapeAnalyzer.RdpSimplify(pts, 4.0);
        if (fitPts.Count < 2)
            return null;

        var curves = FitBezierCurves(fitPts, error);
        curves = ConsolidateBezierCurves(curves, maxSegments);
        if (curves.Count == 0)
            return null;
        if (curves.Count == 1 && IsNearlyStraightBezier(curves[0]))
            return null;
        return new CurveShape(curves);
    }

    private static TriangleShape FitTriangle(IReadOnlyList<Vec2> pts, bool equilateral)
    {
        if (equilateral)
        {
            var bounds = Bounds(pts);
            var r = Math.Max(bounds.Width, bounds.Height) * 0.5;
            var cx = bounds.Center.X;
            var cy = bounds.Center.Y;
            var angles = new[] { -90.0, 150.0, 30.0 };
            var vertices = angles.Select(a =>
            {
                var rad = a * Math.PI / 180.0;
                return new Vec2(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
            }).ToList();
            return new TriangleShape(vertices);
        }

        var simplified = SmartShapeAnalyzer.RdpSimplify(pts, 4.0);
        return new TriangleShape(FindCorners(pts, simplified, 3));
    }

    private static PolygonShape FitQuadrilateral(IReadOnlyList<Vec2> pts)
    {
        var simplified = SmartShapeAnalyzer.RdpSimplify(pts, 4.0);
        var corners = FindCorners(pts, simplified, 4);
        return new PolygonShape(corners);
    }

    private static RectangleShape FitRectangle(IReadOnlyList<Vec2> pts, bool square)
    {
        var rect = FitObb(pts);
        if (square)
        {
            var s = Math.Max(rect.Width, rect.Height);
            return new RectangleShape(rect.Center, s, s, rect.Angle);
        }
        return new RectangleShape(rect.Center, rect.Width, rect.Height, rect.Angle);
    }

    private static SmartShapeModel FitEllipse(IReadOnlyList<Vec2> pts, bool circle)
    {
        var (cx, cy, rx, ry, angle) = FitEllipseAlgebraic(pts);
        if (circle)
        {
            var r = (rx + ry) * 0.5;
            return new CircleShape(new Vec2(cx, cy), r);
        }
        return new EllipseShape(new Vec2(cx, cy), rx, ry, angle);
    }

    private static PolygonShape FitPolygon(IReadOnlyList<Vec2> pts, bool regular)
    {
        if (regular)
        {
            var simplified = SmartShapeAnalyzer.RdpSimplify(pts, 4.0);
            var n = Math.Clamp(simplified.Count, 3, 8);
            var bounds = Bounds(pts);
            var r = Math.Max(bounds.Width, bounds.Height) * 0.5;
            var cx = bounds.Center.X;
            var cy = bounds.Center.Y;
            var start = Math.Atan2(pts[0].Y - cy, pts[0].X - cx);
            var vertices = new List<Vec2>(n);
            for (var i = 0; i < n; i++)
            {
                var t = start + i * (2 * Math.PI / n);
                vertices.Add(new Vec2(cx + r * Math.Cos(t), cy + r * Math.Sin(t)));
            }
            return new PolygonShape(vertices);
        }

        var simp = SmartShapeAnalyzer.RdpSimplify(pts, 4.0);
        var corners = FindCorners(pts, simp, Math.Min(simp.Count, 8));
        return new PolygonShape(corners);
    }

    private static bool IsRegularPolygon(IReadOnlyList<Vec2> points)
    {
        if (points.Count < 3)
            return false;
        var center = Centroid(points);
        var dists = points.Select(p => Dist(p, center)).ToList();
        var avg = dists.Average();
        return dists.All(d => Math.Abs(d - avg) < avg * 0.15);
    }

    // ── Ported helpers (mirror SmartShapeAnalyzer internals) ──

    private static List<Vec2> FindCorners(IReadOnlyList<Vec2> pts, IReadOnlyList<Vec2> simplified, int n)
        => SmartShapeAnalyzer.FindCornersForFit(pts, simplified, n);

    private static (Vec2 Center, double Width, double Height, double Angle) FitObb(IReadOnlyList<Vec2> pts)
        => SmartShapeAnalyzer.FitObbForFit(pts);

    private static (double Cx, double Cy, double Rx, double Ry, double Angle) FitEllipseAlgebraic(IReadOnlyList<Vec2> pts)
        => SmartShapeAnalyzer.FitEllipseAlgebraicForFit(pts);

    private static List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> FitBezierCurves(IReadOnlyList<Vec2> pts, double error)
        => SmartShapeAnalyzer.FitBezierCurvesForFit(pts, error);

    private static List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> ConsolidateBezierCurves(
        List<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> curves, int maxSegments)
        => SmartShapeAnalyzer.ConsolidateBezierCurvesForFit(curves, maxSegments);

    private static bool IsNearlyStraightBezier((Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3) seg)
        => SmartShapeAnalyzer.IsNearlyStraightBezierForFit(seg);

    private static (Vec2 Center, double Width, double Height) Bounds(IReadOnlyList<Vec2> pts)
    {
        var minX = pts.Min(p => p.X);
        var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxY = pts.Max(p => p.Y);
        return (new Vec2((minX + maxX) * 0.5, (minY + maxY) * 0.5), maxX - minX, maxY - minY);
    }

    private static Vec2 Centroid(IReadOnlyList<Vec2> points)
    {
        var sx = points.Sum(p => p.X);
        var sy = points.Sum(p => p.Y);
        return new Vec2(sx / points.Count, sy / points.Count);
    }

    private static double Dist(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
