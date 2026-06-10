using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Floss.App.SmartShape;

internal enum SmartShapeOverlayStyle
{
    /// <summary>While holding — thin dark outline (pre-edit).</summary>
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

    public static void Draw(
        DrawingContext dc,
        double zoom,
        SmartShapeModel? shape,
        SmartShapeOverlayStyle style,
        IReadOnlyList<GizmoHandle>? gizmoHandles = null,
        Rect frameRect = default,
        double frameAngleDeg = 0)
    {
        if (shape == null)
            return;

        if (style != SmartShapeOverlayStyle.Edit || gizmoHandles == null)
            return;

        DrawBoundingBox(dc, zoom, frameRect, frameAngleDeg);
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

    private static void DrawBoundingBox(DrawingContext dc, double zoom, Rect frameRect, double frameAngleDeg)
    {
        if (frameRect.Width <= 0 || frameRect.Height <= 0)
            return;

        var rect = new Rect(frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height);
        var center = new Point(rect.X + rect.Width * 0.5, rect.Y + rect.Height * 0.5);
        var angleRad = frameAngleDeg * Math.PI / 180.0;
        var matrix = Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(angleRad)
            * Matrix.CreateTranslation(center.X, center.Y);

        var penW = Math.Max(0.75, 1.0 / zoom);
        var bboxPen = new Pen(BboxBrush, penW) { DashStyle = DashStyle.Dash };
        var rotateStemPen = new Pen(RotateStemBrush, penW);

        using (dc.PushTransform(matrix))
        {
            dc.DrawRectangle(null, bboxPen, rect);

            var midX = rect.X + rect.Width * 0.5;
            var rotY = rect.Bottom + Math.Max(rect.Height * 0.25, 12.0 / zoom);
            dc.DrawLine(rotateStemPen, new Point(midX, rect.Top), new Point(midX, rotY));
        }
    }

    private static void DrawGizmoHandles(
        DrawingContext dc,
        double zoom,
        SmartShapeModel shape,
        IReadOnlyList<GizmoHandle> handles)
    {
        var penW = Math.Max(0.75, 1.0 / zoom);
        var outlinePen = new Pen(HandleOutlineBrush, penW * 1.5);
        var handleSize = SmartShapeGizmo.HandleSize(zoom);
        var half = handleSize * 0.5;
        foreach (var h in handles)
        {
            var pos = ToPoint(h.Position);
            switch (h.Kind)
            {
                case GizmoHandleKind.Rotate:
                    dc.DrawEllipse(HandleFillBrush, outlinePen, pos, half, half);
                    break;
                case GizmoHandleKind.TopLeft or GizmoHandleKind.TopRight
                    or GizmoHandleKind.BottomLeft or GizmoHandleKind.BottomRight:
                    var isCurve = shape is CurveShape;
                    if (isCurve)
                        dc.DrawEllipse(BboxHandleFillBrush, outlinePen, pos, half, half);
                    else
                        dc.DrawRectangle(HandleFillBrush, outlinePen, new Rect(pos.X - half, pos.Y - half, handleSize, handleSize));
                    break;
                case GizmoHandleKind.Top or GizmoHandleKind.Bottom
                    or GizmoHandleKind.Left or GizmoHandleKind.Right:
                    dc.DrawRectangle(HandleFillBrush, outlinePen, new Rect(pos.X - half, pos.Y - half, handleSize, handleSize));
                    break;
                case GizmoHandleKind.CurveAnchor:
                    dc.DrawEllipse(HandleFillBrush, new Pen(AnchorOutlineBrush, penW * 1.5), pos, half * 0.85, half * 0.85);
                    break;
            }
        }
    }

    private static Point ToPoint(Vec2 v) => new(v.X, v.Y);
}
