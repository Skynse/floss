using System;
using System.Collections.Generic;
using Floss.App.Document;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Creates a selection from a flood-fill at a click point (magic wand).
public sealed class MagicWandOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;
    public double Tolerance { get; set; } = 0.1;
    public SelectOp Operation { get; set; } = SelectOp.Replace;

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ClickInput click) return;

        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup) return;

        var cx = (int)click.Point.X - layer.OffsetX;
        var cy = (int)click.Point.Y - layer.OffsetY;

        layer.Pixels.GetPixel(cx, cy, out byte refB, out byte refG, out byte refR, out byte refA);

        // Create a mask-based selection (single flood fill in SetFromFloodFill).
        var before = ctx.Selection.CaptureSnapshot();
        ctx.Selection.SetFromFloodFill(layer.Pixels, cx, cy, layer.OffsetX, layer.OffsetY, Tolerance, Operation);
        ctx.CommitSelectionMutation(before);
    }
}
