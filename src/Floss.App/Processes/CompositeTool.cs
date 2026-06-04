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
        var hadPending = HasPendingOperation;
        Input.Cancel();
        if (Output is DirectDrawOutput directDraw && directDraw.HasPendingWork)
            directDraw.FinalizeAccepting();
        else if (Output is { } output && hadPending)
            output.Cancel();
        InvalidateUi(ctx);
    }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        Input.ToolAuxMode = ctx.ToolAuxMode;
        if (Output is SelectionAreaOutput sao)
            ctx.ActiveSelectionOp = SelectOpHelper.ResolveForSelection(sao.Operation, ctx);
        if (Input is RectInputProcess rip && Output is not SelectionAreaOutput)
            rip.Constrain = rip.ConsumesModifier(ctx.CurrentModifiers);
        Input.PointerDown(s);
        if (Input.GetImmediateResult() is { } immediate)
        {
            Output.Execute(ctx, immediate);
            InvalidateUi(ctx);
        }
        if (Input.IsActive && Input.GetPreview() is { } preview)
        {
            Output.Preview(ctx, preview);
            InvalidateUi(ctx);
        }
        else if (Output is SelectionAreaOutput)
        {
            ctx.InvalidateSelectionOverlay();
        }
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        Input.ToolAuxMode = ctx.ToolAuxMode;
        Input.PointerMove(s);
        if (Input.IsActive && Input.GetPreview() is { } preview)
            Output.Preview(ctx, preview);
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        Input.PointerUp(s);
        if (Input.GetResult() is { } result)
            Output.Execute(ctx, result);
        ClearSelectionGesture(ctx);
        InvalidateUi(ctx);
    }

    public void Cancel(ToolContext ctx)
    {
        Input.Cancel();
        Output.Cancel();
        ClearSelectionGesture(ctx);
    }

    public bool HasPendingOperation =>
        Input.IsActive ||
        (Output is DirectDrawOutput directDraw && directDraw.HasPendingWork);

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom)
    {
        if (Input is BrushStrokeInputProcess bsip)
            bsip.BrushSize = ctx.Brush?.Size ?? 8;
        else if (Input is LiquifyInputProcess lip)
            lip.BrushSize = ctx.ActivePreset?.LiquifySize ?? 48;
        Input.RenderOverlay(dc, zoom);
    }

    public bool CanCommitFromClick => Input is PolylineInputProcess;

    public bool ConsumesModifier(Avalonia.Input.KeyModifiers mods)
        => Output is not SelectionAreaOutput && Input.ConsumesModifier(mods);

    public void Commit(ToolContext ctx)
    {
        Input.Commit();
        if (Input.GetResult() is { } result)
        {
            Output.Execute(ctx, result);
        }
        else if (Input.IsActive)
            Input.Cancel();

        if (Output is DirectDrawOutput directDraw && directDraw.HasPendingWork)
            directDraw.FlushPending();

        ClearSelectionGesture(ctx);
        InvalidateUi(ctx);
    }

    private static void ClearSelectionGesture(ToolContext ctx) => ctx.ActiveSelectionOp = null;

    private void InvalidateUi(ToolContext ctx)
    {
        ctx.InvalidateRender();
        if (Output is SelectionAreaOutput)
            ctx.InvalidateSelectionOverlay();
    }
}
