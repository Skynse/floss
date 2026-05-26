using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Floss.App.Brushes.Curves;

public enum BrushParameterTarget
{
    Size,
    Opacity,
    Flow,
    Hardness,
    Angle,
    Spacing,
    TipDensity,
    TipThickness,
    Scatter,
}

public enum BrushParameterNodeKind
{
    Output,
    Constant,
    Pressure,
    Velocity,
    Tilt,
    Random,
    Add,
    Multiply,
    Clamp,
    Curve,
}

public sealed class BrushParameterNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BrushParameterNodeKind Kind { get; set; } = BrushParameterNodeKind.Constant;
    public List<string> Inputs { get; set; } = [];
    public float Value { get; set; } = 1f;
    public float Min { get; set; } = 0f;
    public float Max { get; set; } = 1f;
    public float Strength { get; set; } = 1f;
    public List<float> Curve { get; set; } = [0f, 0f, 1f, 1f];

    public BrushParameterNode DeepClone() => new()
    {
        Id = Id,
        Kind = Kind,
        Inputs = Inputs.ToList(),
        Value = Value,
        Min = Min,
        Max = Max,
        Strength = Strength,
        Curve = Curve.ToList()
    };
}

public sealed class BrushParameterGraph
{
    public int Version { get; set; } = 1;
    public BrushParameterTarget Target { get; set; } = BrushParameterTarget.Size;
    public string OutputNodeId { get; set; } = "output";
    public List<BrushParameterNode> Nodes { get; set; } =
    [
        new BrushParameterNode { Id = "constant", Kind = BrushParameterNodeKind.Constant },
        new BrushParameterNode { Id = "output", Kind = BrushParameterNodeKind.Output, Inputs = ["constant"] }
    ];

    public BrushParameterGraph DeepClone() => new()
    {
        Version = Version,
        Target = Target,
        OutputNodeId = OutputNodeId,
        Nodes = Nodes.Select(n => n.DeepClone()).ToList()
    };

