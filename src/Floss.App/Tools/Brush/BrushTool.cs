using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Input;

namespace Floss.App.Tools;

// Wraps the existing stroke rasterisation engine and exposes it as an ITool.
// Handles both brush and eraser modes.
public sealed class BrushTool : ITool
{
    private readonly CanvasTool _inner;
    public bool IsEraser { get; set; }

    public BrushTool(CanvasTool inner, bool isEraser = false)
    {
        _inner = inner;
        IsEraser = isEraser;
    }

    public void Activate(ToolContext ctx)
    {
        _inner.SetKind(IsEraser ? ToolKind.Eraser : ToolKind.Brush);

        // 🔑 Restore runtime params from preset if available
        // if (ctx.ActivePreset != null && ctx.Brush != null)
        // {
        //     ctx.ActivePreset.ApplyToBrushPreset(ref ctx.Brush);
        // }
    }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _inner.SetKind(IsEraser || s.Source == CanvasInputSource.Eraser ? ToolKind.Eraser : ToolKind.Brush);
        _inner.Begin(ctx.Brush, ctx.Selection, s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s) => _inner.Update(ctx.Brush, s);

    public void PointerUp(ToolContext ctx, CanvasInputSample s) => _inner.End(ctx.Brush, s);

    public void Cancel(ToolContext ctx) => _inner.Cancel();

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }

    public int ActiveSampleCount => _inner.ActiveSampleCount;
    public bool HasPendingOperation => ActiveSampleCount > 0;
}
