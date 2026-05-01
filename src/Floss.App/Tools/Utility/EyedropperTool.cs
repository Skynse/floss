using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

// Samples the pixel color from the composite canvas and fires the OnColorSampled callback.
public sealed class EyedropperTool : ITool
{
    private ColorSampleOperation? _operation;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _operation = new ColorSampleOperation(ctx);
        _operation.Commit(s);
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

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }
}
