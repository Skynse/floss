using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Floss.App.SmartShape;

internal enum SmartShapeOverlayStyle
{
    /// <summary>While holding — thin dark outline (CSP pre-edit).</summary>
    Hold,
    /// <summary>Launcher after release — same as hold.</summary>
    Launcher,
    /// <summary>Edit mode — red shape, blue bbox, handles.</summary>
    Edit
}

internal static class SmartShapeOverlay
{
    private static readonly IBrush HoldOutlineBrush = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20));
    private static readonly IBrush EditOutlineBrush = new SolidColorBrush(Color.FromArgb(230, 220, 50, 50));
    private static readonly IBrush BboxBrush = new SolidColorBrush(Color.FromArgb(160, 100, 140, 255));
    private static readonly IBrush HandleFillBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
    private static readonly IBrush HandleOutlineBrush = new SolidColorBrush(Color.FromArgb(220, 60, 60, 200));
    private static readonly IBrush BboxHandleFillBrush = new SolidColorBrush(Color.FromArgb(230, 90, 140, 255));
    private static readonly IBrush AnchorOutlineBrush = new SolidColorBrush(Color.FromArgb(220, 220, 60, 60));
    private static readonly IBrush RotateStemBrush = new SolidColorBrush(Color.FromArgb(120, 100, 140, 255));
    private static readonly IBrush ControlBrush = new SolidColorBrush(Color.FromArgb(220, 160, 80, 220));
    private static readonly IBrush TangentBrush = new SolidColorBrush(Color.FromArgb(100, 160, 80, 220));

    public static void Draw(
        DrawingContext dc,
        double zoom,
        SmartShapeModel? shape,
        SmartShapeOverlayStyle style,
        IReadOnlyList<GizmoHandle>? gizmoHandles = null)
    {
        if (shape == null)
            return;

        if (style != SmartShapeOverlayStyle.Edit || gizmoHandles == null)
            return;

        DrawBoundingBox(dc, zoom, gizmoHandles);
        DrawGizmoHandles(dc, zoom, shape, gizmoHandles);
    }

    private static void DrawShape(DrawingContext dc, Pen pen, SmartShapeModel shape)
    {
        switch (shape)
        {
            case LineShape l:
                dc.DrawLine(pen, ToPoint(l.Start), ToPoint(l.End));
                break;
            case PolylineShape pl:
                DrawPolygon(dc, pen, pl.Points, close: false);
                break;
            case CircleShape c:
                dc.DrawEllipse(null, pen, ToPoint(c.Center), c.Radius, c.Radius);
                break;
            case EllipseShape e:
                DrawRotatedEllipse(dc, pen, e);
                break;
            case RectangleShape r:
                DrawRotatedRect(dc, pen, r);
                break;
            case TriangleShape t:
            case PolygonShape p:
                DrawPolygon(dc, pen, shape is TriangleShape tt ? tt.Points : ((PolygonShape)shape).Points, close: true);
                break;
            case CurveShape c:
                DrawBezierCurve(dc, pen, c);
                break;
        }
    }

    private static void DrawRotatedEllipse(DrawingContext dc, Pen pen, EllipseShape e)
    {
        var angleRad = e.AngleDeg * Math.PI / 180.0;
        var matrix = Matrix.CreateTranslation(e.Center.X, e.Center.Y)
            * Matrix.CreateRotation(angleRad);
        using (dc.PushTransform(matrix))
            dc.DrawEllipse(null, pen, new Point(0, 0), e.Rx, e.Ry);
    }

    private static void DrawRotatedRect(DrawingContext dc, Pen pen, RectangleShape r)
    {
        var angleRad = r.AngleDeg * Math.PI / 180.0;
        var matrix = Matrix.CreateTranslation(r.Center.X, r.Center.Y)
            * Matrix.CreateRotation(angleRad);
        using (dc.PushTransform(matrix))
            dc.DrawRectangle(null, pen, new Rect(-r.Width * 0.5, -r.Height * 0.5, r.Width, r.Height));
    }

    private static void DrawBezierCurve(DrawingContext dc, Pen pen, CurveShape curve)
    {
        if (curve.Curves.Count == 0)
            return;

        var geom = new PathGeometry();
        var fig = new PathFigure
        {
            StartPoint = ToPoint(curve.Curves[0].P0),
            IsClosed = false
        };

        foreach (var seg in curve.Curves)
        {
            fig.Segments!.Add(new BezierSegment
            {
                Point1 = ToPoint(seg.P1),
                Point2 = ToPoint(seg.P2),
                Point3 = ToPoint(seg.P3)
            });
        }

        geom.Figures!.Add(fig);
        dc.DrawGeometry(null, pen, geom);
    }

    private static void DrawPolygon(DrawingContext dc, Pen pen, IReadOnlyList<Vec2> points, bool close)
    {
        if (points.Count < 2)
            return;
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(ToPoint(points[0]), false);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(ToPoint(points[i]));
            if (close)
                ctx.LineTo(ToPoint(points[0]));
        }
        dc.DrawGeometry(null, pen, geom);
    }

    private static void DrawBoundingBox(DrawingContext dc, double zoom, IReadOnlyList<GizmoHandle> handles)
    {
        Vec2? tl = null, tr = null, br = null, bl = null, tc = null, rot = null;
        foreach (var h in handles)
        {
            switch (h.Kind)
            {
                case GizmoHandleKind.TopLeft: tl = h.Position; break;
                case GizmoHandleKind.TopRight: tr = h.Position; break;
                case GizmoHandleKind.BottomRight: br = h.Position; break;
                case GizmoHandleKind.BottomLeft: bl = h.Position; break;
                case GizmoHandleKind.Top: tc = h.Position; break;
                case GizmoHandleKind.Rotate: rot = h.Position; break;
            }
        }

        if (tl is { } a && tr is { } b && br is { } c && bl is { } d)
        {
            var penW = Math.Max(0.5, 1.0 / zoom);
            var bboxPen = new Pen(BboxBrush, penW)
            {
                DashStyle = DashStyle.Dash
            };
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(ToPoint(a), false);
                ctx.LineTo(ToPoint(b));
                ctx.LineTo(ToPoint(c));
                ctx.LineTo(ToPoint(d));
                ctx.LineTo(ToPoint(a));
            }
            dc.DrawGeometry(null, bboxPen, geom);
        }

        if (tc is { } top && rot is { } r)
        {
            var penW = Math.Max(0.5, 1.0 / zoom);
            dc.DrawLine(new Pen(RotateStemBrush, penW), ToPoint(top), ToPoint(r));
        }
    }

    private static void DrawGizmoHandles(
        DrawingContext dc,
        double zoom,
        SmartShapeModel shape,
        IReadOnlyList<GizmoHandle> handles)
    {
        var penW = Math.Max(0.5, 1.0 / zoom);
        var outlinePen = new Pen(HandleOutlineBrush, penW * 1.5);

        if (shape is CurveShape curve)
            DrawCurveGizmoHandles(dc, zoom, curve, handles, penW, outlinePen);

        var handleR = Math.Max(5.0, 6.0 / zoom);
        var isCurve = shape is CurveShape;
        foreach (var h in handles)
        {
            var pos = ToPoint(h.Position);
            switch (h.Kind)
            {
                case GizmoHandleKind.Rotate:
                    dc.DrawEllipse(HandleFillBrush, outlinePen, pos, handleR, handleR);
                    break;
                case GizmoHandleKind.TopLeft or GizmoHandleKind.TopRight
                    or GizmoHandleKind.BottomLeft or GizmoHandleKind.BottomRight:
                    if (isCurve)
                        dc.DrawEllipse(BboxHandleFillBrush, outlinePen, pos, handleR, handleR);
                    else
                    {
                        var s = handleR + 1;
                        dc.DrawRectangle(HandleFillBrush, outlinePen, new Rect(pos.X - s, pos.Y - s, s * 2, s * 2));
                    }
                    break;
                case GizmoHandleKind.Top or GizmoHandleKind.Bottom
                    or GizmoHandleKind.Left or GizmoHandleKind.Right:
                    var edge = handleR - 0.5;
                    dc.DrawRectangle(HandleFillBrush, outlinePen, new Rect(pos.X - edge, pos.Y - edge, edge * 2, edge * 2));
                    break;
                case GizmoHandleKind.CurveControl:
                    var cs = handleR * 0.6;
                    dc.DrawRectangle(HandleFillBrush, new Pen(ControlBrush, penW * 1.5),
                        new Rect(pos.X - cs, pos.Y - cs, cs * 2, cs * 2));
                    break;
                case GizmoHandleKind.CurveAnchor:
                    dc.DrawEllipse(HandleFillBrush, new Pen(AnchorOutlineBrush, penW * 1.5), pos, handleR * 0.85, handleR * 0.85);
                    break;
            }
        }
    }

    private static void DrawCurveGizmoHandles(
        DrawingContext dc,
        double zoom,
        CurveShape curve,
        IReadOnlyList<GizmoHandle> handles,
        double penW,
        Pen outlinePen)
    {
        var anchorByIndex = new Dictionary<int, Vec2>();
        var controlByKey = new Dictionary<(int Seg, int Cp), Vec2>();
        foreach (var h in handles)
        {
            if (h.Kind == GizmoHandleKind.CurveAnchor)
                anchorByIndex[h.Index] = h.Position;
            else if (h.Kind == GizmoHandleKind.CurveControl)
                controlByKey[(h.Index / 4, h.Index % 4)] = h.Position;
        }

        var tangentPen = new Pen(TangentBrush, penW);
        for (var i = 0; i < curve.Curves.Count; i++)
        {
            var seg = curve.Curves[i];
            if (controlByKey.TryGetValue((i, 1), out var c1))
                dc.DrawLine(tangentPen, ToPoint(seg.P0), ToPoint(c1));
            if (controlByKey.TryGetValue((i, 2), out var c2))
                dc.DrawLine(tangentPen, ToPoint(seg.P3), ToPoint(c2));
        }
    }

    private static Point ToPoint(Vec2 v) => new(v.X, v.Y);
}
