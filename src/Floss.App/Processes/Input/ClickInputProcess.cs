using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Single click/tap input with live move tracking for tools that need it (eyedropper).
public sealed class ClickInputProcess : IInputProcess
{
    private CanvasInputSample _point;
    private bool _pending;
    private bool _isActive;

    public bool IsActive => _isActive;
    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }

    public void PointerDown(CanvasInputSample s)
    {
        _point = s;
        _pending = true;
        _isActive = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        if (_isActive)
            _point = s;
    }

    public void PointerUp(CanvasInputSample s)
    {
        _isActive = false;
    }

    public void Cancel()
    {
        _pending = false;
        _isActive = false;
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

    public IProcessedInput? GetPreview() =>
        _isActive ? new ClickInput { Point = _point } : null;

    public void RenderOverlay(DrawingContext dc, double zoom) { }
}
