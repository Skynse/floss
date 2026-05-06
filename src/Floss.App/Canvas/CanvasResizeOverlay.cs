using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Floss.App.Canvas;

// Pure rendering overlay for the interactive canvas resize mode.
// Pointer events are handled by the parent window's workspace viewport handlers,
// which call BeginDrag / UpdateDrag / EndDrag directly.
public sealed class CanvasResizeOverlay : Control
{
    private readonly DrawingCanvas _canvas;

    public int PreviewW   { get; private set; }
    public int PreviewH   { get; private set; }
    public int PreviewOffX { get; private set; }
    public int PreviewOffY { get; private set; }

    // Drag state — managed externally by the workspace pointer handlers
    public int  DragHandle    { get; private set; } = -1;
    public Point DragStartVp  { get; private set; }
    public int  DragStartW    { get; private set; }
    public int  DragStartH    { get; private set; }
    public int  DragStartOffX { get; private set; }
    public int  DragStartOffY { get; private set; }

    public event Action<int, int, int, int>? PreviewChanged;

    private const double HitR  = 10.0;
    private const double HSize =  7.0;

    private static readonly IBrush HandleFill   = new SolidColorBrush(Colors.White);
    private static readonly IBrush HandleBorder = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30));

    public CanvasResizeOverlay(DrawingCanvas canvas)
    {
        _canvas = canvas;
        IsHitTestVisible = false;   // events handled by workspace viewport
        ClipToBounds = false;
    }

    public void SetPreview(int w, int h, int offX, int offY)
    {
        PreviewW    = Math.Max(1, w);
        PreviewH    = Math.Max(1, h);
        PreviewOffX = offX;
        PreviewOffY = offY;
        InvalidateVisual();
    }

    // ── Drag helpers called by workspace handlers ─────────────────────────────

    public int HitHandle(Point vp)
    {
        var handles = HandlePoints(CornerPoints());
        for (int i = 0; i < handles.Length; i++)
        {
            var dx = vp.X - handles[i].X;
            var dy = vp.Y - handles[i].Y;
            if (dx * dx + dy * dy <= HitR * HitR) return i;
        }
        return -1;
    }

    public void BeginDrag(int handle, Point vpPos)
    {
        DragHandle    = handle;
        DragStartVp   = vpPos;
        DragStartW    = PreviewW;
        DragStartH    = PreviewH;
        DragStartOffX = PreviewOffX;
        DragStartOffY = PreviewOffY;
    }

    public void UpdateDrag(Point vpPos)
    {
        if (DragHandle < 0) return;
        var dvx = vpPos.X - DragStartVp.X;
        var dvy = vpPos.Y - DragStartVp.Y;
        var (dx, dy) = VpDeltaToDoc(dvx, dvy);

        ApplyHandleDrag(DragHandle, dx, dy,
            DragStartW, DragStartH, DragStartOffX, DragStartOffY,
            out var nw, out var nh, out var nox, out var noy);

        if (nw < 1) nw = 1;
        if (nh < 1) nh = 1;

        SetPreview(nw, nh, nox, noy);
        PreviewChanged?.Invoke(nw, nh, nox, noy);
    }

    public void EndDrag() => DragHandle = -1;

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        if (PreviewW == 0) return;

        var pts     = CornerPoints();
        var handles = HandlePoints(pts);

        var dash1 = new DashStyle([5, 5], 0);
        var dash2 = new DashStyle([5, 5], 5);
        var path = BuildQuadPath(pts[0], pts[1], pts[2], pts[3]);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 2, dash1), path);
        ctx.DrawGeometry(null, new Pen(HandleFill, 1, dash2), path);

        foreach (var h in handles)
            ctx.DrawRectangle(HandleFill, new Pen(HandleBorder, 1),
                new Rect(h.X - HSize, h.Y - HSize, HSize * 2, HSize * 2));
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    public (double dx, double dy) VpDeltaToDoc(double dvx, double dvy)
    {
        var zoom   = _canvas.CanvasZoom;
        var flipX  = (double)_canvas.FlipX;
        var flipY  = (double)_canvas.FlipY;
        var angle  = _canvas.CanvasRotation * Math.PI / 180.0;
        var cos    = Math.Cos(angle);
        var sin    = Math.Sin(angle);
        var unrotX =  dvx * cos + dvy * sin;
        var unrotY = -dvx * sin + dvy * cos;
        return (unrotX / zoom * flipX, unrotY / zoom * flipY);
    }

    private Point DocToVP(double docX, double docY)
    {
        var vpw   = Bounds.Width;
        var vph   = Bounds.Height;
        var docW  = _canvas.Document.Width;
        var docH  = _canvas.Document.Height;
        var zoom  = _canvas.CanvasZoom;
        var flipX = (double)_canvas.FlipX;
        var flipY = (double)_canvas.FlipY;
        var px    = _canvas.PanOffsetX;
        var py    = _canvas.PanOffsetY;
        var angle = _canvas.CanvasRotation * Math.PI / 180.0;
        var sw    = docW * zoom;
        var sh    = docH * zoom;
        var ox    = flipX == 1 ? (vpw - sw) * 0.5 : (vpw + sw) * 0.5;
        var oy    = flipY == 1 ? (vph - sh) * 0.5 : (vph + sh) * 0.5;
        var cos   = Math.Cos(angle);
        var sin   = Math.Sin(angle);
        var cx    = ox + docX * zoom * flipX;
        var cy    = oy + docY * zoom * flipY;
        var rx    = vpw * 0.5 + (cx - vpw * 0.5) * cos - (cy - vph * 0.5) * sin;
        var ry    = vph * 0.5 + (cx - vpw * 0.5) * sin + (cy - vph * 0.5) * cos;
        return new Point(rx + px, ry + py);
    }

    private Point[] CornerPoints()
    {
        double l = -PreviewOffX, t = -PreviewOffY;
        double r = l + PreviewW,  b = t + PreviewH;
        return [DocToVP(l, t), DocToVP(r, t), DocToVP(r, b), DocToVP(l, b)];
    }

    private Point[] HandlePoints(Point[] p) =>
    [
        p[0],
        new((p[0].X + p[1].X) * 0.5, (p[0].Y + p[1].Y) * 0.5),
        p[1],
        new((p[0].X + p[3].X) * 0.5, (p[0].Y + p[3].Y) * 0.5),
        new((p[1].X + p[2].X) * 0.5, (p[1].Y + p[2].Y) * 0.5),
        p[3],
        new((p[3].X + p[2].X) * 0.5, (p[3].Y + p[2].Y) * 0.5),
        p[2],
    ];

    private static PathGeometry BuildQuadPath(Point a, Point b, Point c, Point d)
    {
        var path = new PathGeometry();
        var fig  = new PathFigure { StartPoint = a, IsClosed = true };
        fig.Segments!.Add(new LineSegment { Point = b });
        fig.Segments.Add(new LineSegment  { Point = c });
        fig.Segments.Add(new LineSegment  { Point = d });
        path.Figures!.Add(fig);
        return path;
    }

    public static void ApplyHandleDrag(int handle,
        double dx, double dy,
        int startW, int startH, int startOffX, int startOffY,
        out int newW, out int newH, out int newOffX, out int newOffY)
    {
        newW    = startW;
        newH    = startH;
        newOffX = startOffX;
        newOffY = startOffY;

        var edges = new (bool left, bool right, bool top, bool bottom)[]
        {
            (true,  false, true,  false), // 0 TL
            (false, false, true,  false), // 1 TC
            (false, true,  true,  false), // 2 TR
            (true,  false, false, false), // 3 ML
            (false, true,  false, false), // 4 MR
            (true,  false, false, true),  // 5 BL
            (false, false, false, true),  // 6 BC
            (false, true,  false, true),  // 7 BR
        };

        var (left, right, top, bottom) = edges[handle];
        if (left)   { newW = startW - (int)Math.Round(dx); newOffX = startOffX - (int)Math.Round(dx); }
        if (right)  { newW = startW + (int)Math.Round(dx); }
        if (top)    { newH = startH - (int)Math.Round(dy); newOffY = startOffY - (int)Math.Round(dy); }
        if (bottom) { newH = startH + (int)Math.Round(dy); }
    }
}
