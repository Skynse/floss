using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Floss.App.Brushes;

public enum BrushParameterTarget
{
    Size,
    Opacity,
    Flow,
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
        return new BrushParameterGraph
        {
            Target = target,
            OutputNodeId = "output",
            Nodes =
            [
                new BrushParameterNode { Id = "constant", Kind = BrushParameterNodeKind.Constant, Value = 1f },
                new BrushParameterNode
                {
                    Id = "pressure",
                    Kind = BrushParameterNodeKind.Pressure,
                    Strength = dyn.PressureEnabled ? 1f : 0f,
                    Min = dyn.Min,
                    Max = dyn.Max,
                    Curve = dyn.CurveData.ToList()
                },
                new BrushParameterNode { Id = "mix", Kind = BrushParameterNodeKind.Multiply, Inputs = ["constant", "pressure"] },
                new BrushParameterNode { Id = "output", Kind = BrushParameterNodeKind.Output, Inputs = ["mix"] }
            ]
        };
    }
}
