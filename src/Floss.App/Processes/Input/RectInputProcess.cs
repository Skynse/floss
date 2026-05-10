using System;
using Avalonia;
using Avalonia.Input;
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
    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public bool Constrain { get; set; }

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

    public bool ConsumesModifier(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Shift);

    public IProcessedInput? GetResult()
    {
        if (!_active)
            return new RectInput { Start = _start, End = ApplyConstrain(_start, _current) };
        return null;
    }

    public IProcessedInput? GetPreview()
    {
        if (_active)
            return new RectInput { Start = _start, End = ApplyConstrain(_start, _current) };
        return null;
    }

    private CanvasInputSample ApplyConstrain(CanvasInputSample start, CanvasInputSample end)
    {
        if (!Constrain) return end;
        if (ShapeKind != ShapeKind.Line)
        {
            var w = end.X - start.X;
            var h = end.Y - start.Y;
            var size = Math.Min(Math.Abs(w), Math.Abs(h));
            var signX = w >= 0 ? 1 : -1;
            var signY = h >= 0 ? 1 : -1;
            return end with { X = start.X + signX * size, Y = start.Y + signY * size };
        }
        else
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var angle = Math.Atan2(dy, dx);
            var snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
            var len = Math.Sqrt(dx * dx + dy * dy);
            return end with { X = start.X + Math.Cos(snapped) * len, Y = start.Y + Math.Sin(snapped) * len };
        }
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!_active) return;
        var end = ApplyConstrain(_start, _current);
        var t = Math.Max(0.5, 1.0 / zoom);
        var d = 4.0 / zoom;
        var penW = new Pen(Avalonia.Media.Brushes.White, t, new DashStyle([d, d], 0));
        var penK = new Pen(Avalonia.Media.Brushes.Black, t, new DashStyle([d, d], d));

        if (ShapeKind == ShapeKind.Line)
        {
            var p0 = new Point(_start.X, _start.Y);
            var p1 = new Point(end.X, end.Y);
            dc.DrawLine(penW, p0, p1);
            dc.DrawLine(penK, p0, p1);
        }
        else if (ShapeKind == ShapeKind.Ellipse)
        {
            var cx = (_start.X + end.X) * 0.5;
            var cy = (_start.Y + end.Y) * 0.5;
            var rx = Math.Abs(end.X - _start.X) * 0.5;
            var ry = Math.Abs(end.Y - _start.Y) * 0.5;
            dc.DrawEllipse(null, penW, new Point(cx, cy), rx, ry);
            dc.DrawEllipse(null, penK, new Point(cx, cy), rx, ry);
        }
        else
        {
            var x = Math.Min(_start.X, end.X);
            var y = Math.Min(_start.Y, end.Y);
            var w = Math.Abs(end.X - _start.X);
            var h = Math.Abs(end.Y - _start.Y);
            dc.DrawRectangle(null, penW, new Rect(x, y, w, h));
            dc.DrawRectangle(null, penK, new Rect(x, y, w, h));
        }
    }
}
