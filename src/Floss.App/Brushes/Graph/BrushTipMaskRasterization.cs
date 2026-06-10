using System;
using Floss.App.Brushes.Curves;
using Floss.App.Brushes.Engine;
using Floss.App.Brushes.Tips;
using SkiaSharp;

namespace Floss.App.Brushes.Graph;

/// <summary>
/// -aligned policy: procedural / node-graph tips rasterize masks at full stamp
/// resolution, then bake dabs. Per-pixel UV graph evaluation is not used for primitives.
/// </summary>
public static class BrushTipMaskRasterization
{
    public const int MaxMaskRasterDimension = 4096;

    public static int StrokeBaseMaskSize(double brushSizePx)
        => Math.Max(1, Math.Min(MaxMaskRasterDimension, (int)Math.Ceiling(brushSizePx)));

    /// <summary>
    /// Upper bound on stamp diameter for mask pre-generation at stroke start (: mask matches dab size).
    /// </summary>
    public static int StrokePeakMaskSize(BrushPreset brush)
    {
        var peak = brush.Size * Math.Max(1.0, brush.Dynamics.Size.MaxOutput);
        foreach (var graph in brush.ParameterGraphs)
        {
            if (graph.Target != BrushParameterTarget.Size || graph.Validate().Count > 0)
                continue;
            var hi = new StrokePoint(0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            peak = Math.Max(peak, brush.Size * Math.Max(1.0, graph.Evaluate(in hi, 1f)));
        }

        return StrokeBaseMaskSize(peak);
    }

    /// <summary>
    /// Mask raster size for one dab — always matches the stamp diameter (: no upscale from a smaller template).
    /// </summary>
    public static int MaskResolutionForStamp(double stampDiameterPx, BrushPreset brush)
        => StrokeBaseMaskSize(stampDiameterPx);

    /// <summary>
    /// Tips that should pre-rasterize <see cref="IBrushTip.GenerateMask"/> at
    /// <see cref="StrokeBaseMaskSize"/> and composite via cached dabs — not live graph UV eval.
    /// </summary>
    public static bool PrefersMaskRasterization(IBrushTip tip)
        => tip switch
        {
            ProceduralBrushTip => true,
            NodeBrushTip { IsDirectImageSampler: true } => false,
            NodeBrushTip => true,
            _ => false
        };

    public static bool TryCreateClassicCircleMask(
        BrushTipNodeGraph graph,
        int baseSize,
        float brushHardness,
        out SKBitmap mask)
    {
        mask = null!;
        if (!IsClassicCircleGraph(graph))
            return false;

        var h = Math.Clamp((int)MathF.Round(brushHardness * 100f), 0, 100);
        mask = ClassicBrushLut.ToAlpha8Bitmap(Math.Max(1, baseSize), h);
        return true;
    }

    /// <summary>
    /// True when the graph is a plain round dab (Technical Pen style) — LUT matches /default circle.
    /// Ellipse, soft round, rectangle, noise shapes keep analytic graph rasterization at full size.
    /// </summary>
    public static bool IsClassicCircleGraph(BrushTipNodeGraph graph)
    {
        if (graph.BuiltInShape is BrushTipShape.Circle)
            return true;

        // Soft round, ellipse, chalk, etc. must keep analytic rasterization (node hardness/aspect).
        if (graph.BuiltInShape is not null)
            return false;

        var output = graph.Nodes.Find(n => n.Id == graph.OutputNodeId);
        if (output is not { Kind: BrushTipNodeKind.Output, Inputs.Count: 1 })
            return false;

        var node = graph.Nodes.Find(n => n.Id == output.Inputs[0]);
        if (node is not { Kind: BrushTipNodeKind.Circle, Inputs.Count: 0 })
            return false;

        return MathF.Abs(node.Radius - 0.49f) < 0.02f
            && MathF.Abs(node.Hardness - 0.72f) < 0.05f
            && MathF.Abs(node.Width - 1f) < 0.02f
            && MathF.Abs(node.Height - 1f) < 0.02f
            && MathF.Abs(node.X - 0.5f) < 0.02f
            && MathF.Abs(node.Y - 0.5f) < 0.02f
            && MathF.Abs(node.RotationDegrees) < 0.5f;
    }
}
