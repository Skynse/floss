using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Krita/CSP-style stabilizer: uniform moving average over a fixed-size
// sample deque.  Fast strokes shrink the deque (less lag), slow strokes
// grow it (more stabilization).  The buffer is pre-filled with the down-point
// so the average starts moving immediately instead of creeping from zero.
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
    public bool SpeedAdaptiveStabilizer { get; set; } = true;

    private bool IsStraightLine => ToolAuxMode == ToolAuxOperationType.StraightLine;

    // Fixed-size deque for uniform stabilizer average.
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

        // Pre-fill stabilizer deque with the starting point so the average
        // doesn't sit at zero for the first N moves (Krita does the same).
        int count = StabilizerSampleCount(s);
        for (int i = 0; i < count; i++)
            _history.Add(s);

        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        _lastKnownPos = s;
        if (!_active) return;

        _raw.Add(s);
        _history.Add(s);

        int targetCount = StabilizerSampleCount(s);
        while (_history.Count > targetCount)
            _history.RemoveAt(0);

        var smoothed = GetStabilizedPosition();

        // Only emit when the stabilized cursor actually moves enough;
        // the raw stream is always preserved for the engine.
        var dx = smoothed.X - _lastSmoothed.X;
        var dy = smoothed.Y - _lastSmoothed.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 0.1)
            return;

        _smoothed.Add(smoothed);
        _lastSmoothed = smoothed;
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;

        _raw.Add(s);
        _history.Add(s);

        int targetCount = StabilizerSampleCount(s);
        while (_history.Count > targetCount)
            _history.RemoveAt(0);

        var smoothed = GetStabilizedPosition();
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

    // Target sample count for the stabilizer deque.
    // Fast strokes use fewer samples (less lag), slow strokes use more.
    private int StabilizerSampleCount(CanvasInputSample raw)
    {
        if (Stabilization <= 0)
            return 1;

        const int maxCount = 24;
        const int minCount = 2;

        var baseCount = (int)(Stabilization * maxCount);
        baseCount = Math.Max(minCount, baseCount);

        if (!SpeedAdaptiveStabilizer)
            return baseCount;

        _lastSpeed01 = ComputeSpeed01(raw);
        var speedBlend = Math.Clamp(_lastSpeed01, 0f, 1f);

        // Fast = minCount, slow = baseCount
        var target = (1.0 - speedBlend) * baseCount + speedBlend * minCount;
        return Math.Max(minCount, (int)Math.Round(target));
    }

    // Uniform average over the deque — same as Krita's stabilizer.
    private CanvasInputSample GetStabilizedPosition()
    {
        if (_history.Count == 0)
            return _lastSmoothed;

        var count = _history.Count;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumPressure = 0.0;

        for (var i = 0; i < count; i++)
        {
            var h = _history[i];
            sumX += h.X;
            sumY += h.Y;
            sumPressure += h.Pressure;
        }

        var latest = _history[^1];
        return latest.WithPosition(
            sumX / count,
            sumY / count,
            sumPressure / count,
            latest.TimeMicros);
    }

    private float ComputeSpeed01(CanvasInputSample raw)
    {
        if (_history.Count < 2)
            return 0;

        var prev = _history[^2];
        var dx = raw.X - prev.X;
        var dy = raw.Y - prev.Y;
        var dt = Math.Max(0.001, (raw.TimeMicros - prev.TimeMicros) / 1_000_000.0);
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp((float)(dist / dt / 5000.0), 0f, 1f);
    }
}
