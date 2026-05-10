using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Raw direct-position input for liquify — no stabilization, no lag.
public sealed class LiquifyInputProcess : IInputProcess
{
    private readonly List<CanvasInputSample> _samples = [];
    private bool _active;

    public bool HasBrushCursor => true;
    public bool IsActive => _active;
    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }

    public void PointerDown(CanvasInputSample s)
    {
        _samples.Clear();
        _samples.Add(s);
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        if (!_active) return;
        _samples.Add(s);
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;
        _samples.Add(s);
        _active = false;
    }

    public void Cancel()
    {
        _active = false;
        _samples.Clear();
    }

    public IProcessedInput? GetPreview()
    {
        if (!_active || _samples.Count == 0) return null;
        return new StrokeInput { RawSamples = _samples, SmoothedSamples = _samples };
    }

    public IProcessedInput? GetResult()
    {
        if (_active || _samples.Count == 0) return null;
        return new StrokeInput { RawSamples = _samples, SmoothedSamples = _samples };
    }

    public void RenderOverlay(DrawingContext dc, double zoom) { }
}
