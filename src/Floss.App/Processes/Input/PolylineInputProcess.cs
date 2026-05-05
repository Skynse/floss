using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes.Input;

// Captures click-to-add polyline vertices. Double-click/Enter commits.
public sealed class PolylineInputProcess : IInputProcess
{
    private readonly List<CanvasInputSample> _points = [];
    private CanvasInputSample _cursor;
    private bool _active;
    private bool _committed;

    public bool IsActive => _active;
    public double Stabilization { get; set; }
    public bool ClosePath { get; set; }

    public void PointerDown(CanvasInputSample s)
    {
        if (!_active)
        {
            _active = true;
            _committed = false;
            _points.Clear();
        }
        _points.Add(s);
        _cursor = s;
    }

    public void PointerMove(CanvasInputSample s)
    {
        if (!_active) return;
        _cursor = s;
    }

    public void PointerUp(CanvasInputSample s)
    {
        _cursor = s;
    }

    public void Cancel()
    {
        _active = false;
        _committed = false;
        _points.Clear();
    }

    public void Commit() => _committed = true;

    public IProcessedInput? GetResult()
    {
        if (_committed && _active && _points.Count >= 2)
        {
            _active = false;
            _committed = false;
            return new PolygonInput
            {
                RawPoints = new List<CanvasInputSample>(_points),
                SmoothedPoints = new List<CanvasInputSample>(_points),
                IsClosed = ClosePath
            };
        }
        return null;
    }

    public IProcessedInput? GetPreview()
    {
        if (_active && _points.Count > 0)
        {
            return new PolygonInput
            {
                RawPoints = new List<CanvasInputSample>(_points),
                SmoothedPoints = new List<CanvasInputSample>(_points),
                IsClosed = ClosePath
            };
        }
        return null;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!_active) return;
        if (_points.Count == 0) return;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Avalonia.Point(_points[0].X, _points[0].Y), false);
            for (int i = 1; i < _points.Count; i++)
                c.LineTo(new Avalonia.Point(_points[i].X, _points[i].Y));
            if (_points.Count >= 2)
                c.LineTo(new Avalonia.Point(_cursor.X, _cursor.Y));
            c.EndFigure(false);
        }
        var t = Math.Max(0.5, 1.0 / zoom);
        dc.DrawGeometry(null, new Pen(Avalonia.Media.Brushes.Black, t), geo);
    }
}
