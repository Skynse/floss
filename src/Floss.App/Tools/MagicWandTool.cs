using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

// Flood-selects a region by color similarity, respecting a tolerance threshold.
public sealed class MagicWandTool : ITool
{
    public double Tolerance { get; set; } = 0.15;
    public SelectOp Op { get; set; } = SelectOp.Replace;

    public void Activate(ToolContext ctx)
    {
        ctx.Selection.Resize(ctx.Document.Width, ctx.Document.Height);
    }

    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null) return;
        int x = (int)s.X - layer.OffsetX;
        int y = (int)s.Y - layer.OffsetY;
        ctx.Selection.SetFromFloodFill(layer.Pixels, x, y, Tolerance, Op);
        ctx.InvalidateRender();
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s) { }
    public void PointerUp(ToolContext ctx, CanvasInputSample s) { }
    public void Cancel(ToolContext ctx) { }
    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
        => ctx.Selection.RenderOverlay(dc, zoom);
}
