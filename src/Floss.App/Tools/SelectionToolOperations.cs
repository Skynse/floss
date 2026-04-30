using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;
using SkiaSharp;

namespace Floss.App.Tools;

public abstract class SelectionToolOperation : IToolOperation
{
    protected SelectionToolOperation(ToolContext context, SelectOp op)
    {
        Context = context;
        Op = op;
        SampleCount = 1;
    }

    protected ToolContext Context { get; }
    protected SelectOp Op { get; }

    public int SampleCount { get; protected set; }

    public abstract void Update(CanvasInputSample sample);
    public abstract void Commit(CanvasInputSample sample);

    public virtual void Cancel()
    {
        SampleCount = 0;
        Context.InvalidateRender();
    }

    public abstract void RenderOverlay(DrawingContext dc, double zoom);
}

public sealed class RectSelectionOperation : SelectionToolOperation
{
    private readonly SKPoint _start;
    private SKPoint _current;

    public RectSelectionOperation(ToolContext context, CanvasInputSample firstSample, SelectOp op)
        : base(context, op)
    {
        _start = _current = Pt(firstSample);
    }

    public override void Update(CanvasInputSample sample)
    {
        _current = Pt(sample);
        SampleCount++;
        Context.InvalidateRender();
    }

    public override void Commit(CanvasInputSample sample)
    {
        _current = Pt(sample);
        int x = (int)System.Math.Min(_start.X, _current.X);
        int y = (int)System.Math.Min(_start.Y, _current.Y);
        int w = (int)System.Math.Abs(_current.X - _start.X);
        int h = (int)System.Math.Abs(_current.Y - _start.Y);

        if (w < 2 && h < 2) Context.Selection.Clear();
        else Context.Selection.SetFromRect(x, y, w, h, Op);
        SampleCount = 0;
        Context.InvalidateRender();
    }

    public override void RenderOverlay(DrawingContext dc, double zoom)
    {
        var (penW, penK) = SelectionOverlayHelpers.SelectionPens(zoom);
        var r = MakeRect(_start, _current);
        dc.DrawRectangle(null, penW, r);
        dc.DrawRectangle(null, penK, r);
    }

    private static Avalonia.Rect MakeRect(SKPoint a, SKPoint b) => new(
        System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y),
        System.Math.Abs(b.X - a.X), System.Math.Abs(b.Y - a.Y));

    private static SKPoint Pt(CanvasInputSample s) => new((float)s.X, (float)s.Y);
}

public sealed class LassoSelectionOperation : SelectionToolOperation
{
    private readonly List<SKPoint> _points = [];

    public LassoSelectionOperation(ToolContext context, CanvasInputSample firstSample, SelectOp op)
        : base(context, op)
    {
        _points.Add(Pt(firstSample));
    }

    public override void Update(CanvasInputSample sample)
    {
        _points.Add(Pt(sample));
        SampleCount++;
        Context.InvalidateRender();
    }

    public override void Commit(CanvasInputSample sample)
    {
        _points.Add(Pt(sample));
        if (_points.Count >= 3) Context.Selection.SetFromPolygon(_points, Op);
        else Context.Selection.Clear();
        _points.Clear();
        SampleCount = 0;
        Context.InvalidateRender();
    }

    public override void RenderOverlay(DrawingContext dc, double zoom)
    {
        SelectionOverlayHelpers.DrawPolyOverlay(dc, zoom, _points, close: false);
    }

    private static SKPoint Pt(CanvasInputSample s) => new((float)s.X, (float)s.Y);
}

public sealed class PolylineSelectionOperation : SelectionToolOperation
{
    private readonly List<SKPoint> _points = [];
    private SKPoint _cursor;

    public PolylineSelectionOperation(ToolContext context, CanvasInputSample firstSample, SelectOp op)
        : base(context, op)
    {
        _cursor = Pt(firstSample);
        _points.Add(_cursor);
    }

    public void AddPoint(CanvasInputSample sample)
    {
        _cursor = Pt(sample);
        if (_points.Count > 0 && DistanceSquared(_points[^1], _cursor) < 0.25f)
            return;

        _points.Add(_cursor);
        SampleCount++;
        Context.InvalidateRender();
    }

    public override void Update(CanvasInputSample sample)
    {
        _cursor = Pt(sample);
        Context.InvalidateRender();
    }

    public override void Commit(CanvasInputSample sample)
    {
        _cursor = Pt(sample);
        CommitCurrent();
    }

    public void CommitCurrent()
    {
        if (_points.Count >= 3) Context.Selection.SetFromPolygon(_points, Op);
        else Context.Selection.Clear();
        _points.Clear();
        SampleCount = 0;
        Context.InvalidateRender();
    }

    public override void Cancel()
    {
        _points.Clear();
        base.Cancel();
    }

    public override void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (_points.Count == 0) return;
        var preview = new List<SKPoint>(_points.Count + 1);
        preview.AddRange(_points);
        preview.Add(_cursor);
        SelectionOverlayHelpers.DrawPolyOverlay(dc, zoom, preview, close: false);
    }

    private static SKPoint Pt(CanvasInputSample s) => new((float)s.X, (float)s.Y);
    private static float DistanceSquared(SKPoint a, SKPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}

file static class SelectionOverlayHelpers
{
    public static (Pen White, Pen Black) SelectionPens(double zoom)
    {
        var t = System.Math.Max(0.5, 1.0 / zoom);
        var d1 = new DashStyle([5, 5], 0);
        var d2 = new DashStyle([5, 5], 5);
        return (
            new Pen(Avalonia.Media.Brushes.White, t, d1),
            new Pen(Avalonia.Media.Brushes.Black, t, d2));
    }

    public static void DrawPolyOverlay(DrawingContext dc, double zoom, IReadOnlyList<SKPoint> points, bool close)
    {
        if (points.Count < 1) return;
        var (penW, penK) = SelectionPens(zoom);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(Av(points[0]), false);
            for (int i = 1; i < points.Count; i++) c.LineTo(Av(points[i]));
            c.EndFigure(close);
        }
        dc.DrawGeometry(null, penW, geo);
        dc.DrawGeometry(null, penK, geo);
    }

    private static Avalonia.Point Av(SKPoint p) => new(p.X, p.Y);
}
