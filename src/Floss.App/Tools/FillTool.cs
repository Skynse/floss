using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

// Flood-fills connected pixels matching the clicked color, constrained to the selection mask.
public sealed class FillTool : ITool
{
    public double Tolerance { get; set; } = 0.05;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        new FillToolOperation(ctx, Tolerance).Commit(s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s) { }
    public void PointerUp(ToolContext ctx, CanvasInputSample s) { }
    public void Cancel(ToolContext ctx) { }
    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }
}
