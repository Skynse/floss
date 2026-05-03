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
    public bool HasPendingOperation => ActiveTool.HasPendingOperation;

    public void SetActiveTool(ITool tool, ToolPreset? preset = null)
    {
        if (ActiveTool == tool) return;
        ActiveTool.Deactivate(_context);
        _context.ActivePreset = preset;
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

    public bool Cancel()
    {
        var hadPendingOperation = ActiveTool.HasPendingOperation;
        ActiveTool.Cancel(_context);
        return hadPendingOperation;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
        => ActiveTool.RenderOverlay(dc, _context, zoom);
}
