using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Captures freehand polygon points with optional stabilization/smoothing.
public sealed class LassoInputProcess : IInputProcess
{
    private readonly List<CanvasInputSample> _raw = [];
    private readonly List<CanvasInputSample> _smoothed = [];
    private bool _active;
    private CanvasInputSample _lastSmoothed;

    public bool IsActive => _active;
    public ToolAuxOperationType ToolAuxMode { get; set; }
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
        if (!_active && _smoothed.Count >= 3)
        {
            return new PolygonInput
            {
                RawPoints = new List<CanvasInputSample>(_raw),
                SmoothedPoints = new List<CanvasInputSample>(_smoothed),
                IsClosed = true
            };
        }
        return null;
    }

    public IProcessedInput? GetPreview()
    {
        if (_active && _smoothed.Count > 0)
        {
            return new PolygonInput
            {
                RawPoints = new List<CanvasInputSample>(_raw),
                SmoothedPoints = new List<CanvasInputSample>(_smoothed),
                IsClosed = true
            };
        }
        return null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!_active) return;
        if (_smoothed.Count < 2) return;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Avalonia.Point(_smoothed[0].X, _smoothed[0].Y), false);
            for (int i = 1; i < _smoothed.Count; i++)
                c.LineTo(new Avalonia.Point(_smoothed[i].X, _smoothed[i].Y));
            c.EndFigure(false);
        }
        var t = Math.Max(0.5, 1.0 / zoom);
        dc.DrawGeometry(null, new Pen(Avalonia.Media.Brushes.Black, t), geo);
    }

    private CanvasInputSample ApplyStabilization(CanvasInputSample raw)
    {
        if (Stabilization <= 0) return raw;
        var s = Math.Clamp(Stabilization, 0, 0.99);
        var alpha = 1.0 - s;
        return raw.WithPosition(
            _lastSmoothed.X + (raw.X - _lastSmoothed.X) * alpha,
            _lastSmoothed.Y + (raw.Y - _lastSmoothed.Y) * alpha,
            raw.Pressure,
            raw.TimeMicros);
    }
}
