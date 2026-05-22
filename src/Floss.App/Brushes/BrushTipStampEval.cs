using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Brushes;

/// <summary>
/// Evaluates scalar brush-tip node graphs at a single normalized coordinate (u,v in [0,1])
/// instead of baking a full-size mask bitmap.
/// </summary>
public sealed class BrushTipStampContext : IDisposable
{
    private const int ImageSampleSize = 256;

    private readonly BrushTipNodeGraph _graph;
    private readonly float _brushHardness;
    private readonly IReadOnlyList<BrushTipData>? _materialTips;
    private readonly Dictionary<string, BrushTipNode> _nodes;
    private readonly string _outputId;
    private readonly Dictionary<string, float> _scalarCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (float X, float Y)> _vectorCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageBrushTip> _imageTips = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visitStack = new(StringComparer.Ordinal);

    public BrushTipStampContext(BrushTipNodeGraph graph, float brushHardness, IReadOnlyList<BrushTipData>? materialTips = null)
    {
        _graph = graph;
        _brushHardness = Math.Clamp(brushHardness, 0.001f, 1f);
        _materialTips = materialTips;
        _nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _outputId = _nodes.ContainsKey(graph.OutputNodeId)
            ? graph.OutputNodeId
            : graph.Nodes.FirstOrDefault()?.Id ?? "output";
    }

    public static bool CanEvaluate(BrushTipNodeGraph graph, IReadOnlyList<BrushTipData>? materialTips = null)
        => !BrushTipNodeGraphEvaluator.GraphUsesColor(graph, materialTips);

    public float EvaluateAlpha(float u, float v)
    {
        _scalarCache.Clear();
        _vectorCache.Clear();
        _visitStack.Clear();
        return Math.Clamp(EvalScalar(_outputId, u, v, _visitStack), 0f, 1f);
    }

    public void Dispose()
    {
        foreach (var tip in _imageTips.Values)
            tip.Dispose();
        _imageTips.Clear();
    }

    private float EvalScalar(string id, float u, float v, HashSet<string> stack)
    {
        if (_scalarCache.TryGetValue(id, out var cached))
            return cached;

        if (!_nodes.TryGetValue(id, out var node) || !stack.Add(id))
            return 0f;

        var result = node.Kind switch
        {
            BrushTipNodeKind.Output => EvalScalarInput(node, 0, u, v, stack),
            BrushTipNodeKind.Coordinates => VectorAsScalar(u, v),
            BrushTipNodeKind.RotateCoordinates => VectorAsScalar(RotateCoordinates(node, u, v, stack)),
            BrushTipNodeKind.WarpCoordinates => VectorAsScalar(WarpCoordinates(node, u, v, stack)),
            BrushTipNodeKind.PolarRadius => PolarRadius(node, u, v, stack),
            BrushTipNodeKind.PolarAngle => PolarAngle(node, u, v, stack),
            BrushTipNodeKind.Value => Math.Clamp(node.Opacity, 0f, 1f),
            BrushTipNodeKind.DistanceField => DistanceField(node, u, v, stack),
            BrushTipNodeKind.BoxDistanceField => BoxDistanceField(node, u, v, stack),
            BrushTipNodeKind.Circle => Circle(node, u, v),
            BrushTipNodeKind.Rectangle => Rectangle(node, u, v),
            BrushTipNodeKind.RoundedRectangle => RoundedRect(node, u, v),
            BrushTipNodeKind.LinearGradient => LinearGradient(node, u, v, stack),
            BrushTipNodeKind.Stripe => Stripe(node, u, v, stack),
            BrushTipNodeKind.ImageSampler => ImageSampler(node, u, v),
            BrushTipNodeKind.Noise => Noise(node, u, v, stack),
            BrushTipNodeKind.Bristle => Bristle(node, u, v),
            BrushTipNodeKind.Add => Combine(node, u, v, stack, (a, b) => Math.Clamp(a + b, 0f, 1f)),
            BrushTipNodeKind.Multiply => Combine(node, u, v, stack, (a, b) => a * b),
            BrushTipNodeKind.Max => Combine(node, u, v, stack, MathF.Max),
            BrushTipNodeKind.Min => Combine(node, u, v, stack, MathF.Min),
            BrushTipNodeKind.Subtract => Combine(node, u, v, stack, (a, b) => Math.Clamp(a - b, 0f, 1f)),
            BrushTipNodeKind.Threshold => Threshold(node, u, v, stack),
            BrushTipNodeKind.SmoothStep => SmoothStep(node, u, v, stack),
            BrushTipNodeKind.Power => Unary(node, u, v, stack, v0 =>
                MathF.Pow(Math.Clamp(v0, 0f, 1f), Math.Clamp(node.Scale, 0.05f, 16f)) * Math.Clamp(node.Opacity, 0f, 1f)),
            BrushTipNodeKind.Sine => Unary(node, u, v, stack, v0 =>
                (MathF.Sin((v0 * Math.Clamp(node.Scale, 0.05f, 64f) + node.X) * MathF.PI * 2f) * 0.5f + 0.5f)
                * Math.Clamp(node.Opacity, 0f, 1f)),
            BrushTipNodeKind.Absolute => Unary(node, u, v, stack, v0 =>
                Math.Abs(v0 - node.X) * Math.Clamp(node.Opacity, 0f, 1f)),
            BrushTipNodeKind.Mix => Mix(node, u, v, stack),
            BrushTipNodeKind.Invert => 1f - EvalScalarInput(node, 0, u, v, stack),
            _ => 0f
        };

        stack.Remove(id);
        _scalarCache[id] = result;
        return result;
    }

