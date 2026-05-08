using System;
using System.Linq;
using Floss.App.Document;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes.Output;

// Creates a selection from a polygon or rectangle.
public sealed class SelectionAreaOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;
    public SelectOp Operation { get; set; } = SelectOp.Replace;

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (ctx.Selection == null) return;
        var before = ctx.Selection.CaptureSnapshot();
        switch (input)
        {
            case PolygonInput poly when poly.SmoothedPoints.Count >= 3:
                {
                    var points = poly.SmoothedPoints.Select(p => new SKPoint((float)p.X, (float)p.Y)).ToList();
                    ctx.Selection.SetFromPolygon(points, Operation);
                    ctx.CommitSelectionMutation(before);
                    break;
                }
            case RectInput rect:
                {
                    var x = (int)Math.Min(rect.Start.X, rect.End.X);
                    var y = (int)Math.Min(rect.Start.Y, rect.End.Y);
                    var w = (int)Math.Abs(rect.End.X - rect.Start.X);
                    var h = (int)Math.Abs(rect.End.Y - rect.Start.Y);
                    ctx.Selection.SetFromRect(x, y, w, h, Operation);
                    ctx.CommitSelectionMutation(before);
                    break;
                }
        }
    }
}
