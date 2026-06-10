using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Floss.App.Brushes.Tips;

public enum BrushTipShape
{
    Circle,
    SoftRound,
    Flat,
    Ellipse,
    Rectangle,
    Chalk,
    Bristle,
    Scatter,
}

public sealed class ProceduralBrushTip : IBrushTip, IDisposable
{
    private readonly NodeBrushTip _graphTip;

    public ProceduralBrushTip(BrushTipShape shape = BrushTipShape.Circle, float aspectRatio = 1.0f)
    {
        Shape = shape;
        AspectRatio = aspectRatio;
        Graph = BrushTipNodeGraph.FromProceduralShape(shape, aspectRatio);
        _graphTip = new NodeBrushTip(Graph);
    }

    public ProceduralBrushTip(BrushTipNodeGraph graph)
    {
        Graph = graph.DeepClone();
        Shape = graph.BuiltInShape ?? BrushTipShape.Circle;
        AspectRatio = graph.BuiltInAspectRatio;
        _graphTip = new NodeBrushTip(Graph);
    }

    public BrushTipShape Shape { get; }
    public float AspectRatio { get; }
    public BrushTipNodeGraph Graph { get; }

    public void Dispose() => _graphTip.Dispose();

    public void BindMaterialTips(IReadOnlyList<BrushTipData> materialTips)
        => _graphTip.BindMaterialTips(materialTips);

    public SKBitmap GenerateMask(int baseSize, float hardness)
        => _graphTip.GenerateMask(baseSize, hardness);

    public SKBitmap? GenerateColorStamp(int baseSize)
        => _graphTip.GenerateColorStamp(baseSize);
}
