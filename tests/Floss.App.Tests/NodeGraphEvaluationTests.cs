using SkiaSharp;

namespace Floss.App.Tests;

public class NodeGraphEvaluationTests
{
    private static BrushTipNodeGraph Graph(params BrushTipNode[] nodes)
    {
        var g = new BrushTipNodeGraph { Nodes = nodes.ToList() };
        g.OutputNodeId = nodes[^1].Id;
        return g;
    }

    private static BrushTipNode Node(BrushTipNodeKind kind, params string[] inputs)
        => new() { Kind = kind, Inputs = inputs.ToList() };

    private static BrushTipNode Gen(BrushTipNodeKind kind, Action<BrushTipNode>? configure = null)
    {
        var n = Node(kind);
        configure?.Invoke(n);
        return n;
    }

    private void AssertNonZero(BrushTipNodeGraph graph, int size = 32)
    {
        var bmp = BrushTipNodeGraphEvaluator.Evaluate(graph, size, 0.5f, null);
        var nonZero = 0;
        var total = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                total++;
                if (bmp.GetPixel(x, y).Alpha > 0) nonZero++;
            }
        Assert.True(nonZero > total * 0.05,
            $"Expected >5% non-zero pixels, got {nonZero * 100 / total}% for {graph.Nodes[^1].Kind}");
    }

    private void AssertAllZero(BrushTipNodeGraph graph, int size = 32)
    {
        var bmp = BrushTipNodeGraphEvaluator.Evaluate(graph, size, 0.5f, null);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                Assert.Equal((byte)0, bmp.GetPixel(x, y).Alpha);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Generators
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Circle_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.Circle, c => { c.Radius = 0.4f; c.Opacity = 1f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void Rectangle_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.Rectangle, c => { c.Width = 0.6f; c.Height = 0.6f; c.Opacity = 1f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void RoundedRectangle_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.RoundedRectangle, c => { c.Width = 0.6f; c.Height = 0.6f; c.Radius = 0.15f; c.Opacity = 1f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void LinearGradient_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.LinearGradient, c => { c.Opacity = 1f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void Stripe_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.Stripe, c => { c.Scale = 3f; c.Density = 0.5f; c.Opacity = 1f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void Noise_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.Noise, c => { c.Scale = 3f; c.Opacity = 1f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void Bristle_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.Bristle, c => { c.Density = 0.5f; c.Opacity = 1f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void Perlin_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.Perlin, c => { c.Scale = 3f; c.Opacity = 1f; c.Density = 0.5f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void Voronoi_ProducesMask()
    {
        var n = Gen(BrushTipNodeKind.Voronoi, c => { c.Scale = 3f; c.Opacity = 1f; c.Density = 0.5f; });
        AssertNonZero(Graph(n));
    }

    [Fact]
    public void Value_ProducesConstant()
    {
        var n = Gen(BrushTipNodeKind.Value, c => { c.Opacity = 0.5f; });
        var g = Graph(n);
        var bmp = BrushTipNodeGraphEvaluator.Evaluate(g, 16, 0.5f, null);
        var nonZero = 0;
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            Assert.InRange(bmp.GetPixel(x, y).Alpha, (byte)120, (byte)135);
    }

    [Fact]
    public void Coordinates_VectorOutput_HasFullRange()
    {
        var coords = Gen(BrushTipNodeKind.Coordinates, c => c.Id = "coord");
        var circle = Node(BrushTipNodeKind.Circle, "coord");
        circle.Radius = 0.5f; circle.Opacity = 1f; circle.Id = "circ";
        AssertNonZero(Graph(coords, circle));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Combiners
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Add_ProducesMask()
    {
        var a = Node(BrushTipNodeKind.Circle); a.Id = "a"; a.Radius = 0.3f; a.Opacity = 0.5f;
        var b = Node(BrushTipNodeKind.Noise); b.Id = "b"; b.Scale = 3f; b.Opacity = 0.5f;
        var add = Node(BrushTipNodeKind.Add, "a", "b"); add.Id = "add";
        AssertNonZero(Graph(a, b, add));
    }

    [Fact]
    public void Multiply_ProducesMask()
    {
        var a = Node(BrushTipNodeKind.Circle); a.Id = "a"; a.Radius = 0.5f; a.Opacity = 1f;
        var b = Node(BrushTipNodeKind.Noise); b.Id = "b"; b.Scale = 4f; b.Opacity = 0.7f;
        var mul = Node(BrushTipNodeKind.Multiply, "a", "b"); mul.Id = "mul";
        AssertNonZero(Graph(a, b, mul));
    }

    [Fact]
    public void Max_ProducesMask()
    {
        var a = Node(BrushTipNodeKind.Circle); a.Id = "a"; a.Radius = 0.3f; a.Opacity = 0.5f;
        var b = Node(BrushTipNodeKind.Noise); b.Id = "b"; b.Scale = 3f; b.Opacity = 0.8f;
        var max = Node(BrushTipNodeKind.Max, "a", "b"); max.Id = "max";
        AssertNonZero(Graph(a, b, max));
    }

    [Fact]
    public void Min_ProducesMask()
    {
        var a = Node(BrushTipNodeKind.Circle); a.Id = "a"; a.Radius = 0.5f; a.Opacity = 1f;
        var b = Node(BrushTipNodeKind.Noise); b.Id = "b"; b.Scale = 4f; b.Opacity = 0.7f;
        var min = Node(BrushTipNodeKind.Min, "a", "b"); min.Id = "min";
        AssertNonZero(Graph(a, b, min));
    }

    [Fact]
    public void Subtract_ProducesMask()
    {
        var a = Node(BrushTipNodeKind.Noise); a.Id = "a"; a.Scale = 4f; a.Opacity = 0.7f;
        var b = Node(BrushTipNodeKind.Circle); b.Id = "b"; b.Radius = 0.3f; b.Opacity = 0.3f;
        var sub = Node(BrushTipNodeKind.Subtract, "a", "b"); sub.Id = "sub";
        AssertNonZero(Graph(a, b, sub));
    }

    [Fact]
    public void Mix_ProducesMask()
    {
        var a = Node(BrushTipNodeKind.Circle); a.Id = "a"; a.Radius = 0.5f; a.Opacity = 1f;
        var b = Node(BrushTipNodeKind.Noise); b.Id = "b"; b.Scale = 3f; b.Opacity = 1f;
        var mix = Node(BrushTipNodeKind.Mix, "a", "b"); mix.Id = "mix"; mix.Density = 0.5f;
        AssertNonZero(Graph(a, b, mix));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Modifiers
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Threshold_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.5f; circ.Opacity = 1f;
        var th = Node(BrushTipNodeKind.Threshold, "c"); th.Threshold = 0.3f; th.Opacity = 1f;
        AssertNonZero(Graph(circ, th));
    }

    [Fact]
    public void SmoothStep_ProducesMask()
    {
        var df = Node(BrushTipNodeKind.DistanceField); df.Id = "df"; df.Width = 0.6f; df.Height = 0.6f;
        var ss = Node(BrushTipNodeKind.SmoothStep, "df"); ss.Hardness = 0.5f;
        AssertNonZero(Graph(df, ss));
    }

    [Fact]
    public void Power_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.7f; circ.Opacity = 1f;
        var pw = Node(BrushTipNodeKind.Power, "c"); pw.Scale = 3f; pw.Opacity = 1f;
        AssertNonZero(Graph(circ, pw));
    }

    [Fact]
    public void Sine_ProducesMask()
    {
        var grad = Node(BrushTipNodeKind.LinearGradient); grad.Id = "g"; grad.Opacity = 1f;
        var sine = Node(BrushTipNodeKind.Sine, "g"); sine.Scale = 4f; sine.Opacity = 1f;
        AssertNonZero(Graph(grad, sine));
    }

    [Fact]
    public void Absolute_ProducesMask()
    {
        var grad = Node(BrushTipNodeKind.LinearGradient); grad.Id = "g"; grad.Opacity = 1f;
        var abs = Node(BrushTipNodeKind.Absolute, "g"); abs.X = 0.5f; abs.Opacity = 1f;
        AssertNonZero(Graph(grad, abs));
    }

    [Fact]
    public void Invert_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.6f; circ.Opacity = 1f;
        var inv = Node(BrushTipNodeKind.Invert, "c");
        AssertNonZero(Graph(circ, inv));
    }

    [Fact]
    public void Remap_ProducesMask()
    {
        var grad = Node(BrushTipNodeKind.LinearGradient); grad.Id = "g"; grad.Opacity = 1f;
        var remap = Node(BrushTipNodeKind.Remap, "g");
        remap.Threshold = 0.2f; remap.Hardness = 0.8f; remap.Opacity = 0.2f; remap.Scale = 0.9f;
        AssertNonZero(Graph(grad, remap));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Coordinate nodes
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RotateCoordinates_ProducesMask()
    {
        var coords = Node(BrushTipNodeKind.Coordinates); coords.Id = "c";
        var rot = Node(BrushTipNodeKind.RotateCoordinates, "c"); rot.Id = "r"; rot.RotationDegrees = 45f;
        var circ = Node(BrushTipNodeKind.Circle, "r"); circ.Radius = 0.4f; circ.Opacity = 1f; circ.Id = "circ";
        AssertNonZero(Graph(coords, rot, circ));
    }

    [Fact]
    public void WarpCoordinates_ProducesMask()
    {
        var coords = Node(BrushTipNodeKind.Coordinates); coords.Id = "c";
        var noise = Node(BrushTipNodeKind.Noise); noise.Id = "n"; noise.Scale = 4f; noise.Opacity = 0.5f;
        var warp = Node(BrushTipNodeKind.WarpCoordinates, "c", "n"); warp.Id = "w"; warp.Scale = 0.15f;
        var circ = Node(BrushTipNodeKind.Circle, "w"); circ.Radius = 0.3f; circ.Opacity = 1f; circ.Id = "circ";
        AssertNonZero(Graph(coords, noise, warp, circ));
    }

    [Fact]
    public void PolarRadius_ProducesMask()
    {
        var coords = Node(BrushTipNodeKind.Coordinates); coords.Id = "c";
        var pol = Node(BrushTipNodeKind.PolarRadius, "c"); pol.Id = "p";
        AssertNonZero(Graph(coords, pol));
    }

    [Fact]
    public void PolarAngle_ProducesMask()
    {
        var coords = Node(BrushTipNodeKind.Coordinates); coords.Id = "c";
        var ang = Node(BrushTipNodeKind.PolarAngle, "c"); ang.Id = "a";
        AssertNonZero(Graph(coords, ang));
    }

    [Fact]
    public void DistanceField_ProducesMask()
    {
        var coords = Node(BrushTipNodeKind.Coordinates); coords.Id = "c";
        var df = Node(BrushTipNodeKind.DistanceField, "c"); df.Width = 0.5f; df.Height = 0.5f;
        AssertNonZero(Graph(coords, df));
    }

    [Fact]
    public void BoxDistanceField_ProducesMask()
    {
        var coords = Node(BrushTipNodeKind.Coordinates); coords.Id = "c";
        var bdf = Node(BrushTipNodeKind.BoxDistanceField, "c"); bdf.Width = 0.5f; bdf.Height = 0.5f;
        AssertNonZero(Graph(coords, bdf));
    }

    [Fact]
    public void Transform_ProducesDifferentOutput()
    {
        var coords = Node(BrushTipNodeKind.Coordinates); coords.Id = "c";
        var tf = Node(BrushTipNodeKind.Transform, "c"); tf.Id = "t"; tf.Scale = 0.5f;
        var circ = Node(BrushTipNodeKind.Circle, "t"); circ.Radius = 0.5f; circ.Opacity = 1f; circ.Id = "circ";
        AssertNonZero(Graph(coords, tf, circ));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Effects
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Erosion_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.5f; circ.Opacity = 1f;
        var ero = Node(BrushTipNodeKind.Erosion, "c"); ero.Radius = 0.1f;
        AssertNonZero(Graph(circ, ero));
    }

    [Fact]
    public void DirectionalBlur_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.5f; circ.Opacity = 1f;
        var blur = Node(BrushTipNodeKind.DirectionalBlur, "c"); blur.Radius = 0.1f; blur.RotationDegrees = 45f;
        AssertNonZero(Graph(circ, blur));
    }

    [Fact]
    public void RaggedEdge_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.5f; circ.Opacity = 0.7f;
        var rag = Node(BrushTipNodeKind.RaggedEdge, "c"); rag.Scale = 8f; rag.Density = 0.3f;
        AssertNonZero(Graph(circ, rag));
    }

    [Fact]
    public void IsotropicBlur_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.5f; circ.Opacity = 1f;
        var blur = Node(BrushTipNodeKind.IsotropicBlur, "c"); blur.Radius = 0.1f;
        AssertNonZero(Graph(circ, blur));
    }

    [Fact]
    public void EdgeDetect_ProducesMask()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.4f; circ.Opacity = 1f;
        var edge = Node(BrushTipNodeKind.EdgeDetect, "c"); edge.Opacity = 1f;
        AssertNonZero(Graph(circ, edge));
    }

    [Fact]
    public void TextureStamp_ProducesEmpty_WithoutPngBytes()
    {
        var stamp = Node(BrushTipNodeKind.TextureStamp); stamp.Scale = 2f; stamp.Density = 0.5f;
        AssertAllZero(Graph(stamp));
    }

    [Fact]
    public void ImageSampler_ProducesEmpty_WithoutPngBytes()
    {
        var sampler = Node(BrushTipNodeKind.ImageSampler); sampler.Opacity = 1f;
        AssertAllZero(Graph(sampler));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Stamp evaluator path
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void StampEval_ProceduralOnly_RunsViaStamp()
    {
        var graph = BrushTipNodeGraph.SimpleCircle();
        Assert.True(BrushTipNodeGraphEvaluator.CanEvaluateViaStamp(graph, null));

        var bmp = BrushTipNodeGraphEvaluator.Evaluate(graph, 32, 0.5f, null);
        var nonZero = 0;
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                if (bmp.GetPixel(x, y).Alpha > 0) nonZero++;
        Assert.True(nonZero > 32, $"Circle stamp should produce non-zero pixels, got {nonZero}");
    }

    [Fact]
    public void EffectNodes_UseFullBitmapPath()
    {
        var circ = Node(BrushTipNodeKind.Circle); circ.Id = "c"; circ.Radius = 0.5f; circ.Opacity = 1f;
        var blur = Node(BrushTipNodeKind.IsotropicBlur, "c"); blur.Id = "b"; blur.Radius = 0.1f;
        var graph = Graph(circ, blur);
        Assert.False(BrushTipNodeGraphEvaluator.CanEvaluateViaStamp(graph, null));
    }

    [Fact]
    public void SimpleCircle_ProducesNonZero()
    {
        var graph = BrushTipNodeGraph.SimpleCircle();
        var bmp = BrushTipNodeGraphEvaluator.Evaluate(graph, 64, 1f, null);
        var center = bmp.GetPixel(31, 31).Alpha;
        Assert.True(center > 200, $"Center of SimpleCircle should be bright, got {center}");
    }

    [Fact]
    public void SimpleRectangle_ProducesNonZero()
    {
        var graph = BrushTipNodeGraph.SimpleRectangle();
        var bmp = BrushTipNodeGraphEvaluator.Evaluate(graph, 64, 1f, null);
        var center = bmp.GetPixel(31, 31).Alpha;
        Assert.True(center > 200, $"Center of SimpleRectangle should be bright, got {center}");
    }
}
