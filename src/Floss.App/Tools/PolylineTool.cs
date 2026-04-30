using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

// Click to add vertices; double-click or Enter to commit the polyline onto the active layer.
public sealed class PolylineTool : ITool
{
    public float StrokeWidth { get; set; } = 4f;
    public bool ClosePath { get; set; } = false;

    private PolylineToolOperation? _operation;

    public void Activate(ToolContext ctx) { }

    public void Deactivate(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = null;
    }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        if (_operation == null)
            _operation = new PolylineToolOperation(ctx, s, StrokeWidth, ClosePath);
        else
            _operation.AddPoint(s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        _operation?.Update(s);
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s) { }

    // Called from MainWindow on double-click or Enter.
    public void Commit(ToolContext ctx)
    {
        _operation?.Commit(default);
        _operation = null;
    }

    public void Cancel(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = null;
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        _operation?.RenderOverlay(dc, zoom);
    }
}
