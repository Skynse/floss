using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Captures freehand stroke points with optional stabilization/smoothing.
public sealed class BrushStrokeInputProcess : IInputProcess
{
    public bool HasBrushCursor => true;
    private readonly List<CanvasInputSample> _raw = [];
    private readonly List<CanvasInputSample> _smoothed = [];
    private bool _active;
    private CanvasInputSample _lastSmoothed;

    public bool IsActive => _active;
    public double Stabilization { get; set; }

    public void PointerDown(CanvasInputSample s)
    {
        _raw.Clear();
        _smoothed.Clear();
        _raw.Add(s);
        _smoothed.Add(s);
        _lastSmoothed = s;
        _active = true;
    }

    public void PointerMove(CanvasInputSample s)
    {
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
        _active = false;
    }

    public void Cancel()
    {
        _active = false;
        _raw.Clear();
        _smoothed.Clear();
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
        // Brush strokes don't need a path overlay — the brush dabs provide visual feedback.
    }

    private CanvasInputSample ApplyStabilization(CanvasInputSample raw)
    {
        if (Stabilization <= 0) return raw;
        var s = Math.Clamp(Stabilization, 0, 0.99);
        var alpha = 1.0 - s;
        return raw.WithPosition(
            _lastSmoothed.X + (raw.X - _lastSmoothed.X) * alpha,
            _lastSmoothed.Y + (raw.Y - _lastSmoothed.Y) * alpha,
            _lastSmoothed.Pressure + (raw.Pressure - _lastSmoothed.Pressure) * alpha,
            raw.TimeMicros);
    }
}
