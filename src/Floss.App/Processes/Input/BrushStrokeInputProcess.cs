using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Captures freehand stroke points with optional stabilization/smoothing.
// StraightLine aux mode: holding Shift shows a guideline from the last anchor to
// the cursor; pressing down immediately commits anchor→click as a stroke, then
// the rest of that press is normal freehand painting.
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
    private CanvasInputSample? _lastKnownPos;

    private IProcessedInput? _immediateResult;

    public bool IsActive => _active;
    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }
    public double BrushSize { get; set; } = 8;

    private bool IsStraightLine => ToolAuxMode == ToolAuxOperationType.StraightLine;

    // History buffer for Gaussian-weighted moving-average stabilization.
    private readonly List<CanvasInputSample> _history = new(32);

    public void PointerDown(CanvasInputSample s)
    {
        _raw.Clear();
        _smoothed.Clear();
        _history.Clear();

        if (IsStraightLine && _straightLineAnchorSet)
        {
            var a = _straightLineAnchor.WithPosition(_straightLineAnchor.X, _straightLineAnchor.Y, 1.0, _straightLineAnchor.TimeMicros);
            var b = s.WithPosition(s.X, s.Y, 1.0, s.TimeMicros);
            _immediateResult = new StrokeInput
            {
                RawSamples = [a, b],
                SmoothedSamples = [a, b]
            };
        }

        _straightLineAnchorSet = false;
        _raw.Add(s);
        _smoothed.Add(s);
        _lastSmoothed = s;
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        _lastKnownPos = s;
        if (!_active) return;

        _raw.Add(s);
        var smoothed = ApplyStabilization(s);
        _smoothed.Add(smoothed);
        _lastSmoothed = smoothed;
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;

        _raw.Add(s);
        _smoothed.Add(ApplyStabilization(s));
        _straightLineAnchor = _smoothed[^1];
        _straightLineAnchorSet = true;
        _active = false;
    }

    public void Cancel()
    {
        _active = false;
        _immediateResult = null;
        _raw.Clear();
        _smoothed.Clear();
        _history.Clear();
    }

    public IProcessedInput? GetImmediateResult()
    {
        var r = _immediateResult;
        _immediateResult = null;
        return r;
    }

    public IProcessedInput? GetResult()
    {
        if (!_active && _smoothed.Count > 0)
        {
            return new StrokeInput
            {
                RawSamples = new List<CanvasInputSample>(_raw),
                SmoothedSamples = new List<CanvasInputSample>(_smoothed)
            };
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
        if (_lastKnownPos is not { } end) return;

        StraightLineOverlay.Draw(dc, zoom,
            new Avalonia.Point(_straightLineAnchor.X, _straightLineAnchor.Y),
            new Avalonia.Point(end.X, end.Y),
            BrushSize);
    }

    private CanvasInputSample ApplyStabilization(CanvasInputSample raw)
    {
        if (Stabilization <= 0) return raw;

        var maxWindow = 20;
        var windowSize = Math.Max(1, (int)(Stabilization * maxWindow));

        _history.Add(raw);
        while (_history.Count > windowSize)
            _history.RemoveAt(0);

        if (_history.Count == 1) return raw;

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
