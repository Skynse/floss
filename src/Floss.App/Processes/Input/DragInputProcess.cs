using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Captures drag start-to-end.
public sealed class DragInputProcess : IInputProcess
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
            return new DragInput { Start = _start, Current = _current };
        }
        return null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        // No overlay by default — output processes handle their own preview.
    }
}