    private float EvalScalarInput(BrushTipNode node, int index, float u, float v, HashSet<string> stack)
        => index < node.Inputs.Count ? EvalScalar(node.Inputs[index], u, v, stack) : 0f;

    private (float X, float Y) EvalVector(string id, float u, float v, HashSet<string> stack)
    {
        if (_vectorCache.TryGetValue(id, out var cached))
            return cached;

        if (!_nodes.TryGetValue(id, out var node) || !stack.Add(id))
            return (u, v);

        var result = node.Kind switch
        {
            BrushTipNodeKind.Coordinates => (u, v),
            BrushTipNodeKind.RotateCoordinates => RotateCoordinates(node, u, v, stack),
            BrushTipNodeKind.WarpCoordinates => WarpCoordinates(node, u, v, stack),
            _ => EvalVectorInputOrCoordinates(node, 0, u, v, stack)
        };

        stack.Remove(id);
        _vectorCache[id] = result;
        return result;
    }

    private (float X, float Y) EvalVectorInputOrCoordinates(BrushTipNode node, int index, float u, float v, HashSet<string> stack)
    {
        if (index < node.Inputs.Count && !string.IsNullOrEmpty(node.Inputs[index]))
            return EvalVector(node.Inputs[index], u, v, stack);
        return (u, v);
    }

    private (float X, float Y) RotateCoordinates(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (srcX, srcY) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        var radians = -node.RotationDegrees * MathF.PI / 180f;
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        var scale = Math.Max(0.001f, node.Scale);
        var dx = (srcX - node.X) / scale;
        var dy = (srcY - node.Y) / scale;
        return (node.X + dx * c - dy * s, node.Y + dx * s + dy * c);
    }

    private (float X, float Y) WarpCoordinates(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (srcX, srcY) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        var warp = EvalScalarInput(node, 1, u, v, stack);
        var angle = node.RotationDegrees * MathF.PI / 180f;
        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        var amountX = node.Width * 0.35f;
        var amountY = node.Height * 0.35f;
        var w = (warp - 0.5f) * Math.Clamp(node.Density, 0f, 1f);
        return (srcX + w * amountX * c, srcY + w * amountY * s);
    }

    private (float X, float Y) TransformCoords(BrushTipNode node, float x, float y)
    {
        var radians = -node.RotationDegrees * MathF.PI / 180f;
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        var dx = x - node.X;
        var dy = y - node.Y;
        return (node.X + dx * c - dy * s, node.Y + dx * s + dy * c);
    }

