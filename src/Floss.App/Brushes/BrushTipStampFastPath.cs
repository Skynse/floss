using System;

namespace Floss.App.Brushes;

/// <summary>
/// Fused per-pixel evaluators for common node-graph shapes (distance field + smooth step, etc.)
/// without walking the graph interpreter on every pixel.
/// </summary>
public static class BrushTipStampFastPath
{
    public delegate float AlphaAt(float u, float v);

    public static bool TryCreate(BrushTipNodeGraph graph, float brushHardness, out AlphaAt evaluate)
    {
        evaluate = null!;
        var outputId = graph.OutputNodeId;
        var output = graph.Nodes.Find(n => n.Id == outputId);
        if (output == null || output.Inputs.Count == 0)
            return false;

        var brushH = Math.Clamp(brushHardness, 0.001f, 1f);

        if (TryResolveSmoothStepChain(graph, output.Inputs[0], brushH, out evaluate))
            return true;

        if (TryResolveCircle(graph, output.Inputs[0], brushH, out evaluate))
            return true;

        if (TryResolveRectangle(graph, output.Inputs[0], brushH, out evaluate))
            return true;

        if (TryResolveRoundedRectangle(graph, output.Inputs[0], brushH, out evaluate))
            return true;

        return false;
    }

    private static bool TryResolveSmoothStepChain(
        BrushTipNodeGraph graph,
        string nodeId,
        float brushHardness,
        out AlphaAt evaluate)
    {
        evaluate = null!;
        var step = graph.Nodes.Find(n => n.Id == nodeId);
        if (step?.Kind != BrushTipNodeKind.SmoothStep || step.Inputs.Count == 0)
            return false;

        var field = graph.Nodes.Find(n => n.Id == step.Inputs[0]);
        if (field == null || field.Inputs.Count > 0)
            return false;

        var edge = Math.Clamp(step.Threshold, 0f, 1f);
        var softness = BrushTipNodeGraphEvaluator.CombinedSoftness(step, brushHardness);
        var opacity = Math.Clamp(step.Opacity, 0f, 1f);

        switch (field.Kind)
        {
            case BrushTipNodeKind.DistanceField:
            {
                var halfW = Math.Max(0.001f, field.Width * 0.5f);
                var halfH = Math.Max(0.001f, field.Height * 0.5f);
                var cx = field.X;
                var cy = field.Y;
                var rot = field.RotationDegrees;
                evaluate = (u, v) =>
                {
                    var (x, y) = Rotate(u, v, cx, cy, rot);
                    var dx = (x - cx) / halfW;
                    var dy = (y - cy) / halfH;
                    var dist = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) * 0.5f, 0f, 1f);
                    return ApplySmoothStep(dist, edge, softness, opacity);
                };
                return true;
            }
            case BrushTipNodeKind.BoxDistanceField:
            {
                var halfW = Math.Max(0.001f, field.Width * 0.5f);
                var halfH = Math.Max(0.001f, field.Height * 0.5f);
                var cx = field.X;
                var cy = field.Y;
                var rot = field.RotationDegrees;
                evaluate = (u, v) =>
                {
                    var (x, y) = Rotate(u, v, cx, cy, rot);
                    var dx = MathF.Abs(x - cx) / halfW;
                    var dy = MathF.Abs(y - cy) / halfH;
                    var dist = Math.Clamp(MathF.Max(dx, dy), 0f, 1f);
                    return ApplySmoothStep(dist, edge, softness, opacity);
                };
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryResolveCircle(
        BrushTipNodeGraph graph,
        string nodeId,
        float brushHardness,
        out AlphaAt evaluate)
    {
        evaluate = null!;
        var node = graph.Nodes.Find(n => n.Id == nodeId);
        if (node?.Kind != BrushTipNodeKind.Circle)
            return false;

        var rx = Math.Max(0.001f, node.Radius * Math.Max(0.01f, node.Width));
        var ry = Math.Max(0.001f, node.Radius * Math.Max(0.01f, node.Height));
        var hard = Math.Clamp(node.Hardness * brushHardness, 0.001f, 1f);
        var opacity = Math.Clamp(node.Opacity, 0f, 1f);
        var cx = node.X;
        var cy = node.Y;
        var rot = node.RotationDegrees;

        evaluate = (u, v) =>
        {
            var (x, y) = Rotate(u, v, cx, cy, rot);
            var dx = (x - cx) / rx;
            var dy = (y - cy) / ry;
            var t = MathF.Sqrt(dx * dx + dy * dy);
            return Falloff(t, hard) * opacity;
        };
        return true;
    }

    private static bool TryResolveRectangle(
        BrushTipNodeGraph graph,
        string nodeId,
        float brushHardness,
        out AlphaAt evaluate)
    {
        evaluate = null!;
        var node = graph.Nodes.Find(n => n.Id == nodeId);
        if (node?.Kind != BrushTipNodeKind.Rectangle)
            return false;

        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var hard = Math.Clamp(node.Hardness * brushHardness, 0.001f, 1f);
        var opacity = Math.Clamp(node.Opacity, 0f, 1f);
        var cx = node.X;
        var cy = node.Y;
        var rot = node.RotationDegrees;

        evaluate = (u, v) =>
        {
            var (x, y) = Rotate(u, v, cx, cy, rot);
            var dx = MathF.Abs(x - cx) / halfW;
            var dy = MathF.Abs(y - cy) / halfH;
            var t = MathF.Max(dx, dy);
            return Falloff(t, hard) * opacity;
        };
        return true;
    }

    private static bool TryResolveRoundedRectangle(
        BrushTipNodeGraph graph,
        string nodeId,
        float brushHardness,
        out AlphaAt evaluate)
    {
        evaluate = null!;
        var node = graph.Nodes.Find(n => n.Id == nodeId);
        if (node?.Kind != BrushTipNodeKind.RoundedRectangle)
            return false;

        var halfW = Math.Max(0.001f, node.Width * 0.5f);
        var halfH = Math.Max(0.001f, node.Height * 0.5f);
        var corner = Math.Clamp(node.Radius, 0f, Math.Min(halfW, halfH));
        var hard = Math.Clamp(node.Hardness * brushHardness, 0.001f, 1f);
        var opacity = Math.Clamp(node.Opacity, 0f, 1f);
        var invRange = 1f / Math.Max(0.001f, Math.Min(halfW, halfH));
        var cx = node.X;
        var cy = node.Y;
        var rot = node.RotationDegrees;

        evaluate = (u, v) =>
        {
            var (x, y) = Rotate(u, v, cx, cy, rot);
            var ax = MathF.Abs(x - cx);
            var ay = MathF.Abs(y - cy);
            float t;
            if (ax <= halfW - corner || ay <= halfH - corner)
                t = MathF.Max(ax / halfW, ay / halfH);
            else
            {
                var cdx = MathF.Max(ax - (halfW - corner), 0f);
                var cdy = MathF.Max(ay - (halfH - corner), 0f);
                var cornerDist = MathF.Sqrt(cdx * cdx + cdy * cdy);
                t = Math.Clamp((cornerDist - corner) * invRange + 1f, 0f, 1f);
            }
            return Falloff(t, hard) * opacity;
        };
        return true;
    }

    private static float ApplySmoothStep(float input, float edge, float softness, float opacity)
    {
        var t = Math.Clamp((input - (edge - softness)) / softness, 0f, 1f);
        var smooth = t * t * (3f - 2f * t);
        return (1f - smooth) * opacity;
    }

    private static (float X, float Y) Rotate(float u, float v, float cx, float cy, float degrees)
    {
        if (MathF.Abs(degrees) < 0.01f)
            return (u, v);
        var radians = -degrees * MathF.PI / 180f;
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        var dx = u - cx;
        var dy = v - cy;
        return (cx + dx * c - dy * s, cy + dx * s + dy * c);
    }

    private static float Falloff(float t, float hardness)
    {
        if (t >= 1f) return 0f;
        if (t <= hardness) return 1f;
        var fade = (t - hardness) / Math.Max(0.001f, 1f - hardness);
        var smooth = fade * fade * (3f - 2f * fade);
        var exponent = 1f + hardness * 5f;
        return 1f - MathF.Pow(smooth, exponent);
    }
}
