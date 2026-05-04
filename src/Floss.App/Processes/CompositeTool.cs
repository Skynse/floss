using Avalonia.Media;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes;

// Wires an IInputProcess to an IOutputProcess, implementing ITool.
public sealed class CompositeTool : ITool
{
    public IInputProcess Input { get; }
    public IOutputProcess Output { get; }

    public CompositeTool(IInputProcess input, IOutputProcess output)
    {
        Input = input;
        Output = output;
    }

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) => Cancel(ctx);

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        Input.PointerDown(s);
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        Input.PointerMove(s);
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        Input.PointerUp(s);
        if (Input.GetResult() is { } result)
        {
            Output.Execute(ctx, result);
        }
    }

    public void Cancel(ToolContext ctx)
    {
        Input.Cancel();
    }

    public bool HasPendingOperation => Input.IsActive;

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        Input.RenderOverlay(dc, zoom);
    }

    public void Commit(ToolContext ctx)
    {
        // For modal tools (polyline, etc.), commit current input if any.
        if (Input.GetResult() is { } result)
        {
            Output.Execute(ctx, result);
        }
    }

    public bool CanCommitFromClick => false;
}
