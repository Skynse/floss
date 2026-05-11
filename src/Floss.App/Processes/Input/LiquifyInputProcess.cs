using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Raw direct-position input for liquify — no stabilization, no lag.
// In StraightLine aux mode, draws straight lines from an anchor point.
public sealed class LiquifyInputProcess : IInputProcess
{
    private readonly List<CanvasInputSample> _samples = [];
    private bool _active;

    // StraightLine state
    private bool _straightLineAnchorSet;
    private CanvasInputSample _straightLineAnchor;
    private CanvasInputSample? _straightLineHoverEnd;

    public bool HasBrushCursor => true;
    public bool IsActive => _active;
    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }

    private bool IsStraightLine => ToolAuxMode == ToolAuxOperationType.StraightLine;

    public void PointerDown(CanvasInputSample s)
    {
        if (IsStraightLine)
        {
            if (!_straightLineAnchorSet)
            {
                _straightLineAnchor = s;
                _straightLineAnchorSet = true;
            }
            _samples.Clear();
            _samples.Add(_straightLineAnchor);
            _samples.Add(s);
            _active = true;
            return;
        }

        _straightLineAnchorSet = false;
        _samples.Clear();
        _samples.Add(s);
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        if (IsStraightLine)
        {
            if (_active)
            {
                if (_samples.Count >= 2)
                    _samples[1] = s;
                else
                    _samples.Add(s);
            }
            else
            {
                _straightLineHoverEnd = s;
            }
            return;
        }

        _straightLineHoverEnd = null;
        if (!_active) return;
        _samples.Add(s);
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;

        if (IsStraightLine)
        {
            if (_samples.Count >= 2)
                _samples[1] = s;
            else
                _samples.Add(s);
            _straightLineAnchor = s;
            _straightLineAnchorSet = true;
            _active = false;
            return;
        }

        _samples.Add(s);
        _straightLineAnchor = _samples[^1];
        _straightLineAnchorSet = true;
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

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!IsStraightLine || !_straightLineAnchorSet) return;

        var t = Math.Max(0.5, 1.0 / zoom);
        var pen = new Pen(Avalonia.Media.Brushes.Black, t);

        if (_active && _samples.Count >= 2)
        {
            dc.DrawLine(
                pen,
                new Avalonia.Point(_samples[0].X, _samples[0].Y),
                new Avalonia.Point(_samples[1].X, _samples[1].Y));
        }
        else if (_straightLineHoverEnd is { } end)
        {
            dc.DrawLine(
                pen,
                new Avalonia.Point(_straightLineAnchor.X, _straightLineAnchor.Y),
                new Avalonia.Point(end.X, end.Y));
        }
    }
}
