using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

// Samples the pixel color from the composite canvas and fires the OnColorSampled callback.
public sealed class EyedropperTool : ITool
{
    private bool _active;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _active = true;
        Sample(ctx, s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        if (_active) Sample(ctx, s);
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        if (_active) Sample(ctx, s);
        _active = false;
    }

    public void Cancel(ToolContext ctx) => _active = false;

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }

    private static void Sample(ToolContext ctx, CanvasInputSample s)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null) return;
        int x = (int)s.X - layer.OffsetX;
        int y = (int)s.Y - layer.OffsetY;
        layer.Pixels.GetPixel(x, y, out byte b, out byte g, out byte r, out byte a);
        if (a == 0) return;
        ctx.OnColorSampled(Color.FromArgb(a, r, g, b));
    }
}
