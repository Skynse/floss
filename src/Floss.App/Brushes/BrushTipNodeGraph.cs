using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Floss.App.Brushes;

public enum BrushTipNodeKind
{
    Output,
    Coordinates,
    RotateCoordinates,
    WarpCoordinates,
    PolarRadius,
    PolarAngle,
    Value,
    DistanceField,
    BoxDistanceField,
    Circle,
    Rectangle,
    RoundedRectangle,
    LinearGradient,
    Stripe,
    ImageSampler,
    Noise,
    Bristle,
    Add,
    Multiply,
    Max,
    Min,
    Subtract,
    Threshold,
    SmoothStep,
    Power,
    Sine,
    Absolute,
    Mix,
    Invert,
}

public sealed class BrushTipNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BrushTipNodeKind Kind { get; set; } = BrushTipNodeKind.Circle;
    public List<string> Inputs { get; set; } = [];

    public float X { get; set; } = 0.5f;
    public float Y { get; set; } = 0.5f;
    public float Width { get; set; } = 1.0f;
    public float Height { get; set; } = 1.0f;
    public float Radius { get; set; } = 0.5f;
    public float RotationDegrees { get; set; } = 0f;
    public float Hardness { get; set; } = 1.0f;
    public float Opacity { get; set; } = 1.0f;
    public float Density { get; set; } = 0.75f;
    public float Scale { get; set; } = 1.0f;
    public float Threshold { get; set; } = 0.5f;
    public int Seed { get; set; } = 1;
    /// <summary>Reference into <see cref="BrushPreset.Tips"/> — PNG bytes live in the library, not here.</summary>
    public string? MaterialTipId { get; set; }
    /// <summary>Legacy embedded bytes; cleared once bound to <see cref="MaterialTipId"/>.</summary>
    public byte[] PngBytes { get; set; } = [];

    public BrushTipNode DeepClone() => new()
    {
        Id = Id,
        Kind = Kind,
        Inputs = Inputs.ToList(),
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Radius = Radius,
        RotationDegrees = RotationDegrees,
        Hardness = Hardness,
        Opacity = Opacity,
        Density = Density,
        Scale = Scale,
        Threshold = Threshold,
        Seed = Seed,
        MaterialTipId = MaterialTipId,
        PngBytes = PngBytes.ToArray()
    };
}

