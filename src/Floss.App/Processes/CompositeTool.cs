using Avalonia.Media;
using Floss.App.Input;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Tools;

namespace Floss.App.Processes;

// Wires an IInputProcess to an IOutputProcess, implementing ITool.
public sealed class CompositeTool : ITool
{
    public IInputProcess Input { get; }
    public IOutputProcess Output { get; }
    public ITool? Alternate { get; }

    public CompositeTool(IInputProcess input, IOutputProcess output, ITool? alternate = null)
    {
        Input = input;
        Output = output;
        Alternate = alternate;
    }

    public void Activate(ToolContext ctx) { }

    public void Deactivate(ToolContext ctx)
    {
        if (Input.IsActive)
        {
            Cancel(ctx);
            ctx.InvalidateRender();
        }
        else
            Input.Cancel();
    }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        Input.ToolAuxMode = ctx.ToolAuxMode;
        if (Input is RectInputProcess rip)
            rip.Constrain = rip.ConsumesModifier(ctx.CurrentModifiers);
        Input.PointerDown(s);
        if (Input.GetImmediateResult() is { } immediate)
        {
            Output.Execute(ctx, immediate);
            ctx.InvalidateRender();
        }
        if (Input.IsActive && Input.GetPreview() is { } preview)
        {
            Output.Preview(ctx, preview);
            ctx.InvalidateRender();
        }
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        Input.ToolAuxMode = ctx.ToolAuxMode;
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
        Output.Cancel();
    }

    public bool HasPendingOperation => Input.IsActive;

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        if (Input is BrushStrokeInputProcess bsip)
            bsip.BrushSize = ctx.Brush?.Size ?? 8;
        else if (Input is LiquifyInputProcess lip)
            lip.BrushSize = ctx.ActivePreset?.LiquifySize ?? 48;
        Input.RenderOverlay(dc, zoom);
    }

    public bool CanCommitFromClick => Input is PolylineInputProcess;

    public bool ConsumesModifier(Avalonia.Input.KeyModifiers mods) => Input.ConsumesModifier(mods);

    public void Commit(ToolContext ctx)
    {
        // For modal tools (polyline, etc.), mark as explicitly committed.
        Input.Commit();
        if (Input.GetResult() is { } result)
        {
            Output.Execute(ctx, result);
            if (Output is DirectDrawOutput directDraw)
                directDraw.FlushPending();
        }
    }
}
