using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Floss.App;

public sealed record CurveChangedArgs(float[] CurvePoints);

public sealed class CurveGraph : Control
{
    private readonly List<(float X, float Y)> _points = [(0f, 0f), (1f, 1f)];
    private const double PointRadius = 6;
    private const double HitRadius  = 10;
    private int _dragIndex = -1;
    private Point _dragOrigin;
    private (float X, float Y) _dragPoint;

    private static readonly IBrush BgBrush      = new SolidColorBrush(Color.Parse("#090b0f"));
    private static readonly IBrush GridBrush    = new SolidColorBrush(Color.Parse("#181c28"));
    private static readonly IBrush RefBrush     = new SolidColorBrush(Color.Parse("#222840"));
    private static readonly IBrush CurveBrush   = new SolidColorBrush(Color.Parse("#4c7ed8"));
    private static readonly IBrush PtBrush      = new SolidColorBrush(Color.Parse("#80aaee"));
    private static readonly IBrush PtActiveBrush = new SolidColorBrush(Color.Parse("#b0d0ff"));
    private static readonly IBrush PtOutline    = new SolidColorBrush(Color.Parse("#1a1c22"));
    private static readonly IBrush LabelBrush   = new SolidColorBrush(Color.Parse("#44506a"));

    public event EventHandler<CurveChangedArgs>? CurveChanged;

    public float[] CurvePoints
    {
        get => _points.SelectMany(p => new[] { p.X, p.Y }).ToArray();
        set
        {
            _points.Clear();
            if (value != null && value.Length >= 4 && value.Length % 2 == 0)
                for (var i = 0; i < value.Length; i += 2)
                    _points.Add((Math.Clamp(value[i], 0, 1), Math.Clamp(value[i + 1], 0, 1)));
            else
            {
                _points.Add((0f, 0f));
                _points.Add((1f, 1f));
            }
            InvalidateVisual();
        }
    }

    public byte[] ComputeLut()
    {
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp((int)(Eval(i / 255f) * 255f + 0.5f), 0, 255);
        return lut;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;

        ctx.FillRectangle(BgBrush, new Rect(0, 0, w, h));
        RenderGrid(ctx, w, h);
        RenderCurve(ctx, w, h);
        RenderPoints(ctx, w, h);
        RenderLabel(ctx, w, h);
    }

    private static void RenderGrid(DrawingContext ctx, double w, double h)
    {
        var gp = new Pen(GridBrush, 1);
        for (var i = 1; i < 4; i++)
        {
            ctx.DrawLine(gp, new Point(w * i / 4.0, 0),        new Point(w * i / 4.0, h));
            ctx.DrawLine(gp, new Point(0,  h * i / 4.0),       new Point(w, h * i / 4.0));
        }
        ctx.DrawLine(new Pen(RefBrush, 1), new Point(0, h), new Point(w, 0));
    }