public sealed class BrushTipNodeGraph
{
    public int Version { get; set; } = 1;
    public BrushTipShape? BuiltInShape { get; set; }
    public float BuiltInAspectRatio { get; set; } = 1.0f;
    public string OutputNodeId { get; set; } = "output";
    public List<BrushTipNode> Nodes { get; set; } =
    [
        new BrushTipNode { Id = "circle", Kind = BrushTipNodeKind.Circle },
        new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["circle"] }
    ];

    public BrushTipNodeGraph DeepClone() => new()
    {
        Version = Version,
        BuiltInShape = BuiltInShape,
        BuiltInAspectRatio = BuiltInAspectRatio,
        OutputNodeId = OutputNodeId,
        Nodes = Nodes.Select(n => n.DeepClone()).ToList()
    };

    public bool ContainsImageSampler(IReadOnlyList<BrushTipData>? materialTips = null)
        => Nodes.Exists(n => BrushMaterialTips.HasResolvedSampler(n, materialTips));

    public IReadOnlyList<string> Validate(int maxNodes = 128)
    {
        var errors = new List<string>();
        if (Nodes.Count == 0)
        {
            errors.Add("Graph has no nodes.");
            return errors;
        }
        if (Nodes.Count > maxNodes)
            errors.Add($"Graph has {Nodes.Count} nodes; maximum is {maxNodes}.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                errors.Add("Graph contains a node with an empty id.");
            else if (!ids.Add(node.Id))
                errors.Add($"Graph contains duplicate node id '{node.Id}'.");
        }
        if (!ids.Contains(OutputNodeId))
            errors.Add($"Graph output node '{OutputNodeId}' does not exist.");

        foreach (var node in Nodes)
        {
            foreach (var input in node.Inputs)
                if (!ids.Contains(input))
                    errors.Add($"Node '{node.Id}' references missing input '{input}'.");
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (ids.Contains(OutputNodeId))
            Visit(OutputNodeId);
        return errors;

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
            {
                errors.Add($"Graph contains a cycle at node '{id}'.");
                return;
            }
            var node = Nodes.FirstOrDefault(n => n.Id == id);
            if (node != null)
                foreach (var input in node.Inputs)
                    if (ids.Contains(input))
                        Visit(input);
            visiting.Remove(id);
            visited.Add(id);
        }
    }

    public string CacheKey()
    {
        var sb = new StringBuilder();
        sb.Append(Version.ToString(CultureInfo.InvariantCulture))
            .Append('|').Append(BuiltInShape?.ToString() ?? "")
            .Append('|').Append(F(BuiltInAspectRatio))
            .Append('|').Append(OutputNodeId);
        foreach (var node in Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            sb.Append('|').Append(node.Id)
                .Append(':').Append((int)node.Kind)
                .Append(':').Append(string.Join(',', node.Inputs))
                .Append(':').Append(F(node.X))
                .Append(':').Append(F(node.Y))
                .Append(':').Append(F(node.Width))
                .Append(':').Append(F(node.Height))
                .Append(':').Append(F(node.Radius))
                .Append(':').Append(F(node.RotationDegrees))
                .Append(':').Append(F(node.Hardness))
                .Append(':').Append(F(node.Opacity))
                .Append(':').Append(F(node.Density))
                .Append(':').Append(F(node.Scale))
                .Append(':').Append(F(node.Threshold))
                .Append(':').Append(node.Seed)
                .Append(':').Append(node.MaterialTipId ?? "")
                .Append(':').Append(node.PngBytes.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(node.PngBytes)));
        }
        return sb.ToString();

        static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static BrushTipNodeGraph PrimitiveCircle(string id, float radius, float hardness, float width = 1f, float height = 1f)
        => new()
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode
                {
                    Id = id,
                    Kind = BrushTipNodeKind.Circle,
                    Radius = radius,
                    Hardness = hardness,
                    Width = width,
                    Height = height,
                    Opacity = 1f
                },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = [id] }
            ]
        };

    private static BrushTipNodeGraph PrimitiveRectangle(string id, float width, float height, float hardness)
        => new()
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode
                {
                    Id = id,
                    Kind = BrushTipNodeKind.Rectangle,
                    Width = width,
                    Height = height,
                    Hardness = hardness,
                    Opacity = 1f
                },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = [id] }
            ]
        };

    private static BrushTipNodeGraph PrimitiveRoundedRectangle(string id, float width, float height, float cornerRadius, float hardness)
        => new()
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode
                {
                    Id = id,
                    Kind = BrushTipNodeKind.RoundedRectangle,
                    Width = width,
                    Height = height,
                    Radius = cornerRadius,
                    Hardness = hardness,
                    Opacity = 1f
                },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = [id] }
            ]
        };

    public static BrushTipNodeGraph SimpleCircle(float radius = 0.49f, float hardness = 0.72f)
        => PrimitiveCircle("circle", radius, hardness);

    public static BrushTipNodeGraph SimpleRectangle(float width = 1f, float height = 1f, float hardness = 0.85f)
        => PrimitiveRectangle("rect", width, height, hardness);

    public static BrushTipNodeGraph SimpleRoundedRectangle(float width = 1f, float height = 1f, float cornerRadius = 0.12f, float hardness = 0.85f)
        => PrimitiveRoundedRectangle("round-rect", width, height, cornerRadius, hardness);

    public static BrushTipNodeGraph BristleRound()
        => new()
        {
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode
                {
                    Id = "round",
                    Kind = BrushTipNodeKind.Circle,
                    Radius = 0.48f,
                    Hardness = 0.85f,
                    Opacity = 0.8f
                },
                new BrushTipNode
                {
                    Id = "bristle",
                    Kind = BrushTipNodeKind.Bristle,
                    Width = 0.92f,
                    Height = 0.7f,
                    Density = 0.55f,
                    Hardness = 0.95f,
                    Seed = 42
                },
                new BrushTipNode
                {
                    Id = "mix",
                    Kind = BrushTipNodeKind.Multiply,
                    Inputs = ["round", "bristle"]
                },
                new BrushTipNode
                {
                    Id = "output",
                    Kind = BrushTipNodeKind.Output,
                    Inputs = ["mix"]
                }
            ]
        };

    public static BrushTipNodeGraph FromProceduralShape(BrushTipShape shape, float aspectRatio = 1.0f)
    {
        var (width, height) = AspectSize(shape == BrushTipShape.Flat ? 4.5f : aspectRatio);
        var graph = shape switch
        {
            BrushTipShape.SoftRound => PrimitiveCircle("soft-round", radius: 0.49f, hardness: 0.32f, width, height),
            BrushTipShape.Ellipse => PrimitiveCircle("ellipse", radius: 0.49f, hardness: 0.72f, width, height),
            BrushTipShape.Flat => PrimitiveRectangle("flat", width: 0.94f, height: 0.22f, hardness: 0.85f),
            BrushTipShape.Rectangle => PrimitiveRectangle("rect", width, height, hardness: 0.85f),
            BrushTipShape.Chalk => MaskedNoise("chalk", density: 0.92f, scale: 2.6f, threshold: 0.08f, seed: 31),
            BrushTipShape.Bristle => BristleRound(),
            BrushTipShape.Scatter => MaskedNoise("scatter", density: 0.38f, scale: 1.35f, threshold: 0.12f, seed: 77),
            _ => PrimitiveCircle("round", radius: 0.49f, hardness: 0.72f)
        };
        graph.BuiltInShape = shape;
        graph.BuiltInAspectRatio = aspectRatio;
        return graph;

        static (float Width, float Height) AspectSize(float aspect)
        {
            aspect = Math.Clamp(aspect, 0.05f, 20f);
            return aspect >= 1f ? (1f, 1f / aspect) : (aspect, 1f);
        }

        static BrushTipNodeGraph MaskedNoise(string prefix, float density, float scale, float threshold, int seed)
            => new()
            {
                OutputNodeId = "output",
                Nodes =
                [
                    new BrushTipNode
                    {
                        Id = $"{prefix}-mask",
                        Kind = BrushTipNodeKind.Circle,
                        Radius = 0.49f,
                        Hardness = 0.72f
                    },
                    new BrushTipNode
                    {
                        Id = $"{prefix}-noise",
                        Kind = BrushTipNodeKind.Noise,
                        Density = density,
                        Scale = scale,
                        Seed = seed
                    },
                    new BrushTipNode
                    {
                        Id = $"{prefix}-grain",
                        Kind = BrushTipNodeKind.Multiply,
                        Inputs = [$"{prefix}-mask", $"{prefix}-noise"]
                    },
                    new BrushTipNode
                    {
                        Id = $"{prefix}-cut",
                        Kind = BrushTipNodeKind.Threshold,
                        Inputs = [$"{prefix}-grain"],
                        Threshold = threshold
                    },
                    new BrushTipNode
                    {
                        Id = "output",
                        Kind = BrushTipNodeKind.Output,
                        Inputs = [$"{prefix}-cut"]
                    }
                ]
            };
    }

    public static BrushTipNodeGraph FromImageTip(byte[] pngBytes, string? materialTipId = null)
        => new()
        {
            BuiltInShape = null,
            BuiltInAspectRatio = 1.0f,
            OutputNodeId = "output",
            Nodes =
            [
                new BrushTipNode
                {
                    Id = "image",
                    Kind = BrushTipNodeKind.ImageSampler,
                    MaterialTipId = materialTipId,
                    PngBytes = string.IsNullOrEmpty(materialTipId) ? pngBytes.ToArray() : [],
                    Opacity = 1.0f
                },
                new BrushTipNode { Id = "output", Kind = BrushTipNodeKind.Output, Inputs = ["image"] }
            ]
        };

    public bool TryGetDirectImageSampler(IReadOnlyList<BrushTipData>? materialTips, out byte[] pngBytes)
    {
        pngBytes = [];
        var output = Nodes.FirstOrDefault(n => n.Id == OutputNodeId);
        if (output == null)
            return false;

        BrushTipNode? image = null;
        if (output.Kind == BrushTipNodeKind.ImageSampler)
            image = output;
        else if (output.Kind == BrushTipNodeKind.Output && output.Inputs.Count > 0)
            image = Nodes.FirstOrDefault(n => n.Id == output.Inputs[0] && n.Kind == BrushTipNodeKind.ImageSampler);

        if (image == null)
            return false;

        var bytes = BrushMaterialTips.ResolveSamplerPng(image, materialTips);
        if (bytes.Length == 0)
            return false;

        pngBytes = bytes.ToArray();
        return true;
    }

    public bool TryGetDirectImageSampler(out byte[] pngBytes)
        => TryGetDirectImageSampler(null, out pngBytes);

    public ProceduralBrushTip? ToBuiltInProceduralTip()
        => BuiltInShape.HasValue
            ? new ProceduralBrushTip(this)
            : null;
}

