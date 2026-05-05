using System;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Captures drag-to-define rectangle.
public sealed class RectInputProcess : IInputProcess
{
    private CanvasInputSample _start;
    private CanvasInputSample _current;
    private bool _active;

    public bool IsActive => _active;
    public double Stabilization { get; set; }  // Not used

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
        var x = Math.Min(_start.X, _current.X);
        var y = Math.Min(_start.Y, _current.Y);
        var w = Math.Abs(_current.X - _start.X);
        var h = Math.Abs(_current.Y - _start.Y);
        var t = Math.Max(0.5, 1.0 / zoom);
        dc.DrawRectangle(null, new Pen(Avalonia.Media.Brushes.Black, t), new Avalonia.Rect(x, y, w, h));
    }
}
