using System;
using System.Linq;
using Avalonia;

namespace Floss.App.SmartShape;

/// <summary>Port of transform_shape / move_shape from stroke_analyzer.py.</summary>
public static class SmartShapeTransforms
{
    public static SmartShapeModel Move(SmartShapeModel shape, double dx, double dy)
    {
        Vec2 M(Vec2 p) => new(p.X + dx, p.Y + dy);
        return shape switch
        {
            LineShape l => new LineShape(M(l.Start), M(l.End)),
            PolylineShape pl => new PolylineShape(pl.Points.Select(M).ToList()),
            CurveShape c => new CurveShape(c.Curves.Select(seg =>
                (M(seg.P0), M(seg.P1), M(seg.P2), M(seg.P3))).ToList()),
            CircleShape c => c with { Center = M(c.Center) },
            EllipseShape e => e with { Center = M(e.Center) },
            RectangleShape r => r with { Center = M(r.Center) },
            TriangleShape t => new TriangleShape(t.Points.Select(M).ToList()),
            PolygonShape p => new PolygonShape(p.Points.Select(M).ToList()),
            _ => shape
        };
    }

    public static SmartShapeModel Transform(SmartShapeModel shape, double cx, double cy, double scale, double angleDeg)
    {
        var a = angleDeg * (Math.PI / 180.0);
        var ca = Math.Cos(a);
        var sa = Math.Sin(a);

        Vec2 Xf(double px, double py)
        {
            var dx = px - cx;
            var dy = py - cy;
            var rx = ca * dx - sa * dy;
            var ry = sa * dx + ca * dy;
            return new Vec2(cx + rx * scale, cy + ry * scale);
        }

        return shape switch
        {
            LineShape l => new LineShape(Xf(l.Start.X, l.Start.Y), Xf(l.End.X, l.End.Y)),
            PolylineShape pl => new PolylineShape(pl.Points.Select(p => Xf(p.X, p.Y)).ToList()),
            CurveShape c => new CurveShape(c.Curves.Select(seg =>
                (Xf(seg.P0.X, seg.P0.Y), Xf(seg.P1.X, seg.P1.Y), Xf(seg.P2.X, seg.P2.Y), Xf(seg.P3.X, seg.P3.Y))).ToList()),
            CircleShape c => new CircleShape(Xf(c.Center.X, c.Center.Y), c.Radius * scale),
            EllipseShape e => new EllipseShape(Xf(e.Center.X, e.Center.Y), e.Rx * scale, e.Ry * scale, e.AngleDeg + angleDeg),
            RectangleShape r => new RectangleShape(Xf(r.Center.X, r.Center.Y), r.Width * scale, r.Height * scale, r.AngleDeg + angleDeg),
            TriangleShape t => new TriangleShape(t.Points.Select(p => Xf(p.X, p.Y)).ToList()),
            PolygonShape p => new PolygonShape(p.Points.Select(pt => Xf(pt.X, pt.Y)).ToList()),
            _ => shape
        };
    }

    /// <summary>Apply the same scale/rotate/move frame as <see cref="Tools.SelectionTransformOperation"/>.</summary>
    public static SmartShapeModel ApplyTransformFrame(
        SmartShapeModel shape,
        Rect baseRect,
        Rect currentRect,
        double angleDeg)
    {
        var bx = baseRect.X + baseRect.Width * 0.5;
        var by = baseRect.Y + baseRect.Height * 0.5;
        var cx = currentRect.X + currentRect.Width * 0.5;
        var cy = currentRect.Y + currentRect.Height * 0.5;
        var sx = baseRect.Width > 0.001 ? currentRect.Width / baseRect.Width : 1.0;
        var sy = baseRect.Height > 0.001 ? currentRect.Height / baseRect.Height : 1.0;
        var rad = angleDeg * (Math.PI / 180.0);
        var ca = Math.Cos(rad);
        var sa = Math.Sin(rad);

        Vec2 Map(Vec2 p)
        {
            var lx = (p.X - bx) * sx;
            var ly = (p.Y - by) * sy;
            var rx = ca * lx - sa * ly;
            var ry = sa * lx + ca * ly;
            return new Vec2(cx + rx, cy + ry);
        }

        return shape switch
        {
            LineShape l => new LineShape(Map(l.Start), Map(l.End)),
            PolylineShape pl => new PolylineShape(pl.Points.Select(Map).ToList()),
            CurveShape c => new CurveShape(c.Curves.Select(seg =>
                (Map(seg.P0), Map(seg.P1), Map(seg.P2), Map(seg.P3))).ToList()),
            CircleShape c => new CircleShape(Map(c.Center), c.Radius * Math.Sqrt(sx * sy)),
            EllipseShape e => new EllipseShape(Map(e.Center), e.Rx * sx, e.Ry * sy, e.AngleDeg + angleDeg),
            RectangleShape r => new RectangleShape(Map(r.Center), r.Width * sx, r.Height * sy, r.AngleDeg + angleDeg),
            TriangleShape t => new TriangleShape(t.Points.Select(Map).ToList()),
            PolygonShape p => new PolygonShape(p.Points.Select(Map).ToList()),
            _ => shape
        };
    }
}
