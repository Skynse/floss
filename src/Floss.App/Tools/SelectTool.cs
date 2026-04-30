using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public enum SelectMode { Rect, Lasso, PolylineLasso }

// Handles rectangular, freehand lasso, and click-to-add polyline lasso selection.
// The "lasso fill" workflow: draw a lasso, switch to FillTool → fill respects the selection.
public sealed class SelectTool : ITool
{
    public SelectMode Mode  { get; set; } = SelectMode.Rect;
    public SelectOp   Op    { get; set; } = SelectOp.Replace;

    private bool _dragging;
    private SKPoint _startPt, _curPt;
    private readonly List<SKPoint> _poly = [];
    // PolylineLasso is accumulative until double-click or Enter commits it.
    private bool _polyPending;

    public void Activate(ToolContext ctx)
    {
        ctx.Selection.Resize(ctx.Document.Width, ctx.Document.Height);
    }

    public void Deactivate(ToolContext ctx)
    {
        Reset();
    }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        var p = Pt(s);
        switch (Mode)
        {
            case SelectMode.Rect:
                _dragging = true;
                _startPt = _curPt = p;
                break;

            case SelectMode.Lasso:
                _dragging = true;
                _poly.Clear();
                _poly.Add(p);
                break;

            case SelectMode.PolylineLasso:
                if (!_polyPending)
                {
                    _polyPending = true;
                    _poly.Clear();
                    _poly.Add(p);
                }
                else
                {
                    _poly.Add(p);
                }
                break;
        }
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        _curPt = Pt(s);
        if (Mode == SelectMode.Lasso && _dragging) _poly.Add(_curPt);
        ctx.InvalidateRender();
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        _curPt = Pt(s);

        if (Mode == SelectMode.Rect && _dragging)
        {
            _dragging = false;
            CommitRect(ctx);
        }
        else if (Mode == SelectMode.Lasso && _dragging)
        {
            _dragging = false;
            _poly.Add(_curPt);
            CommitPolygon(ctx);
            _poly.Clear();
        }
        // PolylineLasso: accumulate via clicks; committed by CommitPolyline()
    }

    // Called from MainWindow on double-click or Enter key while PolylineLasso is pending.
    public void CommitPolyline(ToolContext ctx)
    {
        if (!_polyPending || _poly.Count < 3) { Reset(); return; }
        CommitPolygon(ctx);
        _poly.Clear();
        _polyPending = false;
        ctx.InvalidateRender();
    }

    public void Cancel(ToolContext ctx)
    {
        Reset();
        ctx.InvalidateRender();
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        var t    = System.Math.Max(0.5, 1.0 / zoom);
        var d1   = new DashStyle([5, 5], 0);
        var d2   = new DashStyle([5, 5], 5);
        var penW = new Pen(Avalonia.Media.Brushes.White, t, d1);
        var penK = new Pen(Avalonia.Media.Brushes.Black, t, d2);

        if (Mode == SelectMode.Rect && _dragging)
        {
            var r = MakeRect(_startPt, _curPt);
            dc.DrawRectangle(null, penW, r);
            dc.DrawRectangle(null, penK, r);
        }
        else if ((Mode == SelectMode.Lasso && _dragging) ||
                 (Mode == SelectMode.PolylineLasso && _polyPending))
        {
            RenderPolyOverlay(dc, penW, penK, Mode == SelectMode.PolylineLasso && !_dragging);
        }

        // Also render the committed selection.
        ctx.Selection.RenderOverlay(dc, zoom);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void CommitRect(ToolContext ctx)
    {
        int x = (int)System.Math.Min(_startPt.X, _curPt.X);
        int y = (int)System.Math.Min(_startPt.Y, _curPt.Y);
        int w = (int)System.Math.Abs(_curPt.X - _startPt.X);
        int h = (int)System.Math.Abs(_curPt.Y - _startPt.Y);

        if (w < 2 && h < 2) { ctx.Selection.Clear(); }
        else { ctx.Selection.SetFromRect(x, y, w, h, Op); }
        ctx.InvalidateRender();
    }

    private void CommitPolygon(ToolContext ctx)
    {
        if (_poly.Count >= 3) ctx.Selection.SetFromPolygon(_poly, Op);
        else                  ctx.Selection.Clear();
        ctx.InvalidateRender();
    }

    private void RenderPolyOverlay(DrawingContext dc, Pen penW, Pen penK, bool drawToCursor)
    {
        if (_poly.Count < 1) return;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(Av(_poly[0]), false);
            for (int i = 1; i < _poly.Count; i++) c.LineTo(Av(_poly[i]));
            if (drawToCursor) c.LineTo(Av(_curPt));
            c.EndFigure(!drawToCursor);
        }
        dc.DrawGeometry(null, penW, geo);
        dc.DrawGeometry(null, penK, geo);
    }

    private static Avalonia.Rect MakeRect(SKPoint a, SKPoint b) => new(
        System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y),
        System.Math.Abs(b.X - a.X), System.Math.Abs(b.Y - a.Y));

    private static SKPoint Pt(CanvasInputSample s) => new((float)s.X, (float)s.Y);
    private static Avalonia.Point Av(SKPoint p) => new(p.X, p.Y);
    private void Reset() { _dragging = false; _polyPending = false; _poly.Clear(); }
}