public sealed class NodeBrushTip : IBrushTip, IDisposable
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<(int Size, int Hardness, string Graph, string Material), SKBitmap> _maskCache = [];
    private readonly string _graphCacheKey;
    private IReadOnlyList<BrushTipData> _materialTips = [];
    private readonly object _directTipLock = new();
    private ImageBrushTip? _directImageTip;
    private string _directImageKey = "";

    public NodeBrushTip(BrushTipNodeGraph graph)
    {
        Graph = graph.Validate().Count == 0 ? graph.DeepClone() : new BrushTipNodeGraph();
        _graphCacheKey = Graph.CacheKey();
    }

    public BrushTipNodeGraph Graph { get; }

    public void BindMaterialTips(IReadOnlyList<BrushTipData> materialTips)
    {
        // Never dispose caches here — the engine thread may hold live
        // references to masks from a prior lookup. Old cache entries with
        // outdated material keys simply won't match future lookups; they
        // accumulate harmlessly until Dispose().
        _materialTips = materialTips ?? [];
    }

    public bool IsDirectImageSampler => Graph.TryGetDirectImageSampler(_materialTips, out _);

    public bool HasColor
    {
        get
        {
            if (Graph.TryGetDirectImageSampler(_materialTips, out var bytes))
                return GetDirectImageTip(bytes).HasColor;
            return BrushTipNodeGraphEvaluator.GraphUsesColor(Graph, _materialTips);
        }
    }

    public SKBitmap? GenerateColorStamp(int baseSize)
    {
        if (!HasColor)
            return null;
        if (Graph.TryGetDirectImageSampler(_materialTips, out var directBytes))
            return GetDirectImageTip(directBytes).GenerateColorStamp(baseSize);
        return BrushTipNodeGraphEvaluator.EvaluateColor(Graph, baseSize, 1.0f, _materialTips);
    }

    public void Dispose()
    {
        lock (_directTipLock)
        {
            _directImageTip?.Dispose();
            _directImageTip = null;
            _directImageKey = "";
        }
        lock (_cacheLock)
        {
            foreach (var mask in _maskCache.Values)
                mask.Dispose();
            _maskCache.Clear();
        }
    }

    public SKBitmap GenerateMask(int baseSize, float hardness)
    {
        if (Graph.TryGetDirectImageSampler(_materialTips, out var directBytes) && directBytes.Length > 0)
            return GetDirectImageTip(directBytes).GenerateMask(baseSize, hardness);

        var size = Math.Max(1, baseSize);
        var h = Math.Clamp(hardness, 0.001f, 1.0f);
        var materialKey = BrushMaterialTips.LibraryCacheKey(_materialTips);
        var key = (size, QuantizeHardness(h), _graphCacheKey, materialKey);

        lock (_cacheLock)
            if (_maskCache.TryGetValue(key, out var cached))
                return cached;

        var mask = BrushTipNodeGraphEvaluator.Evaluate(Graph, size, h, _materialTips);
        lock (_cacheLock)
        {
            if (_maskCache.TryGetValue(key, out var existing))
            {
                mask.Dispose();
                return existing;
            }
            _maskCache[key] = mask;
            return mask;
        }
    }

    private static int QuantizeHardness(float hardness)
        => Math.Clamp((int)MathF.Round(Math.Clamp(hardness, 0.001f, 1f) * 255f), 0, 255);

    private ImageBrushTip GetDirectImageTip(byte[] pngBytes)
    {
        var key = Convert.ToHexString(SHA256.HashData(pngBytes));
        lock (_directTipLock)
        {
            if (_directImageTip != null && _directImageKey == key)
                return _directImageTip;

            // Don't dispose the old tip — the engine thread may still hold
            // live references to masks obtained from it. The old tip will
            // be disposed when this NodeBrushTip is disposed.
            _directImageKey = key;
            _directImageTip = new ImageBrushTip(pngBytes);
            return _directImageTip;
        }
    }
}

public static class BrushTipNodeGraphEvaluator
{
    public static bool CanEvaluateViaStamp(BrushTipNodeGraph graph, IReadOnlyList<BrushTipData>? materialTips = null)
        => !GraphUsesColor(graph, materialTips) && !graph.ContainsImageSampler(materialTips);

    public static unsafe SKBitmap Evaluate(BrushTipNodeGraph graph, int size, float brushHardness, IReadOnlyList<BrushTipData>? materialTips = null)
    {
        size = Math.Max(1, size);
        if (CanEvaluateViaStamp(graph, materialTips))
            return EvaluateViaStamp(graph, size, brushHardness, materialTips);

        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var cache = new Dictionary<string, NodeSample>(StringComparer.Ordinal);
        var outputId = nodes.ContainsKey(graph.OutputNodeId) ? graph.OutputNodeId : graph.Nodes.FirstOrDefault()?.Id;
        var data = outputId == null ? new float[size * size] : EvalScalar(outputId, []);

        var bitmap = NewAlpha8(size);
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        if (ptr == null)
            return bitmap;
        for (var y = 0; y < size; y++)
        {
            var row = ptr + y * bitmap.RowBytes;
            for (var x = 0; x < size; x++)
                row[x] = (byte)(Math.Clamp(data[y * size + x], 0f, 1f) * 255f + 0.5f);
        }
        return bitmap;

        NodeSample Eval(string id, HashSet<string> stack)
        {
            if (cache.TryGetValue(id, out var existing))
                return existing;
            if (!nodes.TryGetValue(id, out var node) || !stack.Add(id))
                return NodeSample.Zero(size);

            var result = node.Kind switch
            {
                BrushTipNodeKind.Output => EvalInput(node, 0, stack),
                BrushTipNodeKind.Coordinates => Coordinates(),
                BrushTipNodeKind.RotateCoordinates => RotateCoordinates(node, stack),
                BrushTipNodeKind.WarpCoordinates => WarpCoordinates(node, stack),
                BrushTipNodeKind.PolarRadius => Scalar(PolarRadius(node, stack)),
                BrushTipNodeKind.PolarAngle => Scalar(PolarAngle(node, stack)),
                BrushTipNodeKind.Value => Scalar(Constant(node)),
                BrushTipNodeKind.DistanceField => Scalar(DistanceField(node, stack)),
                BrushTipNodeKind.BoxDistanceField => Scalar(BoxDistanceField(node, stack)),
                BrushTipNodeKind.Circle => Scalar(Circle(node)),
                BrushTipNodeKind.Rectangle => Scalar(Rectangle(node)),
                BrushTipNodeKind.LinearGradient => Scalar(LinearGradient(node, stack)),
                BrushTipNodeKind.Stripe => Scalar(Stripe(node, stack)),
                BrushTipNodeKind.ImageSampler => Scalar(ImageSampler(node, materialTips)),
                BrushTipNodeKind.Noise => Scalar(Noise(node, stack)),
                BrushTipNodeKind.Bristle => Scalar(Bristle(node)),
                BrushTipNodeKind.Add => Scalar(Combine(node, stack, (a, b) => Math.Clamp(a + b, 0f, 1f))),
                BrushTipNodeKind.Multiply => Scalar(Combine(node, stack, (a, b) => a * b)),
                BrushTipNodeKind.Max => Scalar(Combine(node, stack, MathF.Max)),
                BrushTipNodeKind.Min => Scalar(Combine(node, stack, MathF.Min)),
                BrushTipNodeKind.Subtract => Scalar(Combine(node, stack, (a, b) => Math.Clamp(a - b, 0f, 1f))),
                BrushTipNodeKind.Threshold => Scalar(Threshold(node, stack)),
                BrushTipNodeKind.SmoothStep => Scalar(SmoothStep(node, stack)),
                BrushTipNodeKind.Power => Scalar(Unary(node, stack, v => MathF.Pow(Math.Clamp(v, 0f, 1f), Math.Clamp(node.Scale, 0.05f, 16f)) * Math.Clamp(node.Opacity, 0f, 1f))),
                BrushTipNodeKind.Sine => Scalar(Unary(node, stack, v => (MathF.Sin((v * Math.Clamp(node.Scale, 0.05f, 64f) + node.X) * MathF.PI * 2f) * 0.5f + 0.5f) * Math.Clamp(node.Opacity, 0f, 1f))),
                BrushTipNodeKind.Absolute => Scalar(Unary(node, stack, v => Math.Abs(v - node.X) * Math.Clamp(node.Opacity, 0f, 1f))),
                BrushTipNodeKind.Mix => Scalar(Mix(node, stack)),
                BrushTipNodeKind.Invert => Scalar(Invert(node, stack)),
                BrushTipNodeKind.RoundedRectangle => Scalar(RoundedRect(node)),
                _ => NodeSample.Zero(size)
            };
            stack.Remove(id);
            cache[id] = result;
            return result;
        }

        NodeSample EvalInput(BrushTipNode node, int index, HashSet<string> stack)
            => index < node.Inputs.Count ? Eval(node.Inputs[index], stack) : NodeSample.Zero(size);

        float[] EvalScalar(string id, HashSet<string> stack)
            => Eval(id, stack).AsScalar(size);

        float[] EvalScalarInput(BrushTipNode node, int index, HashSet<string> stack)
            => EvalInput(node, index, stack).AsScalar(size);

        (float[] X, float[] Y) EvalVectorInputOrCoordinates(BrushTipNode node, int index, HashSet<string> stack)
        {
            if (index < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[index]))
            {
                var sample = EvalInput(node, index, stack);
                if (sample.IsVector)
                    return (sample.A, sample.B!);
            }
            return DefaultCoordinates();
        }

