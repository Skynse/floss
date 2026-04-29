using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public enum ToolKind
{
    Brush,
    Eraser
}

public sealed class CanvasTool
{
    private readonly DrawingDocument _document;
    private readonly BrushEngine _brushEngine;
    private CanvasInputSample _lastSample;
    private bool _active;

    public CanvasTool(DrawingDocument document, BrushEngine brushEngine)
    {
        _document = document;
        _brushEngine = brushEngine;
    }

    public ToolKind Kind { get; private set; } = ToolKind.Brush;
    public int ActiveSampleCount { get; private set; }

    public void SetKind(ToolKind kind) => Kind = kind;

    public void Begin(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_document.CanPaintActiveLayer) return;
        _document.BeginDocumentMutation();
        _active = true;
        ActiveSampleCount = 1;
        _lastSample = sample;
        _brushEngine.RasterizeDab(_document.ActiveLayer.Bitmap, brush, IsEraser(sample), sample, 0);
        _document.NotifyChanged();
    }

    public void Update(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_active) return;
        var smoothed = ApplyStreamline(sample, brush.Smoothing);
        _brushEngine.RasterizeSegment(_document.ActiveLayer.Bitmap, brush, IsEraser(sample), _lastSample, smoothed);
        _lastSample = smoothed;
        ActiveSampleCount++;
        _document.NotifyChanged();
    }

    public void End(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_active) return;
        // Always draw the final segment so the stroke reaches the pen-up position
        _brushEngine.RasterizeSegment(_document.ActiveLayer.Bitmap, brush, IsEraser(sample), _lastSample, sample);
        _document.NotifyChanged();
        _active = false;
        ActiveSampleCount = 0;
        _document.CommitStroke();
    }

    public void Cancel()
    {
        _active = false;
        ActiveSampleCount = 0;
    }

    // Exponential moving average toward the new sample ("Streamline" smoothing)
    private CanvasInputSample ApplyStreamline(CanvasInputSample raw, double smoothing)
    {
        if (smoothing <= 0) return raw;
        var alpha = 1.0 - smoothing;
        return raw.WithPosition(
            _lastSample.X + (raw.X - _lastSample.X) * alpha,
            _lastSample.Y + (raw.Y - _lastSample.Y) * alpha,
            _lastSample.Pressure + (raw.Pressure - _lastSample.Pressure) * alpha,
            raw.TimeMicros);
    }

    private bool IsEraser(CanvasInputSample sample)
        => Kind == ToolKind.Eraser || sample.Source == CanvasInputSource.Eraser;
}
