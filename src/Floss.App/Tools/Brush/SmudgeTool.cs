using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

public sealed class SmudgeTool : ITool
{
    private SmudgeStrokeOperation? _op;

    public bool HasPendingOperation => _op != null;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { _op?.Cancel(); _op = null; }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        if (!ctx.Document.CanPaintActiveLayer) return;
        _op?.Cancel();
        _op = new SmudgeStrokeOperation(ctx.Document, ctx.Brush, s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
        => _op?.Update(ctx.Brush, s);

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        _op?.Commit(s);
        _op = null;
    }

    public void Cancel(ToolContext ctx)
    {
        _op?.Cancel();
        _op = null;
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }
}
