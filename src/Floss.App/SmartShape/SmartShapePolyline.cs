using System;
using System.Collections.Generic;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.SmartShape;

/// <summary>Decompose fitted shapes into sample chains for brush-engine rasterization.</summary>
public static class SmartShapePolyline
{
    private const int CurvePointCount = 64;

    public static List<CanvasInputSample> ToDocumentSamples(
        SmartShapeModel shape,
        DrawingLayer layer,
        double avgPressure)
    {
        var docPts = ToDocumentPoints(shape);
        if (docPts.Count < 2)
            return [];

        var samples = new List<CanvasInputSample>(docPts.Count);
        foreach (var p in docPts)
        {
            samples.Add(new CanvasInputSample(
                (float)(p.X - layer.OffsetX),
                (float)(p.Y - layer.OffsetY),
                avgPressure, 0, 0, 0, 0, 0, 0,
                CanvasInputPhase.Move));
        }
        return samples;
    }

    public static List<Vec2> ToDocumentPoints(SmartShapeModel shape) => shape switch
    {
        LineShape l => [l.Start, l.End],
        PolylineShape pl => [.. pl.Points],
        CircleShape c => EllipsePoints(c.Center, c.Radius, c.Radius, 0),
        EllipseShape e => EllipsePoints(e.Center, e.Rx, e.Ry, e.AngleDeg),
        RectangleShape r => RectanglePoints(r),
        TriangleShape t => ClosedPolyline(t.Points),
        PolygonShape p => ClosedPolyline(p.Points),
        CurveShape c => CurvePoints(c),
        _ => []
    };

    private static List<Vec2> ClosedPolyline(IReadOnlyList<Vec2> points)
    {
        if (points.Count == 0)
            return [];
        var pts = new List<Vec2>(points.Count + 1);
        pts.AddRange(points);
        pts.Add(points[0]);
        return pts;
    }

    private static List<Vec2> RectanglePoints(RectangleShape r)
    {
        var hw = r.Width * 0.5;
        var hh = r.Height * 0.5;
        var rad = r.AngleDeg * (Math.PI / 180.0);
        var ca = Math.Cos(rad);
        var sa = Math.Sin(rad);
        var corners = new[]
        {
            new Vec2(-hw, -hh), new Vec2(hw, -hh), new Vec2(hw, hh), new Vec2(-hw, hh)
        };
        var pts = new List<Vec2>(5);
        foreach (var c in corners)
        {
            var rx = ca * c.X - sa * c.Y;
            var ry = sa * c.X + ca * c.Y;
            pts.Add(new Vec2(r.Center.X + rx, r.Center.Y + ry));
        }
        pts.Add(pts[0]);
        return pts;
    }

    private static List<Vec2> EllipsePoints(Vec2 center, double rx, double ry, double angleDeg)
    {
        var pts = new List<Vec2>(CurvePointCount + 1);
        var rad = angleDeg * (Math.PI / 180.0);
        var ca = Math.Cos(rad);
        var sa = Math.Sin(rad);
        for (var i = 0; i <= CurvePointCount; i++)
        {
            var t = i * (2 * Math.PI / CurvePointCount);
            var ex = rx * Math.Cos(t);
            var ey = ry * Math.Sin(t);
            var x = center.X + ca * ex - sa * ey;
            var y = center.Y + sa * ex + ca * ey;
            pts.Add(new Vec2(x, y));
        }
        return pts;
    }

    private static List<Vec2> CurvePoints(CurveShape curve)
    {
        var pts = new List<Vec2>();
        foreach (var seg in curve.Curves)
        {
            var start = pts.Count == 0 ? 0 : 1;
            for (var i = start; i <= CurvePointCount; i++)
            {
                var t = (double)i / CurvePointCount;
                var u = 1 - t;
                var b0 = u * u * u;
                var b1 = 3 * t * u * u;
                var b2 = 3 * t * t * u;
                var b3 = t * t * t;
                var x = b0 * seg.P0.X + b1 * seg.P1.X + b2 * seg.P2.X + b3 * seg.P3.X;
                var y = b0 * seg.P0.Y + b1 * seg.P1.Y + b2 * seg.P2.Y + b3 * seg.P3.Y;
                pts.Add(new Vec2(x, y));
            }
        }
        return pts;
    }
}
