using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Floss.App.Canvas;

internal sealed class CheckerboardOverlay : Control
{
    internal DrawingCanvas Canvas { get; set; }
    private DrawingCanvas _canvas => Canvas;
    private static readonly IBrush CheckerBrush = new DrawingBrush
    {
        TileMode = TileMode.Tile,
        DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#555555")), Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#555555")), Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#aaaaaa")), Geometry = new RectangleGeometry(new Rect(8, 0, 8, 8)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#aaaaaa")), Geometry = new RectangleGeometry(new Rect(0, 8, 8, 8)) },
            }
        }
    };

    internal static readonly ISolidColorBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#2a2a2e"));

    public CheckerboardOverlay(DrawingCanvas canvas)
    {
        Canvas = canvas;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var vpw = Bounds.Width;
        var vph = Bounds.Height;
        var docW = _canvas.Document.Width;
        var docH = _canvas.Document.Height;
        var zoom = _canvas.CanvasZoom;
        var flipX = _canvas.FlipX;
        var flipY = _canvas.FlipY;
        var px = _canvas.PanOffsetX;
        var py = _canvas.PanOffsetY;
        var angle = _canvas.CanvasRotation * Math.PI / 180.0;

        ctx.FillRectangle(BackgroundBrush, new Rect(0, 0, vpw, vph));

        var sw = docW * zoom;
        var sh = docH * zoom;

        // Canvas rect in viewport space (before rotation), centered.
        // When flipped, the canvas origin (0,0) is at the far edge, so shift
        // the anchor so the checkerboard stays aligned with the flipped canvas.
        var ox = flipX == 1 ? (vpw - sw) * 0.5 : (vpw + sw) * 0.5;
        var oy = flipY == 1 ? (vph - sh) * 0.5 : (vph + sh) * 0.5;

        // Transform corners: (0,0), (docW,0), (docW,docH), (0,docH)
        static Point T(double x, double y, double ox, double oy,
            double zoom, double px, double py, double vpw, double vph,
            double flipX, double flipY, double cos, double sin)
        {
            // pre-rotation canvas offset (unscaled)
            var cx = ox + x * zoom * flipX;
            var cy = oy + y * zoom * flipY;
            // rotate around viewport center
            var rx = vpw * 0.5 + (cx - vpw * 0.5) * cos - (cy - vph * 0.5) * sin;
            var ry = vph * 0.5 + (cx - vpw * 0.5) * sin + (cy - vph * 0.5) * cos;
            // pan (screen-space, after rotation)
            return new Point(rx + px, ry + py);
        }

        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var p0 = T(0, 0, ox, oy, zoom, px, py, vpw, vph, flipX, flipY, cos, sin);
        var p1 = T(docW, 0, ox, oy, zoom, px, py, vpw, vph, flipX, flipY, cos, sin);
        var p2 = T(docW, docH, ox, oy, zoom, px, py, vpw, vph, flipX, flipY, cos, sin);
        var p3 = T(0, docH, ox, oy, zoom, px, py, vpw, vph, flipX, flipY, cos, sin);

        var path = new PathGeometry();
        var figure = new PathFigure { StartPoint = p0, IsClosed = true };
        figure.Segments!.Add(new LineSegment { Point = p1 });
        figure.Segments.Add(new LineSegment { Point = p2 });
        figure.Segments.Add(new LineSegment { Point = p3 });
        path.Figures!.Add(figure);

        ctx.DrawGeometry(CheckerBrush, null, path);
    }
}
