using System;
using Avalonia;
using Avalonia.Media;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Input;

// Captures drag-to-define rectangle.
public sealed class RectInputProcess : IInputProcess
{
    private CanvasInputSample _start;
    private CanvasInputSample _current;
    private bool _active;

    public bool IsActive => _active;
    public double Stabilization { get; set; }  // Not used
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;

    public void PointerDown(CanvasInputSample s)
    {
        _start = s;
        _current = s;
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        if (!_active) return;
        _current = s;
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;
        _current = s;
        _active = false;
    }

    public void Cancel()
    {
        _active = false;
    }

    public IProcessedInput? GetResult()
    {
        if (!_active)
        {
            return new RectInput { Start = _start, End = _current };
        }
        return null;
    }

    public IProcessedInput? GetPreview()
    {
        if (_active)
        {
            return new RectInput { Start = _start, End = _current };
        }
        return null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!_active) return;
        var t = Math.Max(0.5, 1.0 / zoom);
        var d = 4.0 / zoom;
        var penW = new Pen(Avalonia.Media.Brushes.White, t, new DashStyle([d, d], 0));
        var penK = new Pen(Avalonia.Media.Brushes.Black, t, new DashStyle([d, d], d));

        if (ShapeKind == ShapeKind.Line)
        {
            var p0 = new Point(_start.X, _start.Y);
            var p1 = new Point(_current.X, _current.Y);
            dc.DrawLine(penW, p0, p1);
            dc.DrawLine(penK, p0, p1);
        }
        else if (ShapeKind == ShapeKind.Ellipse)
        {
            var cx = (_start.X + _current.X) * 0.5;
            var cy = (_start.Y + _current.Y) * 0.5;
            var rx = Math.Abs(_current.X - _start.X) * 0.5;
            var ry = Math.Abs(_current.Y - _start.Y) * 0.5;
            dc.DrawEllipse(null, penW, new Point(cx, cy), rx, ry);
            dc.DrawEllipse(null, penK, new Point(cx, cy), rx, ry);
        }
        else
        {
            var x = Math.Min(_start.X, _current.X);
            var y = Math.Min(_start.Y, _current.Y);
            var w = Math.Abs(_current.X - _start.X);
            var h = Math.Abs(_current.Y - _start.Y);
            dc.DrawRectangle(null, penW, new Rect(x, y, w, h));
            dc.DrawRectangle(null, penK, new Rect(x, y, w, h));
        }
    }
}