        NodeSample Scalar(float[] values) => new(values, null);

        NodeSample Coordinates()
        {
            var (x, y) = DefaultCoordinates();
            return new NodeSample(x, y);
        }

        NodeSample RotateCoordinates(BrushTipNode node, HashSet<string> stack)
        {
            var (srcX, srcY) = EvalVectorInputOrCoordinates(node, 0, stack);
            var resultX = new float[size * size];
            var resultY = new float[size * size];
            var radians = -node.RotationDegrees * MathF.PI / 180f;
            var c = MathF.Cos(radians);
            var s = MathF.Sin(radians);
            var scale = Math.Max(0.001f, node.Scale);
            for (var i = 0; i < resultX.Length; i++)
            {
                var dx = (srcX[i] - node.X) / scale;
                var dy = (srcY[i] - node.Y) / scale;
                resultX[i] = node.X + dx * c - dy * s;
                resultY[i] = node.Y + dx * s + dy * c;
            }
            return new NodeSample(resultX, resultY);
        }

        NodeSample WarpCoordinates(BrushTipNode node, HashSet<string> stack)
        {
            var (srcX, srcY) = EvalVectorInputOrCoordinates(node, 0, stack);
            var warp = EvalScalarInput(node, 1, stack);
            var resultX = new float[size * size];
            var resultY = new float[size * size];
            var angle = node.RotationDegrees * MathF.PI / 180f;
            var c = MathF.Cos(angle);
            var s = MathF.Sin(angle);
            var amountX = node.Width * 0.35f;
            var amountY = node.Height * 0.35f;
            for (var i = 0; i < resultX.Length; i++)
            {
                var w = (warp[i] - 0.5f) * Math.Clamp(node.Density, 0f, 1f);
                resultX[i] = srcX[i] + w * amountX * c;
                resultY[i] = srcY[i] + w * amountY * s;
            }
            return new NodeSample(resultX, resultY);
        }

        float[] Combine(BrushTipNode node, HashSet<string> stack, Func<float, float, float> op)
        {
            var a = EvalScalarInput(node, 0, stack);
            var b = EvalScalarInput(node, 1, stack);
            var result = new float[size * size];
            for (var i = 0; i < result.Length; i++)
                result[i] = op(a[i], b[i]);
            return result;
        }

        float[] Unary(BrushTipNode node, HashSet<string> stack, Func<float, float> op)
        {
            var input = EvalScalarInput(node, 0, stack);
            var result = new float[size * size];
            for (var i = 0; i < result.Length; i++)
                result[i] = Math.Clamp(op(input[i]), 0f, 1f);
            return result;
        }

        float[] Constant(BrushTipNode node)
        {
            var result = new float[size * size];
            Array.Fill(result, Math.Clamp(node.Opacity, 0f, 1f));
            return result;
        }

        float[] Threshold(BrushTipNode node, HashSet<string> stack)
        {
            var input = EvalScalarInput(node, 0, stack);
            var result = new float[size * size];
            var threshold = Math.Clamp(node.Threshold, 0f, 1f);
            for (var i = 0; i < result.Length; i++)
                result[i] = input[i] >= threshold ? Math.Clamp(node.Opacity, 0f, 1f) : 0f;
            return result;
        }

        float[] Mix(BrushTipNode node, HashSet<string> stack)
        {
            var a = EvalScalarInput(node, 0, stack);
            var b = EvalScalarInput(node, 1, stack);
            var factor = Math.Clamp(node.Density, 0f, 1f);
            var result = new float[size * size];
            for (var i = 0; i < result.Length; i++)
                result[i] = Math.Clamp(a[i] * (1f - factor) + b[i] * factor, 0f, 1f);
            return result;
        }

        float[] SmoothStep(BrushTipNode node, HashSet<string> stack)
        {
            var input = EvalScalarInput(node, 0, stack);
            var result = new float[size * size];
            var edge = Math.Clamp(node.Threshold, 0f, 1f);
            var softness = CombinedSoftness(node, brushHardness);
            var opacity = Math.Clamp(node.Opacity, 0f, 1f);
            for (var i = 0; i < result.Length; i++)
            {
                var t = Math.Clamp((input[i] - (edge - softness)) / softness, 0f, 1f);
                var smooth = t * t * (3f - 2f * t);
                result[i] = (1f - smooth) * opacity;
            }
            return result;
        }

