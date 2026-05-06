using Avalonia.Media;
using Floss.App.Input;
using Floss.App.Processes.Input;
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
        if (Input.IsActive && Input.GetPreview() is { } preview)
        {
            Output.Preview(ctx, preview);
            ctx.InvalidateRender();
        }
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        Input.PointerMove(s);
        if (Input.IsActive && Input.GetPreview() is { } preview)
        {
            Output.Preview(ctx, preview);
        }
        ctx.InvalidateRender();
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        Input.PointerUp(s);
        if (Input.GetResult() is { } result)
        {
            Output.Execute(ctx, result);
        }
        ctx.InvalidateRender();
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

    public bool CanCommitFromClick => Input is PolylineInputProcess;

    public void Commit(ToolContext ctx)
    {
        // For modal tools (polyline, etc.), mark as explicitly committed.
        Input.Commit();
        if (Input.GetResult() is { } result)
        {
            Output.Execute(ctx, result);
        }
    }
}
