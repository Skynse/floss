using System;
using Floss.App.Canvas.FloodFill;
using Floss.App.Config;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Creates a selection from a flood-fill at a click point (magic wand).
public sealed class MagicWandOutput : IOutputProcess
{
    // Reserved for soft selection edges (); not applied yet.
    public bool Antialiasing { get; set; } = false;
    public double Tolerance { get; set; } = 0.05;
    public SelectOp Operation { get; set; } = SelectOp.Replace;
    public FillReferenceMode FillReference { get; set; } = FillReferenceMode.CurrentLayer;
    public bool ContiguousFill { get; set; } = true;
    public double AreaScaling { get; set; }

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ClickInput click) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        int docW = ctx.Document.Width;
        int docH = ctx.Document.Height;
        var docX = (int)click.Point.X;
        var docY = (int)click.Point.Y;
        if ((uint)docX >= (uint)docW || (uint)docY >= (uint)docH) return;

        var op = SelectOpHelper.ResolveForSelection(Operation, ctx);
        var before = ctx.Selection.CaptureSnapshot();
        int areaScaling = (int)Math.Round(Math.Clamp(AreaScaling, -20, 20));

        if (FillReference != FillReferenceMode.CurrentLayer)
        {
            var buf = FloodFillReference.BuildCompositeBuffer(ctx, FillReference, docW, docH);
            ctx.Selection.SetFromFloodFillBuffer(buf, docX, docY, Tolerance, op,
                ContiguousFill, areaScaling);
        }
        else
        {
            int localX = docX - layer.OffsetX;
            int localY = docY - layer.OffsetY;
            if ((uint)localX >= (uint)layer.Width || (uint)localY >= (uint)layer.Height)
                return;

            ctx.Selection.SetFromFloodFillLayerLocal(
                layer.ActivePixels,
                localX,
                localY,
                layer.OffsetX,
                layer.OffsetY,
                layer.Width,
                layer.Height,
                Tolerance,
                op,
                ContiguousFill,
                areaScaling);
        }

        ctx.CommitSelectionMutation(before);
    }
}
