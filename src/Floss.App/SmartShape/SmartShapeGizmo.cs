using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.SmartShape;

public enum GizmoHandleKind
{
    Move,
    Rotate,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Top,
    Bottom,
    Left,
    Right,
    CurveAnchor,
    CurveControl
}

public readonly record struct GizmoHandle(GizmoHandleKind Kind, Vec2 Position, int Index = -1);

/// <summary>Handle layout and drag editing for the Gizmo phase (CSP-style).</summary>
public static class SmartShapeGizmo
{
    public static IReadOnlyList<GizmoHandle> ComputeHandles(SmartShapeModel shape)
    {
        var handles = new List<GizmoHandle>();

        if (shape is CurveShape curve)
        {
            for (var i = 0; i < curve.Curves.Count; i++)
            {
                var seg = curve.Curves[i];
                if (i == 0)
                    TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, seg.P0, i * 4));
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveControl, seg.P1, i * 4 + 1));
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveControl, seg.P2, i * 4 + 2));
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, seg.P3, i * 4 + 3));
            }
            AddTransformHandles(handles, shape);
            return handles;
        }

        var bounds = ComputeBounds(shape);
        var center = bounds.Center;
        handles.Add(new GizmoHandle(GizmoHandleKind.Move, center));
        handles.Add(new GizmoHandle(GizmoHandleKind.Rotate, new Vec2(center.X, bounds.MinY - 24)));

        handles.Add(new GizmoHandle(GizmoHandleKind.TopLeft, new Vec2(bounds.MinX, bounds.MinY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.TopRight, new Vec2(bounds.MaxX, bounds.MinY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.BottomRight, new Vec2(bounds.MaxX, bounds.MaxY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.BottomLeft, new Vec2(bounds.MinX, bounds.MaxY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Top, new Vec2(center.X, bounds.MinY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Bottom, new Vec2(center.X, bounds.MaxY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Left, new Vec2(bounds.MinX, center.Y)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Right, new Vec2(bounds.MaxX, center.Y)));

        if (shape is LineShape line)
        {
            handles.Clear();
            handles.Add(new GizmoHandle(GizmoHandleKind.Move, center));
            handles.Add(new GizmoHandle(GizmoHandleKind.CurveAnchor, line.Start, 0));
            handles.Add(new GizmoHandle(GizmoHandleKind.CurveAnchor, line.End, 1));
            return handles;
        }

        if (shape is PolylineShape poly)
        {
            for (var i = 0; i < poly.Points.Count; i++)
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, poly.Points[i], i));
            AddTransformHandles(handles, shape);
            return handles;
        }

        return handles;
    }

    public static GizmoHandle? HitTest(IReadOnlyList<GizmoHandle> handles, Vec2 point, double zoom)
    {
        var hitR = Math.Max(10.0 / zoom, 6.0);
        GizmoHandle? best = null;
        var bestDist = double.MaxValue;

        // Curve anchors/controls win over bbox handles (CSP: control points on the stroke).
        foreach (var h in handles)
        {
            if (h.Kind is not (GizmoHandleKind.CurveAnchor or GizmoHandleKind.CurveControl))
                continue;
            var d = Dist(h.Position, point);
            if (d > hitR || d >= bestDist)
                continue;
            bestDist = d;
            best = h;
        }
        if (best != null)
            return best;

        foreach (var h in handles)
        {
            var d = Dist(h.Position, point);
            if (d > hitR || d >= bestDist)
                continue;
            bestDist = d;
            best = h;
        }
        return best;
    }

    public static bool BboxContains(IReadOnlyList<GizmoHandle> handles, Vec2 point, double zoom)
    {
        var bounds = BoundsFromTransformHandles(handles);
        if (bounds == null)
            return false;

        var (minX, minY, maxX, maxY) = bounds.Value;
        var margin = Math.Max(10.0 / zoom, 6.0);
        return point.X >= minX - margin && point.X <= maxX + margin
            && point.Y >= minY - margin && point.Y <= maxY + margin;
    }

    public static ShapeBounds GetBounds(SmartShapeModel shape) => ComputeBounds(shape);

    public static SmartShapeModel ApplyDrag(
        SmartShapeModel shape,
        GizmoHandle handle,
        Vec2 dragStart,
        Vec2 current,
        SmartShapeModel shapeAtDragStart)
    {
        if (shape is CurveShape && handle.Kind is GizmoHandleKind.CurveAnchor or GizmoHandleKind.CurveControl)
            return DragCurvePoint((CurveShape)shapeAtDragStart, handle, current);

        if (shape is PolylineShape poly && handle.Kind == GizmoHandleKind.CurveAnchor)
            return DragPolylinePoint(poly, handle.Index, current);

        var center = SmartShapeAnalyzer.ShapeCenter(shapeAtDragStart);

        return handle.Kind switch
        {
            GizmoHandleKind.Move => SmartShapeTransforms.Move(shapeAtDragStart, current.X - dragStart.X, current.Y - dragStart.Y),
            GizmoHandleKind.Rotate => RotateFromPointer(shapeAtDragStart, center, dragStart, current),
            GizmoHandleKind.TopLeft or GizmoHandleKind.TopRight or GizmoHandleKind.BottomLeft or GizmoHandleKind.BottomRight
                => ScaleFromCorner(shapeAtDragStart, center, handle.Kind, dragStart, current),
            GizmoHandleKind.Top or GizmoHandleKind.Bottom or GizmoHandleKind.Left or GizmoHandleKind.Right
                => StretchEdge(shapeAtDragStart, center, handle.Kind, dragStart, current),
            GizmoHandleKind.CurveAnchor => ReplaceLineEndpoint(shapeAtDragStart, handle.Index, current),
            _ => shape
        };
    }

    private static SmartShapeModel DragCurvePoint(CurveShape curve, GizmoHandle handle, Vec2 pos)
    {
        var segs = curve.Curves.ToArray();
        var segIndex = handle.Index / 4;
        var pointIndex = handle.Index % 4;
        if (segIndex < 0 || segIndex >= segs.Length)
            return curve;

        var seg = segs[segIndex];
        segs[segIndex] = pointIndex switch
        {
            0 => (pos, seg.P1, seg.P2, seg.P3),
            1 => (seg.P0, pos, seg.P2, seg.P3),
            2 => (seg.P0, seg.P1, pos, seg.P3),
            3 => (seg.P0, seg.P1, seg.P2, pos),
            _ => seg
        };
        return new CurveShape(segs);
    }

    private static PolylineShape DragPolylinePoint(PolylineShape poly, int index, Vec2 pos)
    {
        if (index < 0 || index >= poly.Points.Count)
            return poly;
        var pts = poly.Points.ToArray();
        pts[index] = pos;
        return new PolylineShape(pts);
    }

    private static SmartShapeModel ReplaceLineEndpoint(SmartShapeModel shape, int index, Vec2 pos)
    {
        if (shape is not LineShape line)
            return shape;
        return index == 0
            ? line with { Start = pos }
            : line with { End = pos };
    }

    private static SmartShapeModel RotateFromPointer(SmartShapeModel shape, Vec2 center, Vec2 dragStart, Vec2 current)
    {
        var a0 = Math.Atan2(dragStart.Y - center.Y, dragStart.X - center.X);
        var a1 = Math.Atan2(current.Y - center.Y, current.X - center.X);
        var deg = (a1 - a0) * (180.0 / Math.PI);
        return SmartShapeTransforms.Transform(shape, center.X, center.Y, 1.0, deg);
    }

    private static SmartShapeModel ScaleFromCorner(
        SmartShapeModel shape, Vec2 center, GizmoHandleKind corner, Vec2 dragStart, Vec2 current)
    {
        var d0 = Dist(dragStart, center);
        var d1 = Dist(current, center);
        if (d0 < 1e-6)
            return shape;
        var scale = Math.Max(0.05, d1 / d0);
        return SmartShapeTransforms.Transform(shape, center.X, center.Y, scale, 0);
    }

    private static SmartShapeModel StretchEdge(
        SmartShapeModel shape, Vec2 center, GizmoHandleKind edge, Vec2 dragStart, Vec2 current)
    {
        if (shape is RectangleShape r)
            return StretchRectangle(r, edge, dragStart, current);
        if (shape is EllipseShape e)
            return StretchEllipse(e, edge, dragStart, current);

        var dx = current.X - dragStart.X;
        var dy = current.Y - dragStart.Y;
        return edge switch
        {
            GizmoHandleKind.Left or GizmoHandleKind.Right => SmartShapeTransforms.Move(shape, dx * 0.5, 0),
            _ => SmartShapeTransforms.Move(shape, 0, dy * 0.5)
        };
    }

    private static SmartShapeModel StretchRectangle(RectangleShape r, GizmoHandleKind edge, Vec2 dragStart, Vec2 current)
    {
        var dx = current.X - dragStart.X;
        var dy = current.Y - dragStart.Y;
        return edge switch
        {
            GizmoHandleKind.Left => r with { Width = Math.Max(1, r.Width - dx), Center = new Vec2(r.Center.X + dx * 0.5, r.Center.Y) },
            GizmoHandleKind.Right => r with { Width = Math.Max(1, r.Width + dx), Center = new Vec2(r.Center.X + dx * 0.5, r.Center.Y) },
            GizmoHandleKind.Top => r with { Height = Math.Max(1, r.Height - dy), Center = new Vec2(r.Center.X, r.Center.Y + dy * 0.5) },
            GizmoHandleKind.Bottom => r with { Height = Math.Max(1, r.Height + dy), Center = new Vec2(r.Center.X, r.Center.Y + dy * 0.5) },
            _ => r
        };
    }

    private static SmartShapeModel StretchEllipse(EllipseShape e, GizmoHandleKind edge, Vec2 dragStart, Vec2 current)
    {
        var dx = current.X - dragStart.X;
        var dy = current.Y - dragStart.Y;
        return edge switch
        {
            GizmoHandleKind.Left or GizmoHandleKind.Right => e with { Rx = Math.Max(1, e.Rx + dx * 0.5) },
            GizmoHandleKind.Top or GizmoHandleKind.Bottom => e with { Ry = Math.Max(1, e.Ry + dy * 0.5) },
            _ => e
        };
    }

    private static void AddTransformHandles(List<GizmoHandle> handles, SmartShapeModel shape)
    {
        var bounds = ComputeBounds(shape);
        var center = bounds.Center;
        handles.Add(new GizmoHandle(GizmoHandleKind.Move, center));
        handles.Add(new GizmoHandle(GizmoHandleKind.Rotate, new Vec2(center.X, bounds.MinY - 24)));
        handles.Add(new GizmoHandle(GizmoHandleKind.TopLeft, new Vec2(bounds.MinX, bounds.MinY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.TopRight, new Vec2(bounds.MaxX, bounds.MinY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.BottomRight, new Vec2(bounds.MaxX, bounds.MaxY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.BottomLeft, new Vec2(bounds.MinX, bounds.MaxY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Top, new Vec2(center.X, bounds.MinY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Bottom, new Vec2(center.X, bounds.MaxY)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Left, new Vec2(bounds.MinX, center.Y)));
        handles.Add(new GizmoHandle(GizmoHandleKind.Right, new Vec2(bounds.MaxX, center.Y)));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY)? BoundsFromTransformHandles(
        IReadOnlyList<GizmoHandle> handles)
    {
        double? minX = null, minY = null, maxX = null, maxY = null;
        foreach (var h in handles)
        {
            if (h.Kind is not (GizmoHandleKind.TopLeft or GizmoHandleKind.TopRight
                or GizmoHandleKind.BottomLeft or GizmoHandleKind.BottomRight))
                continue;
            minX = minX.HasValue ? Math.Min(minX.Value, h.Position.X) : h.Position.X;
            minY = minY.HasValue ? Math.Min(minY.Value, h.Position.Y) : h.Position.Y;
            maxX = maxX.HasValue ? Math.Max(maxX.Value, h.Position.X) : h.Position.X;
            maxY = maxY.HasValue ? Math.Max(maxY.Value, h.Position.Y) : h.Position.Y;
        }

        return minX.HasValue ? (minX.Value, minY!.Value, maxX!.Value, maxY!.Value) : null;
    }

    private static ShapeBounds ComputeBounds(SmartShapeModel shape)
    {
        var pts = SmartShapePolyline.ToDocumentPoints(shape);
        if (pts.Count == 0)
        {
            var c = SmartShapeAnalyzer.ShapeCenter(shape);
            return new ShapeBounds(c.X, c.Y, c.X, c.Y, c);
        }

        var minX = pts.Min(p => p.X);
        var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxY = pts.Max(p => p.Y);
        var center = new Vec2((minX + maxX) * 0.5, (minY + maxY) * 0.5);
        return new ShapeBounds(minX, minY, maxX, maxY, center);
    }

    private static double Dist(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void TryAddHandle(List<GizmoHandle> handles, GizmoHandle handle)
    {
        foreach (var h in handles)
        {
            if (h.Kind != handle.Kind)
                continue;
            if (Dist(h.Position, handle.Position) < 2.0)
                return;
        }
        handles.Add(handle);
    }

    public readonly record struct ShapeBounds(double MinX, double MinY, double MaxX, double MaxY, Vec2 Center);
}
