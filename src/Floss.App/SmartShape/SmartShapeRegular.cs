using System;
using System.Collections.Generic;

namespace Floss.App.SmartShape;

/// <summary>CSP Shift-hold constraints — perfect circle, square, regular polygon.</summary>
public static class SmartShapeRegular
{
    public static SmartShapeModel Constrain(SmartShapeModel shape)
    {
        return shape switch
        {
            EllipseShape e =>
                new CircleShape(e.Center, Math.Max(e.Rx, e.Ry)),
            RectangleShape r =>
                r with { Width = Math.Max(r.Width, r.Height), Height = Math.Max(r.Width, r.Height) },
            PolygonShape p when p.Points.Count >= 3 =>
                RegularizePolygon(p),
            TriangleShape t when t.Points.Count == 3 =>
                RegularizeTriangle(t),
            _ => shape
        };
    }

    private static TriangleShape RegularizeTriangle(TriangleShape t)
    {
        var c = Centroid(t.Points);
        var avgR = 0.0;
        foreach (var p in t.Points)
            avgR += Dist(p, c);
        avgR /= t.Points.Count;
        var start = Math.Atan2(t.Points[0].Y - c.Y, t.Points[0].X - c.X);
        var verts = new Vec2[3];
        for (var i = 0; i < 3; i++)
        {
            var a = start + i * (2 * Math.PI / 3);
            verts[i] = new Vec2(c.X + avgR * Math.Cos(a), c.Y + avgR * Math.Sin(a));
        }
        return new TriangleShape(verts);
    }

    private static PolygonShape RegularizePolygon(PolygonShape p)
    {
        var c = Centroid(p.Points);
        var avgR = 0.0;
        foreach (var pt in p.Points)
            avgR += Dist(pt, c);
        avgR /= p.Points.Count;
        var n = p.Points.Count;
        var start = Math.Atan2(p.Points[0].Y - c.Y, p.Points[0].X - c.X);
        var verts = new Vec2[n];
        for (var i = 0; i < n; i++)
        {
            var a = start + i * (2 * Math.PI / n);
            verts[i] = new Vec2(c.X + avgR * Math.Cos(a), c.Y + avgR * Math.Sin(a));
        }
        return new PolygonShape(verts);
    }

    private static Vec2 Centroid(IReadOnlyList<Vec2> points)
    {
        var sx = 0.0;
        var sy = 0.0;
        foreach (var p in points)
        {
            sx += p.X;
            sy += p.Y;
        }
        return new Vec2(sx / points.Count, sy / points.Count);
    }

    private static double Dist(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
