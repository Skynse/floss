using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Captures freehand stroke points with optional stabilization/smoothing.
// In StraightLine aux mode, draws straight lines from an anchor point.
public sealed class BrushStrokeInputProcess : IInputProcess
{
    public bool HasBrushCursor => true;
    private readonly List<CanvasInputSample> _raw = [];
    private readonly List<CanvasInputSample> _smoothed = [];
    private bool _active;
    private CanvasInputSample _lastSmoothed;

    // StraightLine state
    private bool _straightLineAnchorSet;
    private CanvasInputSample _straightLineAnchor;
    private CanvasInputSample? _straightLineHoverEnd;

    public bool IsActive => _active;
    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }

    private bool IsStraightLine => ToolAuxMode == ToolAuxOperationType.StraightLine;

    // History buffer for Gaussian-weighted moving-average stabilization.
    private readonly List<CanvasInputSample> _history = new(32);

    public void PointerDown(CanvasInputSample s)
    {
        if (IsStraightLine)
        {
            if (!_straightLineAnchorSet)
            {
                _straightLineAnchor = s;
                _straightLineAnchorSet = true;
            }
            _raw.Clear();
            _smoothed.Clear();
            _history.Clear();
            _raw.Add(_straightLineAnchor);
            _smoothed.Add(_straightLineAnchor);
            _raw.Add(s);
            _smoothed.Add(s);
            _lastSmoothed = s;
            _active = true;
            return;
        }

        _straightLineAnchorSet = false;
        _raw.Clear();
        _smoothed.Clear();
        _history.Clear();
        _raw.Add(s);
        _smoothed.Add(s);
        _lastSmoothed = s;
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        if (IsStraightLine)
        {
            if (_active)
            {
                _raw[1] = s;
                _smoothed[1] = s;
                _lastSmoothed = s;
            }
            else
            {
                _straightLineHoverEnd = s;
            }
            return;
        }

        _straightLineHoverEnd = null;
        if (!_active) return;
        _raw.Add(s);

        var smoothed = ApplyStabilization(s);
        _smoothed.Add(smoothed);
        _lastSmoothed = smoothed;
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;

        if (IsStraightLine)
        {
            _raw[1] = s;
            _smoothed[1] = s;
            _straightLineAnchor = s;
            _straightLineAnchorSet = true;
            _active = false;
            return;
        }

        _raw.Add(s);
        _smoothed.Add(ApplyStabilization(s));
        // Remember last point for straight-line continuation (Shift)
        _straightLineAnchor = _smoothed[^1];
        _straightLineAnchorSet = true;
        _active = false;
    }

    public void Cancel()
    {
        _active = false;
        _raw.Clear();
        _smoothed.Clear();
        _history.Clear();
    }

    public IProcessedInput? GetResult()
    {
        if (!_active && _smoothed.Count > 0)
        {
            var result = new StrokeInput
            {
                RawSamples = new List<CanvasInputSample>(_raw),
                SmoothedSamples = new List<CanvasInputSample>(_smoothed)
            };
            return result;
        }
        return null;
    }

    public IProcessedInput? GetPreview()
    {
        if (_active && _smoothed.Count > 0)
        {
            return new StrokeInput
            {
                RawSamples = new List<CanvasInputSample>(_raw),
                SmoothedSamples = new List<CanvasInputSample>(_smoothed)
            };
        }

        return null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!IsStraightLine || !_straightLineAnchorSet) return;

        var t = Math.Max(0.5, 1.0 / zoom);
        var pen = new Pen(Avalonia.Media.Brushes.Black, t);

        if (_active && _smoothed.Count >= 2)
        {
            dc.DrawLine(
                pen,
                new Avalonia.Point(_smoothed[0].X, _smoothed[0].Y),
                new Avalonia.Point(_smoothed[1].X, _smoothed[1].Y));
        }
        else if (_straightLineHoverEnd is { } end)
        {
            dc.DrawLine(
                pen,
                new Avalonia.Point(_straightLineAnchor.X, _straightLineAnchor.Y),
                new Avalonia.Point(end.X, end.Y));
        }
    }

    private CanvasInputSample ApplyStabilization(CanvasInputSample raw)
    {
        if (Stabilization <= 0) return raw;

        // Window size grows with stabilization (0.0 -> 1, 0.95 -> 20)
        var maxWindow = 20;
        var windowSize = Math.Max(1, (int)(Stabilization * maxWindow));

        _history.Add(raw);
        while (_history.Count > windowSize)
            _history.RemoveAt(0);

        if (_history.Count == 1) return raw;

        // Gaussian-weighted average centered on newest point
        var center = _history.Count - 1;
        var sigma = Math.Max(1.0, _history.Count / 3.0);
        var totalWeight = 0.0;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumPressure = 0.0;

        for (var i = 0; i < _history.Count; i++)
        {
            var w = Math.Exp(-0.5 * Math.Pow((i - center) / sigma, 2));
            totalWeight += w;
            sumX += _history[i].X * w;
            sumY += _history[i].Y * w;
            sumPressure += _history[i].Pressure * w;
        }

        return raw.WithPosition(
            sumX / totalWeight,
            sumY / totalWeight,
            sumPressure / totalWeight,
            raw.TimeMicros);
    }
}
