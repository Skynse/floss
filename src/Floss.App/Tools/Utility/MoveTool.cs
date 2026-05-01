using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

// Translates the active layer's offset (no selection) or moves selected pixels (with selection).
public sealed class MoveTool : ITool
{
    private IToolOperation? _operation;
    public bool HasPendingOperation => _operation != null;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null) return;
        _operation?.Cancel();
        _operation = ctx.Selection.HasSelection
            ? new SelectionMoveOperation(ctx, s)
            : new MoveToolOperation(ctx, s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        _operation?.Update(s);
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        _operation?.Commit(s);
        _operation = null;
    }

    public void Cancel(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = null;
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        if (_operation is IToolOperationOverlay overlay)
            overlay.RenderOverlay(dc, zoom);
    }
}
