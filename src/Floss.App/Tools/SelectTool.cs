using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

public enum SelectMode { Rect, Lasso, PolylineLasso }

// Handles rectangular, freehand lasso, and click-to-add polyline lasso selection.
// The "lasso fill" workflow: draw a lasso, switch to FillTool → fill respects the selection.
public sealed class SelectTool : ITool
{
    public SelectMode Mode  { get; set; } = SelectMode.Rect;
    public SelectOp   Op    { get; set; } = SelectOp.Replace;

    private SelectionToolOperation? _operation;

    public void Activate(ToolContext ctx)
    {
        ctx.Selection.Resize(ctx.Document.Width, ctx.Document.Height);
    }

    public void Deactivate(ToolContext ctx)
    {
        _operation?.Cancel();
        _operation = null;
    }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        switch (Mode)
        {
            case SelectMode.Rect:
                _operation?.Cancel();
                _operation = new RectSelectionOperation(ctx, s, Op);
                break;

            case SelectMode.Lasso:
                _operation?.Cancel();
                _operation = new LassoSelectionOperation(ctx, s, Op);
                break;

            case SelectMode.PolylineLasso:
                if (_operation is PolylineSelectionOperation polyline)
                    polyline.AddPoint(s);
                else
                    _operation = new PolylineSelectionOperation(ctx, s, Op);
                break;
        }
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        _operation?.Update(s);
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        if (_operation is RectSelectionOperation or LassoSelectionOperation)
        {
            _operation.Commit(s);
            _operation = null;
        }
    }

    // Called from MainWindow on double-click or Enter key while PolylineLasso is pending.
    public void CommitPolyline(ToolContext ctx)
    {
        if (_operation is PolylineSelectionOperation polyline)
            polyline.Commit(default);
        else
            ctx.InvalidateRender();
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
        ctx.Selection.RenderOverlay(dc, zoom);
    }
}