        float[] Invert(BrushTipNode node, HashSet<string> stack)
        {
            var input = EvalScalarInput(node, 0, stack);
            var result = new float[size * size];
            for (var i = 0; i < result.Length; i++)
                result[i] = 1f - input[i];
            return result;
        }

        float[] Circle(BrushTipNode node)
        {
            var result = new float[size * size];
            var rx = Math.Max(0.001f, node.Radius * Math.Max(0.01f, node.Width));
            var ry = Math.Max(0.001f, node.Radius * Math.Max(0.01f, node.Height));
            var hard = CombinedHardness(node, brushHardness);
            FillAnalytic(node, result, (x, y) =>
            {
                var dx = (x - node.X) / rx;
                var dy = (y - node.Y) / ry;
                var t = MathF.Sqrt(dx * dx + dy * dy);
                return Falloff(t, hard) * Math.Clamp(node.Opacity, 0f, 1f);
            });
            return result;
        }

        float[] DistanceField(BrushTipNode node, HashSet<string> stack)
        {
            var result = new float[size * size];
            var halfW = Math.Max(0.001f, node.Width * 0.5f);
            var halfH = Math.Max(0.001f, node.Height * 0.5f);
            FillWithCoordinates(node, result, stack, (x, y) =>
            {
                var dx = (x - node.X) / halfW;
                var dy = (y - node.Y) / halfH;
                return Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) * 0.5f, 0f, 1f);
            });
            return result;
        }

        float[] BoxDistanceField(BrushTipNode node, HashSet<string> stack)
        {
            var result = new float[size * size];
            var halfW = Math.Max(0.001f, node.Width * 0.5f);
            var halfH = Math.Max(0.001f, node.Height * 0.5f);
            FillWithCoordinates(node, result, stack, (x, y) =>
            {
                var dx = MathF.Abs(x - node.X) / halfW;
                var dy = MathF.Abs(y - node.Y) / halfH;
                return Math.Clamp(MathF.Max(dx, dy), 0f, 1f);
            });
            return result;
        }

        float[] Rectangle(BrushTipNode node)
        {
            var result = new float[size * size];
            var halfW = Math.Max(0.001f, node.Width * 0.5f);
            var halfH = Math.Max(0.001f, node.Height * 0.5f);
            var hard = CombinedHardness(node, brushHardness);
            FillAnalytic(node, result, (x, y) =>
            {
                var dx = MathF.Abs(x - node.X) / halfW;
                var dy = MathF.Abs(y - node.Y) / halfH;
                var t = MathF.Max(dx, dy);
                return Falloff(t, hard) * Math.Clamp(node.Opacity, 0f, 1f);
            });
            return result;
        }

        float[] RoundedRect(BrushTipNode node)
        {
            var result = new float[size * size];
            var halfW = Math.Max(0.001f, node.Width * 0.5f);
            var halfH = Math.Max(0.001f, node.Height * 0.5f);
            var corner = Math.Clamp(node.Radius, 0f, Math.Min(halfW, halfH));
            var hard = CombinedHardness(node, brushHardness);
            var invRange = 1f / Math.Max(0.001f, Math.Min(halfW, halfH));
            FillAnalytic(node, result, (x, y) =>
            {
                var ax = MathF.Abs(x - node.X);
                var ay = MathF.Abs(y - node.Y);
                if (ax <= halfW - corner || ay <= halfH - corner)
                {
                    var flatT = MathF.Max(ax / halfW, ay / halfH);
                    return Falloff(flatT, hard) * Math.Clamp(node.Opacity, 0f, 1f);
                }
                var cx = MathF.Max(ax - (halfW - corner), 0f);
                var cy = MathF.Max(ay - (halfH - corner), 0f);
                var cornerDist = MathF.Sqrt(cx * cx + cy * cy);
                var cornerT = (cornerDist - corner) * invRange + 1f;
                return Falloff(Math.Clamp(cornerT, 0f, 1f), hard) * Math.Clamp(node.Opacity, 0f, 1f);
            });
            return result;
        }

        float[] LinearGradient(BrushTipNode node, HashSet<string> stack)
        {
            var result = new float[size * size];
            FillWithCoordinates(node, result, stack, (x, y) =>
            {
                var t = ((x - node.X) / Math.Max(0.001f, node.Width)) + 0.5f;
                return Math.Clamp(t, 0f, 1f) * Math.Clamp(node.Opacity, 0f, 1f);
            });
            return result;
        }

        float[] Stripe(BrushTipNode node, HashSet<string> stack)
        {
            var result = new float[size * size];
            var bands = Math.Clamp(node.Scale, 1f, 128f);
            var duty = Math.Clamp(node.Density, 0.02f, 0.98f);
            var hard = CombinedHardness(node, brushHardness);
            FillWithCoordinates(node, result, stack, (_, y) =>
            {
                var t = (y * bands) - MathF.Floor(y * bands);
                var edge = MathF.Abs(t - 0.5f) * 2f;
                var stripe = edge <= duty ? 1f : Falloff((edge - duty) / Math.Max(0.001f, 1f - duty), hard);
                return stripe * Math.Clamp(node.Opacity, 0f, 1f);
            });
            return result;
        }

        float[] Noise(BrushTipNode node, HashSet<string> stack)
        {
            var result = new float[size * size];
            var density = Math.Clamp(node.Density, 0f, 1f);
            var opacity = Math.Clamp(node.Opacity, 0f, 1f);
            var scale = Math.Clamp(node.Scale, 0.05f, 64f);
            var (coordX, coordY) = EvalVectorInputOrCoordinates(node, 0, stack);
            for (var i = 0; i < result.Length; i++)
            {
                var nx = (int)MathF.Floor(coordX[i] * 16f * scale);
                var ny = (int)MathF.Floor(coordY[i] * 16f * scale);
                var value = Hash01(nx, ny, node.Seed);
                result[i] = value <= density ? opacity * value : 0f;
            }
            return result;
        }

        float[] ImageSampler(BrushTipNode node, IReadOnlyList<BrushTipData>? tips)
        {
            var result = new float[size * size];
            var png = BrushMaterialTips.ResolveSamplerPng(node, tips);
            if (png.Length == 0)
                return result;

            using var tip = new ImageBrushTip(png);
            var mask = tip.GenerateMask(size, CombinedHardness(node, brushHardness));
            var opacity = Math.Clamp(node.Opacity, 0f, 1f);
            var ptr = (byte*)mask.GetPixels().ToPointer();
            if (ptr == null)
                return result;

            for (var y = 0; y < size; y++)
            {
                var row = ptr + y * mask.RowBytes;
                for (var x = 0; x < size; x++)
                    result[y * size + x] = row[x] / 255f * opacity;
            }
            return result;
        }

        float[] PolarRadius(BrushTipNode node, HashSet<string> stack)
        {
            var (coordX, coordY) = EvalVectorInputOrCoordinates(node, 0, stack);
            var result = new float[size * size];
            var halfW = Math.Max(0.001f, node.Width * 0.5f);
            var halfH = Math.Max(0.001f, node.Height * 0.5f);
            for (var i = 0; i < result.Length; i++)
            {
                var dx = (coordX[i] - node.X) / halfW;
                var dy = (coordY[i] - node.Y) / halfH;
                result[i] = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) * 0.5f * Math.Max(0.001f, node.Scale), 0f, 1f);
            }
            return result;
        }

        float[] PolarAngle(BrushTipNode node, HashSet<string> stack)
        {
            var (coordX, coordY) = EvalVectorInputOrCoordinates(node, 0, stack);
            var result = new float[size * size];
            for (var i = 0; i < result.Length; i++)
            {
                var angle = MathF.Atan2(coordY[i] - node.Y, coordX[i] - node.X) / (MathF.PI * 2f) + 0.5f;
                result[i] = (angle * Math.Max(0.001f, node.Scale) + node.RotationDegrees / 360f) % 1f;
                if (result[i] < 0f) result[i] += 1f;
            }
            return result;
        }

        float[] Bristle(BrushTipNode node)
        {
            var result = new float[size * size];
            var strandCount = Math.Clamp((int)MathF.Round(size * Math.Clamp(node.Density, 0.05f, 1f) * 0.32f), 3, 64);
            var halfW = Math.Max(0.001f, node.Width * 0.5f);
            var halfH = Math.Max(0.001f, node.Height * 0.5f);
            var hard = CombinedHardness(node, brushHardness);
            FillAnalytic(node, result, (x, y) =>
            {
                var lx = (x - node.X) / halfW;
                var ly = (y - node.Y) / halfH;
                if (MathF.Abs(lx) > 1f || MathF.Abs(ly) > 1f)
                    return 0f;

                var strandPos = (ly + 1f) * 0.5f * strandCount;
                var strandIndex = Math.Clamp((int)MathF.Floor(strandPos), 0, strandCount - 1);
                var center = ((strandIndex + 0.5f) / strandCount) * 2f - 1f;
                var jitter = (Hash01(strandIndex, 13, node.Seed) - 0.5f) * 0.12f;
                var width = (0.22f + 0.45f * Hash01(strandIndex, 31, node.Seed)) / strandCount * (1.25f - hard * 0.45f);
                var dist = MathF.Abs(ly - center - jitter);
                var strand = 1f - Math.Clamp(dist / Math.Max(0.001f, width), 0f, 1f);
                var taper = 1f - MathF.Pow(MathF.Abs(lx), 2.6f);
                return Math.Clamp(strand * taper * Math.Clamp(node.Opacity, 0f, 1f), 0f, 1f);
            });
            return result;
        }

        void FillAnalytic(BrushTipNode node, float[] result, Func<float, float, float> alphaAt)
        {
            var radians = -node.RotationDegrees * MathF.PI / 180f;
            var c = MathF.Cos(radians);
            var s = MathF.Sin(radians);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var nx = (x + 0.5f) / size;
                var ny = (y + 0.5f) / size;
                var dx = nx - node.X;
                var dy = ny - node.Y;
                var rx = node.X + dx * c - dy * s;
                var ry = node.Y + dx * s + dy * c;
                result[y * size + x] = Math.Clamp(alphaAt(rx, ry), 0f, 1f);
            }
        }

        void FillWithCoordinates(BrushTipNode node, float[] result, HashSet<string> stack, Func<float, float, float> alphaAt)
        {
            var (coordX, coordY) = EvalVectorInputOrCoordinates(node, 0, stack);
            var radians = -node.RotationDegrees * MathF.PI / 180f;
            var c = MathF.Cos(radians);
            var s = MathF.Sin(radians);
            for (var i = 0; i < result.Length; i++)
            {
                var dx = coordX[i] - node.X;
                var dy = coordY[i] - node.Y;
                var rx = node.X + dx * c - dy * s;
                var ry = node.Y + dx * s + dy * c;
                result[i] = Math.Clamp(alphaAt(rx, ry), 0f, 1f);
            }
        }

        (float[] X, float[] Y) DefaultCoordinates()
        {
            var xs = new float[size * size];
            var ys = new float[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                xs[i] = (x + 0.5f) / size;
                ys[i] = (y + 0.5f) / size;
            }
            return (xs, ys);
        }
    }

    private sealed record NodeSample(float[] A, float[]? B)
    {
        public bool IsVector => B != null;

        public float[] AsScalar(int size)
        {
            if (!IsVector)
                return A;
            var result = new float[size * size];
            var b = B!;
            for (var i = 0; i < result.Length; i++)
                result[i] = Math.Clamp(MathF.Sqrt(A[i] * A[i] + b[i] * b[i]) * 0.70710677f, 0f, 1f);
            return result;
        }

        public static NodeSample Zero(int size)
            => new(new float[size * size], null);
    }

    private static float CombinedHardness(BrushTipNode node, float brushHardness)
        => Math.Clamp(node.Hardness * Math.Clamp(brushHardness, 0.001f, 1f), 0.001f, 1f);

    /// <summary>
    /// SmoothStep nodes store edge width in <see cref="BrushTipNode.Hardness"/> (softness).
    /// Brush hardness scales that down: 0 = full preset softness, 1 = minimal edge feather.
    /// </summary>
    internal static float CombinedSoftness(BrushTipNode node, float brushHardness)
    {
        var baseSoftness = Math.Clamp(node.Hardness, 0.0001f, 1f);
        var hard = Math.Clamp(brushHardness, 0f, 1f);
        return Math.Max(0.0001f, baseSoftness * (1f - hard));
    }

    private static float Falloff(float t, float hardness)
    {
        if (t >= 1f) return 0f;
        if (t <= hardness) return 1f;
        var fade = (t - hardness) / Math.Max(0.001f, 1f - hardness);
        return (MathF.Cos(fade * MathF.PI) + 1f) * 0.5f;
    }

    private static float Hash01(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0x85EBCA6Bu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777215f;
        }
    }

    private static SKBitmap NewAlpha8(int size)
        => new(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Unpremul));

    private static unsafe SKBitmap EvaluateViaStamp(
        BrushTipNodeGraph graph,
        int size,
        float brushHardness,
        IReadOnlyList<BrushTipData>? materialTips)
    {
        var bitmap = NewAlpha8(size);
        var ptr = (byte*)bitmap.GetPixels().ToPointer();
        if (ptr == null)
            return bitmap;

        var inv = 1f / size;
        if (BrushTipStampFastPath.TryCreate(graph, brushHardness, out var fastAlpha))
        {
            for (var y = 0; y < size; y++)
            {
                var row = ptr + y * bitmap.RowBytes;
                var v = (y + 0.5f) * inv;
                for (var x = 0; x < size; x++)
                {
                    var u = (x + 0.5f) * inv;
                    row[x] = (byte)(Math.Clamp(fastAlpha(u, v), 0f, 1f) * 255f + 0.5f);
                }
            }
            return bitmap;
        }

        using var ctx = new BrushTipStampContext(graph, brushHardness, materialTips);
        for (var y = 0; y < size; y++)
        {
            var row = ptr + y * bitmap.RowBytes;
            var v = (y + 0.5f) * inv;
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) * inv;
                row[x] = (byte)(Math.Clamp(ctx.EvaluateAlpha(u, v), 0f, 1f) * 255f + 0.5f);
            }
        }
        return bitmap;
    }

    public static bool GraphUsesColor(BrushTipNodeGraph graph, IReadOnlyList<BrushTipData>? materialTips = null)
    {
        foreach (var node in graph.Nodes)
        {
            if (node.Kind != BrushTipNodeKind.ImageSampler)
                continue;
            var png = BrushMaterialTips.ResolveSamplerPng(node, materialTips);
            if (png.Length == 0)
                continue;
            using var tip = new ImageBrushTip(png);
            if (tip.HasColor)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Evaluates the graph to an RGBA stamp when any ImageSampler carries color data.
    /// Grayscale samplers contribute white RGB weighted by mask alpha; combine nodes blend color and alpha.
    /// </summary>
    public static SKBitmap? EvaluateColor(BrushTipNodeGraph graph, int size, float brushHardness, IReadOnlyList<BrushTipData>? materialTips = null)
    {
        if (!GraphUsesColor(graph, materialTips))
            return null;

        size = Math.Max(1, size);
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var cache = new Dictionary<string, ColorField>(StringComparer.Ordinal);
        var outputId = nodes.ContainsKey(graph.OutputNodeId) ? graph.OutputNodeId : graph.Nodes.FirstOrDefault()?.Id;
        if (outputId == null)
            return null;

        var field = EvalColor(outputId, []);
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        unsafe
        {
            var ptr = (byte*)bitmap.GetPixels().ToPointer();
            if (ptr == null)
                return bitmap;
            for (var i = 0; i < field.A.Length; i++)
            {
                var a = (byte)(Math.Clamp(field.A[i], 0f, 1f) * 255f + 0.5f);
                var o = i * 4;
                ptr[o + 0] = (byte)(Math.Clamp(field.B[i], 0f, 1f) * 255f + 0.5f);
                ptr[o + 1] = (byte)(Math.Clamp(field.G[i], 0f, 1f) * 255f + 0.5f);
                ptr[o + 2] = (byte)(Math.Clamp(field.R[i], 0f, 1f) * 255f + 0.5f);
                ptr[o + 3] = a;
            }
        }
        return bitmap;

        ColorField EvalColor(string id, HashSet<string> stack)
        {
            if (cache.TryGetValue(id, out var existing))
                return existing;
            if (!nodes.TryGetValue(id, out var node) || !stack.Add(id))
                return ColorField.Zero(size);

            var result = node.Kind switch
            {
                BrushTipNodeKind.Output => EvalColorInput(node, 0, stack),
                BrushTipNodeKind.ImageSampler => ImageSamplerColor(node, materialTips),
                BrushTipNodeKind.Add => CombineColor(node, stack, (a, b) => Math.Clamp(a + b, 0f, 1f)),
                BrushTipNodeKind.Multiply => CombineColor(node, stack, (a, b) => a * b),
                BrushTipNodeKind.Max => CombineColor(node, stack, MathF.Max),
                BrushTipNodeKind.Min => CombineColor(node, stack, MathF.Min),
                BrushTipNodeKind.Subtract => CombineColor(node, stack, (a, b) => Math.Clamp(a - b, 0f, 1f)),
                BrushTipNodeKind.Mix => MixColor(node, stack),
                BrushTipNodeKind.Threshold => MaskColor(node, stack, v =>
                    v >= Math.Clamp(node.Threshold, 0f, 1f) ? Math.Clamp(node.Opacity, 0f, 1f) : 0f),
                BrushTipNodeKind.SmoothStep => SmoothStepColor(node, stack),
                BrushTipNodeKind.Invert => InvertColor(node, stack),
                BrushTipNodeKind.Power => UnaryColor(node, stack, v =>
                    MathF.Pow(Math.Clamp(v, 0f, 1f), Math.Clamp(node.Scale, 0.05f, 16f)) * Math.Clamp(node.Opacity, 0f, 1f)),
                BrushTipNodeKind.Sine => UnaryColor(node, stack, v =>
                    (MathF.Sin((v * Math.Clamp(node.Scale, 0.05f, 64f) + node.X) * MathF.PI * 2f) * 0.5f + 0.5f)
                    * Math.Clamp(node.Opacity, 0f, 1f)),
                BrushTipNodeKind.Absolute => UnaryColor(node, stack, v =>
                    Math.Abs(v - node.X) * Math.Clamp(node.Opacity, 0f, 1f)),
                _ => FromScalar(EvalScalarNode(id, stack))
            };
            stack.Remove(id);
            cache[id] = result;
            return result;
        }

        float[] EvalScalarNode(string id, HashSet<string> stack)
        {
            if (!stack.Add(id))
                return new float[size * size];
            var temp = graph.DeepClone();
            temp.OutputNodeId = id;
            using var bmp = Evaluate(temp, size, brushHardness);
            stack.Remove(id);
            var result = new float[size * size];
            unsafe
            {
                var ptr = (byte*)bmp.GetPixels().ToPointer();
                if (ptr == null)
                    return result;
                for (var i = 0; i < result.Length; i++)
                    result[i] = ptr[i] / 255f;
            }
            return result;
        }

        ColorField EvalColorInput(BrushTipNode node, int index, HashSet<string> stack)
            => index < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[index])
                ? EvalColor(node.Inputs[index], stack)
                : ColorField.Zero(size);

        float[] EvalScalarInput(BrushTipNode node, int index, HashSet<string> stack)
            => index < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[index])
                ? EvalScalarNode(node.Inputs[index], stack)
                : new float[size * size];

        ColorField FromScalar(float[] alpha)
        {
            var n = alpha.Length;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            Array.Fill(r, 1f);
            Array.Fill(g, 1f);
            Array.Fill(b, 1f);
            return new ColorField(r, g, b, alpha, false);
        }

        ColorField ImageSamplerColor(BrushTipNode node, IReadOnlyList<BrushTipData>? tips)
        {
            var n = size * size;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];
            var png = BrushMaterialTips.ResolveSamplerPng(node, tips);
            if (png.Length == 0)
                return new ColorField(r, g, b, a, false);

            using var tip = new ImageBrushTip(png);
            var mask = tip.GenerateMask(size, CombinedHardness(node, brushHardness));
            var opacity = Math.Clamp(node.Opacity, 0f, 1f);
            var hasColor = tip.HasColor;
            var stamp = hasColor ? tip.GenerateColorStamp(size) : null;
            unsafe
            {
                var maskPtr = (byte*)mask.GetPixels().ToPointer();
                byte* stampPtr = stamp != null ? (byte*)stamp.GetPixels().ToPointer() : null;
                var maskRow = mask.RowBytes;
                var stampRow = stamp?.RowBytes ?? 0;
                for (var y = 0; y < size; y++)
                {
                    var maskRowPtr = maskPtr + y * maskRow;
                    var stampRowPtr = stampPtr + y * stampRow;
                    for (var x = 0; x < size; x++)
                    {
                        var i = y * size + x;
                        var maskA = maskRowPtr[x] / 255f * opacity;
                        a[i] = maskA;
                        if (hasColor && stampPtr != null)
                        {
                            var o = x * 4;
                            b[i] = stampRowPtr[o + 0] / 255f;
                            g[i] = stampRowPtr[o + 1] / 255f;
                            r[i] = stampRowPtr[o + 2] / 255f;
                        }
                        else
                        {
                            r[i] = g[i] = b[i] = 1f;
                        }
                    }
                }
            }
            return new ColorField(r, g, b, a, hasColor);
        }

        ColorField CombineColor(BrushTipNode node, HashSet<string> stack, Func<float, float, float> op)
        {
            var left = EvalColorInput(node, 0, stack);
            var right = EvalColorInput(node, 1, stack);
            var n = left.A.Length;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];
            for (var i = 0; i < n; i++)
            {
                var la = left.A[i];
                var ra = right.A[i];
                a[i] = op(la, ra);
                var denom = Math.Max(a[i], 0.0001f);
                r[i] = Math.Clamp((left.R[i] * la + right.R[i] * ra) / denom, 0f, 1f);
                g[i] = Math.Clamp((left.G[i] * la + right.G[i] * ra) / denom, 0f, 1f);
                b[i] = Math.Clamp((left.B[i] * la + right.B[i] * ra) / denom, 0f, 1f);
            }
            return new ColorField(r, g, b, a, left.HasColor || right.HasColor);
        }

        ColorField MixColor(BrushTipNode node, HashSet<string> stack)
        {
            var left = EvalColorInput(node, 0, stack);
            var right = EvalColorInput(node, 1, stack);
            var t = Math.Clamp(node.Density, 0f, 1f);
            var inv = 1f - t;
            var n = left.A.Length;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];
            for (var i = 0; i < n; i++)
            {
                r[i] = Math.Clamp(left.R[i] * inv + right.R[i] * t, 0f, 1f);
                g[i] = Math.Clamp(left.G[i] * inv + right.G[i] * t, 0f, 1f);
                b[i] = Math.Clamp(left.B[i] * inv + right.B[i] * t, 0f, 1f);
                a[i] = Math.Clamp(left.A[i] * inv + right.A[i] * t, 0f, 1f);
            }
            return new ColorField(r, g, b, a, left.HasColor || right.HasColor);
        }

        ColorField MaskColor(BrushTipNode node, HashSet<string> stack, Func<float, float> maskAt)
        {
            var input = EvalColorInput(node, 0, stack);
            var scalar = EvalScalarInput(node, 0, stack);
            var n = input.A.Length;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];
            for (var i = 0; i < n; i++)
            {
                var m = maskAt(scalar[i]);
                r[i] = input.R[i] * m;
                g[i] = input.G[i] * m;
                b[i] = input.B[i] * m;
                a[i] = input.A[i] * m;
            }
            return new ColorField(r, g, b, a, input.HasColor);
        }

        ColorField SmoothStepColor(BrushTipNode node, HashSet<string> stack)
        {
            var input = EvalColorInput(node, 0, stack);
            var scalar = EvalScalarInput(node, 0, stack);
            var edge = Math.Clamp(node.Threshold, 0f, 1f);
            var softness = CombinedSoftness(node, brushHardness);
            var opacity = Math.Clamp(node.Opacity, 0f, 1f);
            var n = input.A.Length;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = Math.Clamp((scalar[i] - (edge - softness)) / softness, 0f, 1f);
                var smooth = (1f - t * t * (3f - 2f * t)) * opacity;
                r[i] = input.R[i] * smooth;
                g[i] = input.G[i] * smooth;
                b[i] = input.B[i] * smooth;
                a[i] = input.A[i] * smooth;
            }
            return new ColorField(r, g, b, a, input.HasColor);
        }

        ColorField InvertColor(BrushTipNode node, HashSet<string> stack)
        {
            var input = EvalColorInput(node, 0, stack);
            var n = input.A.Length;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];
            for (var i = 0; i < n; i++)
            {
                r[i] = 1f - input.R[i];
                g[i] = 1f - input.G[i];
                b[i] = 1f - input.B[i];
                a[i] = 1f - input.A[i];
            }
            return new ColorField(r, g, b, a, input.HasColor);
        }

        ColorField UnaryColor(BrushTipNode node, HashSet<string> stack, Func<float, float> op)
        {
            var input = EvalColorInput(node, 0, stack);
            var scalar = EvalScalarInput(node, 0, stack);
            var n = input.A.Length;
            var r = new float[n];
            var g = new float[n];
            var b = new float[n];
            var a = new float[n];
            for (var i = 0; i < n; i++)
            {
                var m = Math.Clamp(op(scalar[i]), 0f, 1f);
                r[i] = input.R[i] * m;
                g[i] = input.G[i] * m;
                b[i] = input.B[i] * m;
                a[i] = input.A[i] * m;
            }
            return new ColorField(r, g, b, a, input.HasColor);
        }
    }

    private sealed class ColorField(float[] r, float[] g, float[] b, float[] a, bool hasColor)
    {
        public float[] R { get; } = r;
        public float[] G { get; } = g;
        public float[] B { get; } = b;
        public float[] A { get; } = a;
        public bool HasColor { get; } = hasColor;

        public static ColorField Zero(int size)
        {
            var n = size * size;
            return new ColorField(new float[n], new float[n], new float[n], new float[n], false);
        }
    }
}
