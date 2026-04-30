using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

public enum ToolInputEventKind
{
    Down,
    Move,
    Up
}

public readonly record struct ToolInputEvent(ToolInputEventKind Kind, CanvasInputSample Sample);

public sealed class ToolController
{
    private readonly ToolContext _context;

    public ToolController(ToolContext context, ITool initialTool)
    {
        _context = context;
        ActiveTool = initialTool;
        ActiveTool.Activate(_context);
    }

    public ITool ActiveTool { get; private set; }

    public void SetActiveTool(ITool tool)
    {
        if (ActiveTool == tool) return;
        ActiveTool.Deactivate(_context);
        ActiveTool = tool;
        ActiveTool.Activate(_context);
    }

    public void Dispatch(ToolInputEvent input)
    {
        switch (input.Kind)
        {
            case ToolInputEventKind.Down:
                ActiveTool.PointerDown(_context, input.Sample);
                break;
            case ToolInputEventKind.Move:
                ActiveTool.PointerMove(_context, input.Sample);
                break;
            case ToolInputEventKind.Up:
                ActiveTool.PointerUp(_context, input.Sample);
                break;
        }
    }

    public void Cancel() => ActiveTool.Cancel(_context);

    public void RenderOverlay(DrawingContext dc, double zoom)
        => ActiveTool.RenderOverlay(dc, _context, zoom);
}