    private float Combine(BrushTipNode node, float u, float v, HashSet<string> stack, Func<float, float, float> op)
        => op(EvalScalarInput(node, 0, u, v, stack), EvalScalarInput(node, 1, u, v, stack));

    private float Unary(BrushTipNode node, float u, float v, HashSet<string> stack, Func<float, float> op)
        => Math.Clamp(op(EvalScalarInput(node, 0, u, v, stack)), 0f, 1f);

    private float Threshold(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var input = EvalScalarInput(node, 0, u, v, stack);
        var threshold = Math.Clamp(node.Threshold, 0f, 1f);
        return input >= threshold ? Math.Clamp(node.Opacity, 0f, 1f) : 0f;
    }

    private float Mix(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var a = EvalScalarInput(node, 0, u, v, stack);
        var b = EvalScalarInput(node, 1, u, v, stack);
        var factor = Math.Clamp(node.Density, 0f, 1f);
        return Math.Clamp(a * (1f - factor) + b * factor, 0f, 1f);
    }

    private float SmoothStep(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var input = EvalScalarInput(node, 0, u, v, stack);
        var edge = Math.Clamp(node.Threshold, 0f, 1f);
        var softness = CombinedSoftness(node);
        var opacity = Math.Clamp(node.Opacity, 0f, 1f);
        var t = Math.Clamp((input - (edge - softness)) / softness, 0f, 1f);
        var smooth = t * t * (3f - 2f * t);
        return (1f - smooth) * opacity;
    }

    private float Circle(BrushTipNode node, float u, float v)
    {
        var (x, y) = TransformCoords(node, u, v);
        var rx = Math.Max(0.001f, node.Radius * Math.Max(0.01f, node.Width));
        var ry = Math.Max(0.001f, node.Radius * Math.Max(0.01f, node.Height));
        var hard = CombinedHardness(node);
        var dx = (x - node.X) / rx;
        var dy = (y - node.Y) / ry;
        var t = MathF.Sqrt(dx * dx + dy * dy);
        return Falloff(t, hard) * Math.Clamp(node.Opacity, 0f, 1f);
    }

