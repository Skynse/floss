using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

public enum ShapeKind { Rectangle, Ellipse, Line }
public enum ShapeDrawMode { Fill, Stroke, FillAndStroke }

// Drag to preview a shape; release to rasterize it onto the active layer.
public sealed class ShapeTool : ITool
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;
    public ShapeDrawMode DrawMode { get; set; } = ShapeDrawMode.Fill;
    public float StrokeWidth { get; set; } = 4f;

    private ShapeToolOperation? _operation;
    public bool HasPendingOperation => _operation != null;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _operation?.Cancel();
        _operation = new ShapeToolOperation(ctx, s, Kind, DrawMode, StrokeWidth);
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
