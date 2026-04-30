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
    private IToolOperation? _activeOperation;

    public CanvasTool(DrawingDocument document, BrushEngine brushEngine)
    {
        _document = document;
        _brushEngine = brushEngine;
    }

    public ToolKind Kind { get; private set; } = ToolKind.Brush;
    public int ActiveSampleCount => _activeOperation?.SampleCount ?? 0;

    public void SetKind(ToolKind kind) => Kind = kind;

    public void Begin(BrushPreset brush, CanvasInputSample sample)
    {
        if (!_document.CanPaintActiveLayer) return;
        _activeOperation?.Cancel();
        _activeOperation = new FreehandStrokeOperation(_document, _brushEngine, brush, IsEraser(sample), sample);
    }

    public void Update(BrushPreset brush, CanvasInputSample sample)
    {
        _activeOperation?.Update(sample);
    }

    public void End(BrushPreset brush, CanvasInputSample sample)
    {
        _activeOperation?.Commit(sample);
        _activeOperation = null;
    }

    public void Cancel()
    {
        _activeOperation?.Cancel();
        _activeOperation = null;
    }

    private bool IsEraser(CanvasInputSample sample)
        => Kind == ToolKind.Eraser || sample.Source == CanvasInputSource.Eraser;
}
