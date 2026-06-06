using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Krita stabilizer mode: fixed-size deque prefilled at stroke start, uniform
// position average via incremental mix (getStabilizedPaintInfo). Fast strokes
// shrink the deque; slow strokes widen it.
public sealed class BrushStrokeInputProcess : IInputProcess
{
    public bool HasBrushCursor => true;
    private readonly List<CanvasInputSample> _raw = [];
    private readonly List<CanvasInputSample> _smoothed = [];
    private readonly List<CanvasInputSample> _deque = new(64);
    private bool _active;
    private CanvasInputSample _lastSmoothed;
    private int _dequeCapacity = 1;

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

    public void PointerDown(CanvasInputSample s)
    {
        _raw.Clear();
        _smoothed.Clear();
        _deque.Clear();

        if (IsStraightLine && _straightLineAnchorSet)
        {
            var a = _straightLineAnchor.WithPosition(_straightLineAnchor.X, _straightLineAnchor.Y, 1.0, _straightLineAnchor.TimeMicros);
            var b = s.WithPosition(s.X, s.Y, 1.0, s.TimeMicros);
            _immediateResult = new StrokeInput
            {
                RawSamples = [a, b],
                SmoothedSamples = [a, b]
            };
            _straightLineAnchor = b;
            _straightLineAnchorSet = true;
            _lastKnownPos = s;
            _active = false;
            _raw.Clear();
            _smoothed.Clear();
            _deque.Clear();
            return;
        }

        _straightLineAnchorSet = false;
        _raw.Add(s);
        _smoothed.Add(s);
        _lastSmoothed = s;

        _dequeCapacity = StabilizerSampleCount(s);
        for (var i = 0; i < _dequeCapacity; i++)
            _deque.Add(s);

        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
        _lastKnownPos = s;
        if (!_active) return;

        _raw.Add(s);

        if (Stabilization <= 0)
        {
            _smoothed.Add(s);
            _lastSmoothed = s;
            return;
        }

        var smoothed = GetStabilizedPaintInfo(_deque, s);
        if (!NearlyEqual(smoothed, _lastSmoothed))
        {
            _smoothed.Add(smoothed);
            _lastSmoothed = smoothed;
        }

        if (_deque.Count > 0)
            _deque.RemoveAt(0);
        _deque.Add(s);
        MaintainDequeCapacity(s);
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_active) return;

        _raw.Add(s);

        if (Stabilization <= 0)
        {
            if (!NearlyEqual(s, _lastSmoothed))
                _smoothed.Add(s);
        }
        else
        {
            if (_deque.Count > 0)
                _deque.RemoveAt(0);
            _deque.Add(s);
            var smoothed = GetStabilizedPaintInfo(_deque, s);
            if (!NearlyEqual(smoothed, _lastSmoothed))
                _smoothed.Add(smoothed);
        }

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
        _deque.Clear();
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
            var result = BuildStrokeOutput(_raw, _smoothed, _lastKnownPos);
            _raw.Clear();
            _smoothed.Clear();
            _deque.Clear();
            return result;
        }
        return null;
    }

    public IProcessedInput? GetPreview()
    {
        if (!_active || _smoothed.Count == 0)
            return null;

        CanvasInputSample? rawLead = null;
        if (_lastKnownPos is { } raw)
        {
            var last = _smoothed[^1];
            var dx = raw.X - last.X;
            var dy = raw.Y - last.Y;
            if (dx * dx + dy * dy > 0.01)
                rawLead = raw;
        }

        return new StrokeInput
        {
            RawSamples = new List<CanvasInputSample>(_raw),
            SmoothedSamples = new List<CanvasInputSample>(_smoothed),
            RawLead = rawLead
        };
    }

    private static StrokeInput BuildStrokeOutput(List<CanvasInputSample> raw, List<CanvasInputSample> smoothed, CanvasInputSample? rawLead)
        => new()
        {
            RawSamples = new List<CanvasInputSample>(raw),
            SmoothedSamples = new List<CanvasInputSample>(smoothed),
            RawLead = rawLead
        };

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

    private void MaintainDequeCapacity(CanvasInputSample latest)
    {
        var target = StabilizerSampleCount(latest);
        if (target == _dequeCapacity && _deque.Count == _dequeCapacity)
            return;

        _dequeCapacity = target;
        while (_deque.Count > _dequeCapacity)
            _deque.RemoveAt(0);
        while (_deque.Count < _dequeCapacity)
            _deque.Insert(0, _deque[0]);
    }

    // Krita effectiveSmoothnessDistance → sample count (max distance default 50).
    private int StabilizerSampleCount(CanvasInputSample raw)
    {
        if (Stabilization <= 0)
            return 1;

        const int maxCount = 50;
        const int minCount = 3;
        var baseCount = Math.Max(minCount, (int)Math.Round(Stabilization * maxCount));

        if (!SpeedAdaptiveStabilizer)
            return baseCount;

        var speed = ComputeSpeed01(raw);
        var target = (1.0 - speed) * baseCount + speed * minCount;
        return Math.Max(minCount, (int)Math.Round(target));
    }

    // Krita getStabilizedPaintInfo — incremental uniform average (position + pressure).
    internal static CanvasInputSample GetStabilizedPaintInfo(IReadOnlyList<CanvasInputSample> queue, CanvasInputSample latest)
    {
        if (queue.Count <= 1)
            return latest;

        var x = latest.X;
        var y = latest.Y;
        var pressure = latest.Pressure;
        var i = 2;
        for (var idx = 1; idx < queue.Count; idx++)
        {
            var it = queue[idx];
            var k = (i - 1.0) / i;
            x = x * k + it.X * (1.0 - k);
            y = y * k + it.Y * (1.0 - k);
            pressure = pressure * k + it.Pressure * (1.0 - k);
            i++;
        }

        return latest.WithPosition(x, y, pressure, latest.TimeMicros);
    }

    private static bool NearlyEqual(CanvasInputSample a, CanvasInputSample b)
    {
        const double eps = 1e-6;
        return Math.Abs(a.X - b.X) < eps
            && Math.Abs(a.Y - b.Y) < eps
            && Math.Abs(a.Pressure - b.Pressure) < eps;
    }

    private float ComputeSpeed01(CanvasInputSample raw)
    {
        if (_deque.Count < 2)
            return 0;

        var prev = _deque[^2];
        var dx = raw.X - prev.X;
        var dy = raw.Y - prev.Y;
        var dt = Math.Max(0.001, (raw.TimeMicros - prev.TimeMicros) / 1_000_000.0);
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return Math.Clamp((float)(dist / dt / 5000.0), 0f, 1f);
    }
}
