using System;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Creates a selection from a flood-fill at a click point (magic wand).
public sealed class MagicWandOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;
    public double Tolerance { get; set; } = 0.1;
    public SelectOp Operation { get; set; } = SelectOp.Replace;
    public FillReferenceMode FillReference { get; set; } = FillReferenceMode.CurrentLayer;
    public bool ContiguousFill { get; set; } = true;
    public double AreaScaling { get; set; }

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ClickInput click) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup) return;

        var docX = (int)click.Point.X;
        var docY = (int)click.Point.Y;
        var op = SelectOpHelper.ResolveForSelection(Operation, ctx);
        var before = ctx.Selection.CaptureSnapshot();
        int areaScaling = (int)Math.Round(Math.Clamp(AreaScaling, -20, 20));

        if (FillReference != FillReferenceMode.CurrentLayer)
        {
            var buf = BuildReferenceBuffer(ctx, FillReference);
            ctx.Selection.SetFromFloodFillBuffer(buf, docX, docY, Tolerance, op,
                ContiguousFill, areaScaling);
        }
        else
        {
            ctx.Selection.SetFromFloodFill(layer.Pixels,
                docX - layer.OffsetX, docY - layer.OffsetY,
                layer.OffsetX, layer.OffsetY, Tolerance, op,
                ContiguousFill, areaScaling);
        }

        ctx.CommitSelectionMutation(before);
    }

    private static byte[] BuildReferenceBuffer(ToolContext ctx, FillReferenceMode mode)
    {
        int w = ctx.Document.Width, h = ctx.Document.Height;
        var buf = new byte[w * h * 4];
        foreach (var l in ctx.Document.Layers)
        {
            if (!l.IsVisible || l.IsGroup) continue;
            if (mode == FillReferenceMode.ReferenceLayers && !l.IsReference) continue;
            l.Pixels.BlendOnto(buf, w, h, l.Opacity);
        }
        return buf;
    }
}
