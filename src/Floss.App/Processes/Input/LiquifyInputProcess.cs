using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Raw direct-position input for liquify — no stabilization, no lag.
// StraightLine aux mode: holding Shift shows a guideline; pressing down commits
// anchor→click immediately, then the rest of that press is normal freehand.
public sealed class LiquifyInputProcess : IInputProcess
{
    private readonly List<CanvasInputSample> _samples = [];
    private bool _active;

    // StraightLine state
    private bool _straightLineAnchorSet;
    private CanvasInputSample _straightLineAnchor;
    private CanvasInputSample? _lastKnownPos;

    private IProcessedInput? _immediateResult;

    public bool HasBrushCursor => true;
    public bool IsActive => _active;
    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }

    private bool IsStraightLine => ToolAuxMode == ToolAuxOperationType.StraightLine;

    public void PointerDown(CanvasInputSample s)
    {
        _samples.Clear();

        if (IsStraightLine && _straightLineAnchorSet)
        {
            _immediateResult = new StrokeInput
            {
                RawSamples = [_straightLineAnchor, s],
                SmoothedSamples = [_straightLineAnchor, s]
            };
        }

        _straightLineAnchorSet = false;
        _samples.Add(s);
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        _lastKnownPos = s;
        if (!_active) return;
        _samples.Add(s);
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;
        _samples.Add(s);
        _straightLineAnchor = _samples[^1];
        _straightLineAnchorSet = true;
        _active = false;
    }

    public void Cancel()
    {
        _active = false;
        _immediateResult = null;
        _samples.Clear();
    }

    public IProcessedInput? GetImmediateResult()
    {
        var r = _immediateResult;
        _immediateResult = null;
        return r;
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

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!IsStraightLine || !_straightLineAnchorSet) return;
        if (_lastKnownPos is not { } end) return;

        var t = Math.Max(0.5, 1.0 / zoom);
        var pen = new Pen(Avalonia.Media.Brushes.Black, t);

        dc.DrawLine(
            pen,
            new Avalonia.Point(_straightLineAnchor.X, _straightLineAnchor.Y),
            new Avalonia.Point(end.X, end.Y));
    }
}