    public string CacheKey()
    {
        var sb = new StringBuilder();
        sb.Append(Version.ToString(CultureInfo.InvariantCulture)).Append('|').Append(Target).Append('|').Append(OutputNodeId);
        foreach (var node in Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            sb.Append('|').Append(node.Id)
                .Append(':').Append((int)node.Kind)
                .Append(':').Append(string.Join(',', node.Inputs))
                .Append(':').Append(F(node.Value))
                .Append(':').Append(F(node.Min))
                .Append(':').Append(F(node.Max))
                .Append(':').Append(F(node.Strength))
                .Append(':').Append(string.Join(',', node.Curve.Select(F)));
        }
        return sb.ToString();

        static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<string> Validate(int maxNodes = 64)
    {
        var errors = new List<string>();
        if (Nodes.Count == 0)
        {
            errors.Add("Parameter graph has no nodes.");
            return errors;
        }
        if (Nodes.Count > maxNodes)
            errors.Add($"Parameter graph has {Nodes.Count} nodes; maximum is {maxNodes}.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                errors.Add("Parameter graph contains a node with an empty id.");
            else if (!ids.Add(node.Id))
                errors.Add($"Parameter graph contains duplicate node id '{node.Id}'.");
        }
        if (!ids.Contains(OutputNodeId))
            errors.Add($"Parameter graph output node '{OutputNodeId}' does not exist.");
        foreach (var node in Nodes)
            foreach (var input in node.Inputs)
                if (!ids.Contains(input))
                    errors.Add($"Node '{node.Id}' references missing input '{input}'.");
        return errors;
    }

    public static BrushParameterGraph FromDynamics(BrushParameterTarget target, CurveOption option)
    {
        var dyn = BrushDynamics.ToParameterDynamics(option);
        var nodes = new List<BrushParameterNode>
        {
            new() { Id = "constant", Kind = BrushParameterNodeKind.Constant, Value = 1f }
        };
        var mixInputs = new List<string> { "constant" };

        void AddSensor(string id, BrushParameterNodeKind kind, bool enabled, IReadOnlyList<float> curve, float min = 0f, float max = 1f)
        {
            nodes.Add(new BrushParameterNode
            {
                Id = id,
                Kind = kind,
                Strength = enabled ? 1f : 0f,
                Min = min,
                Max = max,
                Curve = curve.ToList()
            });
            if (enabled)
                mixInputs.Add(id);
        }

        AddSensor("pressure", BrushParameterNodeKind.Pressure, dyn.PressureEnabled, dyn.CurveData, dyn.Min, dyn.Max);
        AddSensor("velocity", BrushParameterNodeKind.Velocity, dyn.VelocityEnabled, dyn.VelocityCurveData);
        AddSensor("tilt", BrushParameterNodeKind.Tilt, dyn.TiltEnabled, dyn.TiltCurveData);
        AddSensor("random", BrushParameterNodeKind.Random, dyn.RandomEnabled, dyn.RandomCurveData);

        const string mixId = "mix";
        nodes.Add(new BrushParameterNode { Id = mixId, Kind = BrushParameterNodeKind.Multiply, Inputs = mixInputs });
        nodes.Add(new BrushParameterNode { Id = "output", Kind = BrushParameterNodeKind.Output, Inputs = [mixId] });

        return new BrushParameterGraph
        {
            Target = target,
            OutputNodeId = "output",
            Nodes = nodes
        };
    }

    public float Evaluate(in StrokePoint sp, float fallback = 1f)
    {
        if (Validate().Count > 0)
            return fallback;

        var nodes = Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var cache = new Dictionary<string, float>(StringComparer.Ordinal);
        var outputId = nodes.ContainsKey(OutputNodeId) ? OutputNodeId : Nodes.FirstOrDefault()?.Id;
        var point = sp;
        return outputId == null ? fallback : Eval(outputId, []);

        float Eval(string id, HashSet<string> stack)
        {
            if (cache.TryGetValue(id, out var cached))
                return cached;
            if (!nodes.TryGetValue(id, out var node) || !stack.Add(id))
                return fallback;

            var value = node.Kind switch
            {
                BrushParameterNodeKind.Output => EvalInput(node, 0, stack, fallback),
                BrushParameterNodeKind.Constant => node.Value,
                BrushParameterNodeKind.Pressure => Sensor(node, point.Pressure),
                BrushParameterNodeKind.Velocity => Sensor(node, point.Speed),
                BrushParameterNodeKind.Tilt => Sensor(node, Math.Clamp(MathF.Sqrt(point.TiltX * point.TiltX + point.TiltY * point.TiltY), 0f, 1f)),
                BrushParameterNodeKind.Random => Sensor(node, point.Random),
                BrushParameterNodeKind.Add => EvalInput(node, 0, stack, 0f) + EvalInput(node, 1, stack, 0f),
                BrushParameterNodeKind.Multiply => EvalInput(node, 0, stack, 1f) * EvalInput(node, 1, stack, 1f),
                BrushParameterNodeKind.Clamp => Math.Clamp(EvalInput(node, 0, stack, fallback), node.Min, node.Max),
                BrushParameterNodeKind.Curve => CurveValue(node, EvalInput(node, 0, stack, fallback)),
                _ => fallback
            };
            stack.Remove(id);
            cache[id] = Math.Clamp(value, -16f, 16f);
            return cache[id];
        }

        float EvalInput(BrushParameterNode node, int index, HashSet<string> stack, float missing)
            => index < node.Inputs.Count && !string.IsNullOrWhiteSpace(node.Inputs[index])
                ? Eval(node.Inputs[index], stack)
                : missing;

        static float Sensor(BrushParameterNode node, float input)
        {
            if (node.Strength <= 0.0001f)
                return 1f;
            var curved = EvaluateCurve(node.Curve, Math.Clamp(input, 0f, 1f));
            var ranged = node.Min + curved * (node.Max - node.Min);
            return 1f + (ranged - 1f) * Math.Clamp(node.Strength, 0f, 1f);
        }

        static float CurveValue(BrushParameterNode node, float input)
        {
            var curved = EvaluateCurve(node.Curve, Math.Clamp(input, 0f, 1f));
            var ranged = node.Min + curved * (node.Max - node.Min);
            return 1f + (ranged - 1f) * Math.Clamp(node.Strength, 0f, 1f);
        }

        static float EvaluateCurve(IReadOnlyList<float> points, float x)
        {
            if (points.Count < 4)
                return x;
            var pairs = new List<CurvePoint>(points.Count / 2);
            for (var i = 0; i + 1 < points.Count; i += 2)
                pairs.Add(new CurvePoint(Math.Clamp(points[i], 0f, 1f), Math.Clamp(points[i + 1], 0f, 1f)));
            if (pairs.Count < 2)
                return x;
            var curve = new CubicCurve();
            curve.SetPoints(pairs);
            return curve.Evaluate(x);
        }
    }
}