    private void RenderCurve(DrawingContext ctx, double w, double h)
    {
        const int Steps = 100;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(CanvasPt(0, Eval(0), w, h), isFilled: false);
            for (var i = 1; i <= Steps; i++)
                gc.LineTo(CanvasPt(i / (float)Steps, Eval(i / (float)Steps), w, h));
        }
        ctx.DrawGeometry(null, new Pen(CurveBrush, 2), geo);
    }

    private void RenderPoints(DrawingContext ctx, double w, double h)
    {
        foreach (var (pt, i) in _points.Select((p, i) => (p, i)))
        {
            var cp = CanvasPt(pt.X, pt.Y, w, h);
            ctx.DrawEllipse(i == _dragIndex ? PtActiveBrush : PtBrush, null, cp, PointRadius, PointRadius);
            ctx.DrawEllipse(null, new Pen(PtOutline, 1), cp, PointRadius, PointRadius);
        }
    }

    private void RenderLabel(DrawingContext ctx, double w, double h)
    {
        if (_dragIndex >= 0 && _dragIndex < _points.Count)
        {
            var pt = _points[_dragIndex];
            var text = $"({pt.X:0.00}, {pt.Y:0.00})";
            var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 8.5, LabelBrush);
            ctx.DrawText(ft, new Point(4, 3));
        }
    }

    // Catmull-Rom spline interpolation through control points
    private float Eval(float x)
    {
        if (_points.Count == 0) return x;
        if (x <= _points[0].X) return _points[0].Y;
        if (x >= _points[^1].X) return _points[^1].Y;

        for (var i = 0; i < _points.Count - 1; i++)
        {
            if (x >= _points[i].X && x <= _points[i + 1].X)
            {
                var t = (x - _points[i].X) / (_points[i + 1].X - _points[i].X);
                // Smooth hermite interpolation (cubic)
                var h00 = 2 * t * t * t - 3 * t * t + 1;
                var h10 = t * t * t - 2 * t * t + t;
                var h01 = -2 * t * t * t + 3 * t * t;
                var h11 = t * t * t - t * t;

                var m0 = Tangent(i);
                var m1 = Tangent(i + 1);
                var dx = _points[i + 1].X - _points[i].X;

                return h00 * _points[i].Y + h10 * dx * m0 + h01 * _points[i + 1].Y + h11 * dx * m1;
            }
        }
        return x;
    }

    private float Tangent(int i)
    {
        if (i <= 0) return (_points[1].Y - _points[0].Y) / (_points[1].X - _points[0].X);
        if (i >= _points.Count - 1)
            return (_points[^1].Y - _points[^2].Y) / (_points[^1].X - _points[^2].X);
        return (_points[i + 1].Y - _points[i - 1].Y) / (_points[i + 1].X - _points[i - 1].X);
    }

    private static Point CanvasPt(float x, float y, double w, double h)
        => new(x * w, (1 - y) * h);

    private void ToNormal(Point canvas, double w, double h, out float nx, out float ny)
    {
        nx = (float)Math.Clamp(canvas.X / w, 0, 1);
        ny = (float)Math.Clamp(1 - canvas.Y / h, 0, 1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        var w = Bounds.Width;
        var h = Bounds.Height;

        // Hit-test existing points
        for (var i = 0; i < _points.Count; i++)
        {
            var cp = CanvasPt(_points[i].X, _points[i].Y, w, h);
            if (Dist(pos, cp) <= HitRadius)
            {
                _dragIndex = i;
                _dragOrigin = pos;
                _dragPoint = _points[i];
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        // Click on empty space near the curve → insert a new point
        ToNormal(pos, w, h, out var nx, out var ny);
        var curveY = Eval(nx);
        if (Math.Abs(curveY - ny) < 0.12f)
        {
            var insertIdx = 0;
            while (insertIdx < _points.Count && _points[insertIdx].X < nx)
                insertIdx++;
            if (insertIdx < _points.Count && Math.Abs(_points[insertIdx].X - nx) < 0.02f) return;
            _points.Insert(insertIdx, (nx, Math.Clamp(curveY, 0, 1)));
            _dragIndex = insertIdx;
            _dragOrigin = pos;
            _dragPoint = _points[insertIdx];
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            Emit();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragIndex < 0) return;

        var pos = e.GetPosition(this);
        var w = Bounds.Width;
        var h = Bounds.Height;
        ToNormal(pos, w, h, out var nx, out var ny);

        _points[_dragIndex] = (Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
        InvalidateVisual();
        Emit();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex < 0) return;

        var pos = e.GetPosition(this);
        var w = Bounds.Width;
        var h = Bounds.Height;

        // If released outside editor bounds → delete the point
        if (_points.Count > 2 &&
            _dragIndex > 0 && _dragIndex < _points.Count - 1 &&
            (pos.X < -20 || pos.X > w + 20 || pos.Y < -20 || pos.Y > h + 20))
        {
            _points.RemoveAt(_dragIndex);
        }

        _dragIndex = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
        Emit();
        e.Handled = true;
    }

    private static double Dist(Point a, Point b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void Emit()
        => CurveChanged?.Invoke(this, new CurveChangedArgs(CurvePoints));
}
