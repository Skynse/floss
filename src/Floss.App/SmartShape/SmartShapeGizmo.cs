using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

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

/// <summary>Handle layout and drag editing for the Gizmo phase (selection-transform aligned).</summary>
public static class SmartShapeGizmo
{
    private const double NeighborInfluence = 0.38;
    private const double MinFrameSize = 8.0;
    /// <summary>Minimum inset between shape geometry/anchors and the transform frame (document px).</summary>
    private const double FramePaddingMin = 28.0;
    private const double FramePaddingRatio = 0.15;

    public static Rect GetFrameRect(SmartShapeModel shape) => shape switch
    {
        RectangleShape r => PadFrameRect(OrientedRect(r.Center, r.Width, r.Height)),
        EllipseShape e => PadFrameRect(OrientedRect(e.Center, e.Rx * 2, e.Ry * 2)),
        _ => GetBoundsRect(shape)
    };

    public static double GetFrameAngle(SmartShapeModel shape) => shape switch
    {
        RectangleShape r => r.AngleDeg,
        EllipseShape e => e.AngleDeg,
        _ => 0
    };

    public static IReadOnlyList<GizmoHandle> ComputeHandles(
        SmartShapeModel shape,
        Rect frameRect,
        double frameAngleDeg)
    {
        var handles = new List<GizmoHandle>();

        if (shape is CurveShape curve)
        {
            for (var i = 0; i < curve.Curves.Count; i++)
            {
                var seg = curve.Curves[i];
                if (i == 0)
                    TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, seg.P0, 0));
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, seg.P3, i + 1));
            }
            AddTransformHandles(handles, frameRect, frameAngleDeg);
            return handles;
        }

        if (shape is LineShape line)
        {
            AddTransformHandles(handles, frameRect, frameAngleDeg);
            handles.Add(new GizmoHandle(GizmoHandleKind.CurveAnchor, line.Start, 0));
            handles.Add(new GizmoHandle(GizmoHandleKind.CurveAnchor, line.End, 1));
            return handles;
        }

        if (shape is PolylineShape poly)
        {
            for (var i = 0; i < poly.Points.Count; i++)
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, poly.Points[i], i));
            AddTransformHandles(handles, frameRect, frameAngleDeg);
            return handles;
        }

        if (shape is TriangleShape triangle)
        {
            for (var i = 0; i < triangle.Points.Count; i++)
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, triangle.Points[i], i));
            AddTransformHandles(handles, frameRect, frameAngleDeg);
            return handles;
        }

        if (shape is PolygonShape polygon)
        {
            for (var i = 0; i < polygon.Points.Count; i++)
                TryAddHandle(handles, new GizmoHandle(GizmoHandleKind.CurveAnchor, polygon.Points[i], i));
            AddTransformHandles(handles, frameRect, frameAngleDeg);
            return handles;
        }

        AddTransformHandles(handles, frameRect, frameAngleDeg);
        return handles;
    }

    public static GizmoHandle? HitTest(
        SmartShapeModel shape,
        Rect frameRect,
        double frameAngleDeg,
        IReadOnlyList<GizmoHandle> handles,
        Vec2 point,
        double zoom)
    {
        var handleSize = HandleSize(zoom);

        foreach (var h in handles)
        {
            if (h.Kind != GizmoHandleKind.CurveAnchor)
                continue;
            if (CenteredRect(ToPoint(h.Position), handleSize).Contains(new Point(point.X, point.Y)))
                return h;
        }

        var local = ToFrameLocal(point, frameRect, frameAngleDeg);
        var rect = NormalizeRect(frameRect);

        foreach (var (kind, localPt) in TransformHandlePoints(rect))
        {
            if (kind == GizmoHandleKind.Move)
                continue;
            if (CenteredRect(localPt, handleSize).Contains(new Point(local.X, local.Y)))
                return new GizmoHandle(kind, ToFrameWorld(localPt, frameRect, frameAngleDeg));
        }

        if (rect.Contains(new Point(local.X, local.Y)))
            return new GizmoHandle(GizmoHandleKind.Move, ToFrameWorld(CenterOf(rect), frameRect, frameAngleDeg));

        return null;
    }

    public static bool FrameContainsMoveRegion(Rect frameRect, double frameAngleDeg, Vec2 point)
    {
        var local = ToFrameLocal(point, frameRect, frameAngleDeg);
        return NormalizeRect(frameRect).Contains(new Point(local.X, local.Y));
    }

    public static (Rect Rect, double AngleDeg) UpdateFrameDrag(
        GizmoHandleKind handle,
        Rect startRect,
        double startAngleDeg,
        Vec2 dragStart,
        Vec2 current,
        SmartShapeGizmoModifiers mods)
    {
        var rect = NormalizeRect(startRect);
        var localStart = ToFrameLocal(dragStart, startRect, startAngleDeg);
        var localCurrent = ToFrameLocal(current, startRect, startAngleDeg);
        var dx = localCurrent.X - localStart.X;
        var dy = localCurrent.Y - localStart.Y;
        var uniform = mods.KeepAspectRatio;

        return handle switch
        {
            GizmoHandleKind.Move => (new Rect(rect.X + dx, rect.Y + dy, rect.Width, rect.Height), startAngleDeg),
            GizmoHandleKind.Rotate => (rect, RotationAngle(rect, dragStart, current, startAngleDeg)),
            GizmoHandleKind.TopLeft or GizmoHandleKind.TopRight or GizmoHandleKind.BottomLeft or GizmoHandleKind.BottomRight
                => (uniform
                    ? UniformScaleRect(rect, handle, dx, dy)
                    : AxisResizeRect(rect, handle, dx, dy, uniformSides: false), startAngleDeg),
            GizmoHandleKind.Top or GizmoHandleKind.Bottom or GizmoHandleKind.Left or GizmoHandleKind.Right
                => (AxisResizeRect(rect, handle, dx, dy, uniform), startAngleDeg),
            _ => (rect, startAngleDeg)
        };
    }

    public static SmartShapeModel ApplyFrameToShape(
        SmartShapeModel shapeAtDragStart,
        Rect baseRect,
        double baseAngleDeg,
        Rect currentRect,
        double currentAngleDeg)
        => SmartShapeTransforms.ApplyTransformFrame(
            shapeAtDragStart,
            baseRect,
            currentRect,
            currentAngleDeg - baseAngleDeg);

    public static SmartShapeModel ApplyAnchorDrag(SmartShapeModel shapeAtDragStart, GizmoHandle handle, Vec2 pos)
    {
        if (shapeAtDragStart is CurveShape && handle.Kind == GizmoHandleKind.CurveAnchor)
            return DragCurveAnchor((CurveShape)shapeAtDragStart, handle.Index, pos);

        if (shapeAtDragStart is PolylineShape && handle.Kind == GizmoHandleKind.CurveAnchor)
            return DragPolylinePoint((PolylineShape)shapeAtDragStart, handle.Index, pos);

        if (shapeAtDragStart is TriangleShape tri && handle.Kind == GizmoHandleKind.CurveAnchor)
            return DragClosedPoints(tri, handle.Index, pos);

        if (shapeAtDragStart is PolygonShape poly && handle.Kind == GizmoHandleKind.CurveAnchor)
            return DragClosedPoints(poly, handle.Index, pos);

        if (handle.Kind == GizmoHandleKind.CurveAnchor)
            return ReplaceLineEndpoint(shapeAtDragStart, handle.Index, pos);

        return shapeAtDragStart;
    }

    public static ShapeBounds GetBounds(SmartShapeModel shape) => ComputeBounds(shape);

    public static Rect GetBoundsRect(SmartShapeModel shape)
    {
        var b = ComputeBounds(shape);
        var w = Math.Max(b.MaxX - b.MinX, MinFrameSize);
        var h = Math.Max(b.MaxY - b.MinY, MinFrameSize);
        var cx = (b.MinX + b.MaxX) * 0.5;
        var cy = (b.MinY + b.MaxY) * 0.5;
        return PadFrameRect(new Rect(cx - w * 0.5, cy - h * 0.5, w, h));
    }

    private static double FramePaddingForRect(Rect rect)
    {
        var span = Math.Max(rect.Width, rect.Height);
        return Math.Max(FramePaddingMin, span * FramePaddingRatio);
    }

    private static Rect PadFrameRect(Rect rect)
    {
        var pad = FramePaddingForRect(rect);
        return new Rect(rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2);
    }

    public static Vec2 ToFrameLocal(Vec2 point, Rect frameRect, double angleDeg)
    {
        var center = CenterOf(NormalizeRect(frameRect));
        var rad = -angleDeg * (Math.PI / 180.0);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return new Vec2(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    public static Vec2 ToFrameWorld(Point localPoint, Rect frameRect, double angleDeg)
        => ToFrameWorld(new Vec2(localPoint.X, localPoint.Y), frameRect, angleDeg);

    public static Vec2 ToFrameWorld(Vec2 localPoint, Rect frameRect, double angleDeg)
    {
        var center = CenterOf(NormalizeRect(frameRect));
        var rad = angleDeg * (Math.PI / 180.0);
        var dx = localPoint.X - center.X;
        var dy = localPoint.Y - center.Y;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return new Vec2(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    public static double HandleSize(double zoom) => Math.Max(8.0 / zoom, 6.0);

    private static Rect OrientedRect(Vec2 center, double width, double height)
        => new(center.X - width * 0.5, center.Y - height * 0.5, width, height);

    private static SmartShapeModel DragCurveAnchor(CurveShape curve, int knotIndex, Vec2 newPos)
    {
        var segs = curve.Curves.ToArray();
        if (segs.Length == 0)
            return curve;

        if (knotIndex <= 0)
        {
            var seg = segs[0];
            var delta = Sub(newPos, seg.P0);
            segs[0] = (newPos, Add(seg.P1, delta), seg.P2, seg.P3);
            return new CurveShape(segs);
        }

        if (knotIndex >= segs.Length)
        {
            var i = segs.Length - 1;
            var seg = segs[i];
            var delta = Sub(newPos, seg.P3);
            segs[i] = (seg.P0, seg.P1, Add(seg.P2, delta), newPos);
            return new CurveShape(segs);
        }

        var left = segs[knotIndex - 1];
        var right = segs[knotIndex];
        var oldKnot = left.P3;
        var deltaKnot = Sub(newPos, oldKnot);
        segs[knotIndex - 1] = (left.P0, left.P1, Add(left.P2, deltaKnot), newPos);
        segs[knotIndex] = (newPos, Add(right.P1, deltaKnot), right.P2, right.P3);
        return new CurveShape(segs);
    }

    private static PolylineShape DragPolylinePoint(PolylineShape poly, int index, Vec2 pos)
    {
        if (index < 0 || index >= poly.Points.Count)
            return poly;
        return new PolylineShape(DragPointWithNeighborInfluence(poly.Points, index, pos, closed: false));
    }

    private static SmartShapeModel DragClosedPoints(SmartShapeModel shape, int index, Vec2 pos)
    {
        return shape switch
        {
            TriangleShape t when index >= 0 && index < t.Points.Count =>
                new TriangleShape(DragPointWithNeighborInfluence(t.Points, index, pos, closed: true)),
            PolygonShape p when index >= 0 && index < p.Points.Count =>
                new PolygonShape(DragPointWithNeighborInfluence(p.Points, index, pos, closed: true)),
            _ => shape
        };
    }

    internal static Vec2[] DragPointWithNeighborInfluence(
        IReadOnlyList<Vec2> points, int index, Vec2 newPos, bool closed)
    {
        var pts = points.ToArray();
        var old = pts[index];
        pts[index] = newPos;
        var delta = Sub(newPos, old);

        var prev = closed ? (index - 1 + pts.Length) % pts.Length : index - 1;
        var next = closed ? (index + 1) % pts.Length : index + 1;

        if (prev >= 0 && prev < pts.Length && prev != index)
            pts[prev] = Add(pts[prev], Scale(delta, NeighborInfluence));
        if (next >= 0 && next < pts.Length && next != index)
            pts[next] = Add(pts[next], Scale(delta, NeighborInfluence));

        return pts;
    }

    private static SmartShapeModel ReplaceLineEndpoint(SmartShapeModel shape, int index, Vec2 pos)
    {
        if (shape is not LineShape line)
            return shape;
        return index == 0
            ? line with { Start = pos }
            : line with { End = pos };
    }

    private static double RotationAngle(Rect frameRect, Vec2 dragStart, Vec2 current, double startAngleDeg)
    {
        var center = CenterOf(NormalizeRect(frameRect));
        var start = Math.Atan2(dragStart.Y - center.Y, dragStart.X - center.X) * (180.0 / Math.PI);
        var end = Math.Atan2(current.Y - center.Y, current.X - center.X) * (180.0 / Math.PI);
        return startAngleDeg + (end - start);
    }

    private static bool IsCornerHandle(GizmoHandleKind kind)
        => kind is GizmoHandleKind.TopLeft or GizmoHandleKind.TopRight
            or GizmoHandleKind.BottomLeft or GizmoHandleKind.BottomRight;

    private static Rect AxisResizeRect(Rect rect, GizmoHandleKind part, double dx, double dy, bool uniformSides)
    {
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if (part is GizmoHandleKind.TopLeft or GizmoHandleKind.Left or GizmoHandleKind.BottomLeft)
            left += dx;
        if (part is GizmoHandleKind.TopRight or GizmoHandleKind.Right or GizmoHandleKind.BottomRight)
            right += dx;
        if (part is GizmoHandleKind.TopLeft or GizmoHandleKind.Top or GizmoHandleKind.TopRight)
            top += dy;
        if (part is GizmoHandleKind.BottomLeft or GizmoHandleKind.Bottom or GizmoHandleKind.BottomRight)
            bottom += dy;

        if (uniformSides && !IsCornerHandle(part))
        {
            var dw = right - left;
            var dh = bottom - top;
            var scale = part is GizmoHandleKind.Left or GizmoHandleKind.Right
                ? dw / rect.Width
                : dh / rect.Height;
            scale = Math.Max(scale, MinFrameSize / Math.Max(rect.Width, rect.Height));
            var c = CenterOf(rect);
            dw = rect.Width * scale;
            dh = rect.Height * scale;
            left = c.X - dw * 0.5;
            top = c.Y - dh * 0.5;
            right = left + dw;
            bottom = top + dh;
        }

        if (right - left < MinFrameSize || bottom - top < MinFrameSize)
            return rect;

        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect UniformScaleRect(Rect rect, GizmoHandleKind part, double dx, double dy)
    {
        var anchor = AnchorPoint(part, rect);
        var corner = CornerPoint(part, rect);
        var dragged = new Point(corner.X + dx, corner.Y + dy);

        var newW = Math.Abs(dragged.X - anchor.X);
        var newH = Math.Abs(dragged.Y - anchor.Y);
        var scale = Math.Max(newW / Math.Max(rect.Width, 1e-6), newH / Math.Max(rect.Height, 1e-6));
        if (scale < 0.01)
            return rect;

        var w = Math.Max(MinFrameSize, rect.Width * scale);
        var h = Math.Max(MinFrameSize, rect.Height * scale);
        var x = dragged.X < anchor.X ? anchor.X - w : anchor.X;
        var y = dragged.Y < anchor.Y ? anchor.Y - h : anchor.Y;
        return new Rect(x, y, w, h);
    }

    private static Point AnchorPoint(GizmoHandleKind part, Rect rect) => part switch
    {
        GizmoHandleKind.TopLeft => rect.BottomRight,
        GizmoHandleKind.TopRight => rect.BottomLeft,
        GizmoHandleKind.BottomLeft => rect.TopRight,
        GizmoHandleKind.BottomRight => rect.TopLeft,
        GizmoHandleKind.Top => new Point(rect.X + rect.Width * 0.5, rect.Bottom),
        GizmoHandleKind.Bottom => new Point(rect.X + rect.Width * 0.5, rect.Top),
        GizmoHandleKind.Left => new Point(rect.Right, rect.Y + rect.Height * 0.5),
        GizmoHandleKind.Right => new Point(rect.Left, rect.Y + rect.Height * 0.5),
        _ => CenterOf(rect)
    };

    private static Point CornerPoint(GizmoHandleKind part, Rect rect) => part switch
    {
        GizmoHandleKind.TopLeft => rect.TopLeft,
        GizmoHandleKind.TopRight => rect.TopRight,
        GizmoHandleKind.BottomLeft => rect.BottomLeft,
        GizmoHandleKind.BottomRight => rect.BottomRight,
        GizmoHandleKind.Top => new Point(rect.X + rect.Width * 0.5, rect.Top),
        GizmoHandleKind.Bottom => new Point(rect.X + rect.Width * 0.5, rect.Bottom),
        GizmoHandleKind.Left => new Point(rect.Left, rect.Y + rect.Height * 0.5),
        GizmoHandleKind.Right => new Point(rect.Right, rect.Y + rect.Height * 0.5),
        _ => CenterOf(rect)
    };

    private static Rect NormalizeRect(Rect r)
    {
        if (r.Width < 0)
            r = new Rect(r.X + r.Width, r.Y, -r.Width, r.Height);
        if (r.Height < 0)
            r = new Rect(r.X, r.Y + r.Height, r.Width, -r.Height);
        return r;
    }

    private static Point CenterOf(Rect rect)
    {
        var r = NormalizeRect(rect);
        return new Point(r.X + r.Width * 0.5, r.Y + r.Height * 0.5);
    }

    private static Rect CenteredRect(Point p, double size)
        => new(p.X - size * 0.5, p.Y - size * 0.5, size, size);

    private static Point ToPoint(Vec2 v) => new(v.X, v.Y);

    private static IEnumerable<(GizmoHandleKind Kind, Point Point)> TransformHandlePoints(Rect rect)
    {
        var midX = rect.Left + rect.Width * 0.5;
        var midY = rect.Top + rect.Height * 0.5;
        yield return (GizmoHandleKind.TopLeft, rect.TopLeft);
        yield return (GizmoHandleKind.Top, new Point(midX, rect.Top));
        yield return (GizmoHandleKind.TopRight, rect.TopRight);
        yield return (GizmoHandleKind.Right, new Point(rect.Right, midY));
        yield return (GizmoHandleKind.BottomRight, rect.BottomRight);
        yield return (GizmoHandleKind.Bottom, new Point(midX, rect.Bottom));
        yield return (GizmoHandleKind.BottomLeft, rect.BottomLeft);
        yield return (GizmoHandleKind.Left, new Point(rect.Left, midY));
        var rotY = rect.Bottom + Math.Max(rect.Height * 0.25, 12);
        yield return (GizmoHandleKind.Rotate, new Point(midX, rotY));
    }

    private static void AddTransformHandles(List<GizmoHandle> handles, Rect frameRect, double frameAngleDeg)
    {
        var rect = NormalizeRect(frameRect);
        foreach (var (kind, localPt) in TransformHandlePoints(rect))
        {
            if (kind == GizmoHandleKind.Move)
                continue;
            var world = ToFrameWorld(localPt, rect, frameAngleDeg);
            handles.Add(new GizmoHandle(kind, world));
        }
        handles.Add(new GizmoHandle(GizmoHandleKind.Move, ToFrameWorld(CenterOf(rect), rect, frameAngleDeg)));
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

    private static Vec2 Add(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    private static Vec2 Sub(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    private static Vec2 Scale(Vec2 v, double s) => new(v.X * s, v.Y * s);

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
