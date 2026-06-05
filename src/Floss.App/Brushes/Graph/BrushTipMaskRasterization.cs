using System;
using Floss.App.Brushes.Engine;
using Floss.App.Brushes.Tips;
using SkiaSharp;

namespace Floss.App.Brushes.Graph;

/// <summary>
/// Krita-aligned policy: procedural / node-graph tips rasterize masks at full stamp
/// resolution, then bake dabs. Per-pixel UV graph evaluation is not used for primitives.
/// </summary>
public static class BrushTipMaskRasterization
{
    public const int MaxMaskRasterDimension = 4096;

    public static int StrokeBaseMaskSize(double brushSizePx)
        => Math.Max(1, Math.Min(MaxMaskRasterDimension, (int)Math.Ceiling(brushSizePx)));

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
    /// True when the graph is a plain round dab (Technical Pen style) — LUT matches Drawpile/Krita default circle.
    /// Ellipse, soft round, rectangle, noise shapes keep analytic graph rasterization at full size.
    /// </summary>
    public static bool IsClassicCircleGraph(BrushTipNodeGraph graph)
    {
        if (graph.BuiltInShape is BrushTipShape.Circle)
            return true;

        var output = graph.Nodes.Find(n => n.Id == graph.OutputNodeId);
        if (output is not { Kind: BrushTipNodeKind.Output, Inputs.Count: 1 })
            return false;

        var node = graph.Nodes.Find(n => n.Id == output.Inputs[0]);
        if (node is not { Kind: BrushTipNodeKind.Circle, Inputs.Count: 0 })
            return false;

        return MathF.Abs(node.Width - 1f) < 0.02f
            && MathF.Abs(node.Height - 1f) < 0.02f
            && MathF.Abs(node.X - 0.5f) < 0.02f
            && MathF.Abs(node.Y - 0.5f) < 0.02f
            && MathF.Abs(node.RotationDegrees) < 0.5f;
    }
}
