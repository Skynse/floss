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

    public void SetKind(ToolKind kind)
    {
        Kind = kind;
    }

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
        _brushEngine.RasterizeSegment(_document.ActiveLayer.Bitmap, brush, IsEraser(sample), _lastSample, sample);
        _lastSample = sample;
        ActiveSampleCount += 1;
        _document.NotifyChanged();
    }

    public void End(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_active) return;
        if (sample.Phase != CanvasInputPhase.Up)
        {
            Update(brush, sample);
        }

        _active = false;
        ActiveSampleCount = 0;
        _document.CommitStroke();
    }

    public void Cancel()
    {
        _active = false;
        ActiveSampleCount = 0;
    }

    private bool IsEraser(CanvasInputSample sample)
    {
        return Kind == ToolKind.Eraser || sample.Source == CanvasInputSource.Eraser;
    }
}
