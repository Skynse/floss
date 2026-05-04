using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Single click/tap input.
public sealed class ClickInputProcess : IInputProcess
{
    private CanvasInputSample _point;
    private bool _pending;

    public bool IsActive => false;
    public double Stabilization { get; set; }  // Not used

    public void PointerDown(CanvasInputSample s)
    {
        _point = s;
        _pending = true;
    }

    public void PointerMove(CanvasInputSample s) { }

    public void PointerUp(CanvasInputSample s)
    {
        _pending = false;
    }

    public void Cancel()
    {
        _pending = false;
    }

    public IProcessedInput? GetResult()
    {
        if (_pending)
        {
            _pending = false;
            return new ClickInput { Point = _point };
        }
        return null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom) { }
}
