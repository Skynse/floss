using SkiaSharp;

namespace Floss.App.Brushes;

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

public sealed class ProceduralBrushTip : IBrushTip
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

    public SKBitmap GenerateMask(int baseSize, float hardness)
        => _graphTip.GenerateMask(baseSize, hardness);
}
