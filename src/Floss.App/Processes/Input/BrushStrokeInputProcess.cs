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
    private float _lastSpeed01;

    public void PointerDown(CanvasInputSample s)
    {
        _raw.Clear();
        _smoothed.Clear();
        _history.Clear();
        _lastSpeed01 = 0;

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
        _history.Add(s);
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        _lastKnownPos = s;
        if (!_active) return;

        var smoothed = ApplyStabilization(s);
        if (ShouldSkipSample(smoothed))
            return;

        _raw.Add(s);
        _smoothed.Add(smoothed);
        _lastSmoothed = smoothed;
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;

        _raw.Add(s);
        var smoothed = ApplyStabilization(s);
        if (_smoothed.Count == 0 || !ShouldSkipSample(smoothed))
            _smoothed.Add(smoothed);
        FinishStroke();
    }

    public void Commit()
    {
        if (!_active) return;
        FinishStroke();
    }

    public void Cancel()
    {
        _active = false;
        _immediateResult = null;
        _raw.Clear();
        _smoothed.Clear();
        _history.Clear();
        _lastSpeed01 = 0;
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
            var result = new StrokeInput
            {
                RawSamples = new List<CanvasInputSample>(_raw),
                SmoothedSamples = new List<CanvasInputSample>(_smoothed)
            };
            _raw.Clear();
            _smoothed.Clear();
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

    private void FinishStroke()
    {
        _active = false;
        if (_smoothed.Count == 0) return;
        _straightLineAnchor = _smoothed[^1];
        _straightLineAnchorSet = true;
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
        _lastSpeed01 = ComputeSpeed01(raw);

        if (Stabilization <= 0)
            return raw;

        // Krita-style: fast strokes use a smaller smooth window, slow strokes a larger one.
        const int maxWindow = 24;
        const int minWindow = 4;
        var speedBlend = Math.Clamp(_lastSpeed01, 0f, 1f);
        var targetWindow = (1.0 - speedBlend * 0.7) * Stabilization * maxWindow
            + speedBlend * 0.7 * minWindow;
        var windowSize = Math.Max(minWindow, (int)Math.Round(targetWindow));

        _history.Add(raw);
        while (_history.Count > windowSize)
            _history.RemoveAt(0);

        if (_history.Count == 1)
            return raw;

        var center = _history.Count - 1;
        var sigma = Math.Max(1.0, windowSize / 3.0);
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

    private float ComputeSpeed01(CanvasInputSample raw)
    {
        if (_history.Count == 0)
            return 0;

        var prev = _history[^1];
        var dx = raw.X - prev.X;
        var dy = raw.Y - prev.Y;
        var dt = Math.Max(0.001, (raw.TimeMicros - prev.TimeMicros) / 1_000_000.0);
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp((float)(dist / dt / 5000.0), 0f, 1f);
    }

    // Drop redundant samples on fast strokes — fewer segments reach the engine without
    // changing nominal brush spacing on slow, detail work.
    private bool ShouldSkipSample(CanvasInputSample smoothed)
    {
        if (_smoothed.Count == 0)
            return false;

        var dx = smoothed.X - _lastSmoothed.X;
        var dy = smoothed.Y - _lastSmoothed.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var speedFactor = 0.2 + _lastSpeed01 * 0.8;
        var minDist = Math.Max(0.5, BrushSize * 0.006 * speedFactor);
        return dist < minDist;
    }
}