    private float DistanceField(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (x, y) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        (x, y) = TransformCoords(node, x, y);
        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var dx = (x - node.X) / halfW;
        var dy = (y - node.Y) / halfH;
        return Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) * 0.5f, 0f, 1f);
    }

    private float BoxDistanceField(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (x, y) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        (x, y) = TransformCoords(node, x, y);
        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var dx = MathF.Abs(x - node.X) / halfW;
        var dy = MathF.Abs(y - node.Y) / halfH;
        return Math.Clamp(MathF.Max(dx, dy), 0f, 1f);
    }

    private float Rectangle(BrushTipNode node, float u, float v)
    {
        var (x, y) = TransformCoords(node, u, v);
        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var hard = CombinedHardness(node);
        var dx = MathF.Abs(x - node.X) / halfW;
        var dy = MathF.Abs(y - node.Y) / halfH;
        var t = MathF.Max(dx, dy);
        return Falloff(t, hard) * Math.Clamp(node.Opacity, 0f, 1f);
    }

    private float RoundedRect(BrushTipNode node, float u, float v)
    {
        var (x, y) = TransformCoords(node, u, v);
        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var corner = Math.Clamp(node.Radius, 0f, Math.Min(halfW, halfH));
        var hard = CombinedHardness(node);
        var invRange = 1f / Math.Max(0.001f, Math.Min(halfW, halfH));
        var ax = MathF.Abs(x - node.X);
        var ay = MathF.Abs(y - node.Y);
        float t;
        if (ax <= halfW - corner || ay <= halfH - corner)
            t = MathF.Max(ax / halfW, ay / halfH);
        else
        {
            var cx = MathF.Max(ax - (halfW - corner), 0f);
            var cy = MathF.Max(ay - (halfH - corner), 0f);
            var cornerDist = MathF.Sqrt(cx * cx + cy * cy);
            t = Math.Clamp((cornerDist - corner) * invRange + 1f, 0f, 1f);
        }
        return Falloff(t, hard) * Math.Clamp(node.Opacity, 0f, 1f);
    }

    private float LinearGradient(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (x, _) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        (x, _) = TransformCoords(node, x, v);
        var t = ((x - node.X) / Math.Max(0.001f, node.Width)) + 0.5f;
        return Math.Clamp(t, 0f, 1f) * Math.Clamp(node.Opacity, 0f, 1f);
    }

    private float Stripe(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (_, y) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        (_, y) = TransformCoords(node, u, y);
        var bands = Math.Clamp(node.Scale, 1f, 128f);
        var duty = Math.Clamp(node.Density, 0.02f, 0.98f);
        var hard = CombinedHardness(node);
        var t = (y * bands) - MathF.Floor(y * bands);
        var edge = MathF.Abs(t - 0.5f) * 2f;
        var stripe = edge <= duty ? 1f : Falloff((edge - duty) / Math.Max(0.001f, 1f - duty), hard);
        return stripe * Math.Clamp(node.Opacity, 0f, 1f);
    }

    private float Noise(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (coordX, coordY) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        var density = Math.Clamp(node.Density, 0f, 1f);
        var opacity = Math.Clamp(node.Opacity, 0f, 1f);
        var scale = Math.Clamp(node.Scale, 0.05f, 64f);
        var nx = (int)MathF.Floor(coordX * 16f * scale);
        var ny = (int)MathF.Floor(coordY * 16f * scale);
        var value = Hash01(nx, ny, node.Seed);
        return value <= density ? opacity * value : 0f;
    }

    private float ImageSampler(BrushTipNode node, float u, float v)
    {
        var png = BrushMaterialTips.ResolveSamplerPng(node, _materialTips);
        if (png.Length == 0)
            return 0f;
        if (!_imageTips.TryGetValue(node.Id, out var tip))
        {
            tip = new ImageBrushTip(png);
            _imageTips[node.Id] = tip;
        }
        var opacity = Math.Clamp(node.Opacity, 0f, 1f);
        return tip.SampleMaskAlpha(u, v, ImageSampleSize, CombinedHardness(node)) * opacity;
    }

    private float PolarRadius(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (coordX, coordY) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var dx = (coordX - node.X) / halfW;
        var dy = (coordY - node.Y) / halfH;
        return Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) * 0.5f * Math.Max(0.001f, node.Scale), 0f, 1f);
    }

    private float PolarAngle(BrushTipNode node, float u, float v, HashSet<string> stack)
    {
        var (coordX, coordY) = EvalVectorInputOrCoordinates(node, 0, u, v, stack);
        var angle = MathF.Atan2(coordY - node.Y, coordX - node.X) / (MathF.PI * 2f) + 0.5f;
        var result = (angle * Math.Max(0.001f, node.Scale) + node.RotationDegrees / 360f) % 1f;
        if (result < 0f) result += 1f;
        return result;
    }

    private float Bristle(BrushTipNode node, float u, float v)
    {
        var (x, y) = TransformCoords(node, u, v);
        var strandCount = Math.Clamp((int)MathF.Round(ImageSampleSize * Math.Clamp(node.Density, 0.05f, 1f) * 0.32f), 3, 64);
        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var hard = CombinedHardness(node);
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
    }

    private static float VectorAsScalar(float x, float y)
        => Math.Clamp(MathF.Sqrt(x * x + y * y) * 0.70710677f, 0f, 1f);

    private static float VectorAsScalar((float X, float Y) v)
        => VectorAsScalar(v.X, v.Y);

    private float CombinedHardness(BrushTipNode node)
        => Math.Clamp(node.Hardness * _brushHardness, 0.001f, 1f);

    private float CombinedSoftness(BrushTipNode node)
        => BrushTipNodeGraphEvaluator.CombinedSoftness(node, _brushHardness);

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
}
