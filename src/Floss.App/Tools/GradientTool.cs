using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

public enum GradientType { Linear, Radial }

// Drag to define gradient direction; release to apply to the active layer (respects selection).
public sealed class GradientTool : ITool
{
    public GradientType GradientType { get; set; } = GradientType.Linear;

    private GradientToolOperation? _operation;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _operation?.Cancel();
        _operation = new GradientToolOperation(ctx, s, GradientType);
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
        _operation?.RenderOverlay(dc, zoom);
    }
}
