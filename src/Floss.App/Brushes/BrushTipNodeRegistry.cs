using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Brushes;

public enum NodeDescriptorCategory
{
    Coordinate,
    Generator,
    Math,
    Mask,
    Effect,
    Output
}

public sealed record NodeInputDescriptor(string Label, NodePortKind PortKind);

public sealed record NodeParam(
    string Name, float Min, float Max,
    Func<BrushTipNode, float> Get,
    Action<BrushTipNode, float> Set);

public sealed record BrushTipNodeDescriptor(
    BrushTipNodeKind Kind,
    string DisplayName,
    NodeDescriptorCategory Category,
    NodeInputDescriptor[] Inputs,
    NodePortKind OutputKind,
    NodeParam[] Params,
    bool HasImageSelector = false,
    bool HasStandalonePreview = false)
{
    public int InputCount => Inputs.Length;
    public bool HasInputs => Inputs.Length > 0;

    public NodePortKind GetInputKind(int index)
        => index < Inputs.Length ? Inputs[index].PortKind : NodePortKind.Scalar;

    public string InputLabel(int index)
        => index < Inputs.Length ? Inputs[index].Label : (index == 0 ? "A" : "B");
}

public static class BrushTipNodeRegistry
{
    private static readonly Dictionary<BrushTipNodeKind, BrushTipNodeDescriptor> _byKind;
    private static readonly List<BrushTipNodeDescriptor> _all;
    private static readonly (string GroupName, NodeDescriptorCategory Category)[] _categories;
    private static readonly IReadOnlyList<(string Name, BrushTipNodeKind[] Kinds)> _addableGroups;

    static BrushTipNodeRegistry()
    {
        _all = BuildDescriptors();
        _byKind = _all.ToDictionary(d => d.Kind);

        _categories =
        [
            ("Input / Coordinates", NodeDescriptorCategory.Coordinate),
            ("Fields / Generators", NodeDescriptorCategory.Generator),
            ("Effects", NodeDescriptorCategory.Effect),
            ("Math / Combine", NodeDescriptorCategory.Math),
            ("Mask / Remap", NodeDescriptorCategory.Mask),
        ];

        _addableGroups = _categories.Select(cat =>
            (cat.GroupName, _all.Where(d => d.Category == cat.Category)
                                .Select(d => d.Kind)
                                .ToArray())
        ).Where(g => g.Item2.Length > 0).ToList().AsReadOnly();
    }

    public static BrushTipNodeDescriptor Get(BrushTipNodeKind kind)
        => _byKind.TryGetValue(kind, out var d) ? d : UnknownDescriptor(kind);

    public static IReadOnlyList<BrushTipNodeDescriptor> All => _all;

    public static IReadOnlyList<(string Name, BrushTipNodeKind[] Kinds)> AddableGroups => _addableGroups;

    public static string DisplayName(BrushTipNodeKind kind) => Get(kind).DisplayName;
    public static int InputCount(BrushTipNodeKind kind) => Get(kind).InputCount;
    public static bool HasInputs(BrushTipNodeKind kind) => Get(kind).HasInputs;
    public static NodePortKind GetOutputKind(BrushTipNodeKind kind) => Get(kind).OutputKind;
    public static NodePortKind GetInputKind(BrushTipNodeKind kind, int index) => Get(kind).GetInputKind(index);
    public static string InputLabel(BrushTipNodeKind kind, int index) => Get(kind).InputLabel(index);
    public static NodeParam[] Params(BrushTipNodeKind kind) => Get(kind).Params;
    public static bool HasImageSelector(BrushTipNodeKind kind) => Get(kind).HasImageSelector;
    public static bool HasStandalonePreview(BrushTipNodeKind kind) => Get(kind).HasStandalonePreview;

    private static BrushTipNodeDescriptor UnknownDescriptor(BrushTipNodeKind kind)
        => new(kind, kind.ToString(), NodeDescriptorCategory.Generator,
               [], NodePortKind.Scalar, []);

    public static NodeDescriptorCategory CategoryOf(BrushTipNodeKind kind) => Get(kind).Category;

    // ── Descriptor definitions ──────────────────────────────────────────────

    private static NodeParam[] P(string name, float min, float max,
        Func<BrushTipNode, float> get, Action<BrushTipNode, float> set)
        => [new NodeParam(name, min, max, get, set)];

    private static NodeParam[] PArray(params NodeParam[] p) => p;

    // Shorthand property accessors
    private static readonly Func<BrushTipNode, float> Gx = n => n.X;
    private static readonly Func<BrushTipNode, float> Gy = n => n.Y;
    private static readonly Func<BrushTipNode, float> Gw = n => n.Width;
    private static readonly Func<BrushTipNode, float> Gh = n => n.Height;
    private static readonly Func<BrushTipNode, float> Gr = n => n.Radius;
    private static readonly Func<BrushTipNode, float> Gs = n => n.Scale;
    private static readonly Func<BrushTipNode, float> Gd = n => n.Density;
    private static readonly Func<BrushTipNode, float> Go = n => n.Opacity;
    private static readonly Func<BrushTipNode, float> Ghard = n => n.Hardness;
    private static readonly Func<BrushTipNode, float> Grot = n => n.RotationDegrees;
    private static readonly Func<BrushTipNode, float> Gthr = n => n.Threshold;
    private static readonly Func<BrushTipNode, float> Gseed = n => n.Seed;

    private static readonly Action<BrushTipNode, float> Sx = (n, v) => n.X = v;
    private static readonly Action<BrushTipNode, float> Sy = (n, v) => n.Y = v;
    private static readonly Action<BrushTipNode, float> Sw = (n, v) => n.Width = v;
    private static readonly Action<BrushTipNode, float> Sh = (n, v) => n.Height = v;
    private static readonly Action<BrushTipNode, float> Sr = (n, v) => n.Radius = v;
    private static readonly Action<BrushTipNode, float> Ss = (n, v) => n.Scale = v;
    private static readonly Action<BrushTipNode, float> Sd = (n, v) => n.Density = v;
    private static readonly Action<BrushTipNode, float> So = (n, v) => n.Opacity = v;
    private static readonly Action<BrushTipNode, float> Shard = (n, v) => n.Hardness = v;
    private static readonly Action<BrushTipNode, float> Srot = (n, v) => n.RotationDegrees = v;
    private static readonly Action<BrushTipNode, float> Sthr = (n, v) => n.Threshold = v;

    private static NodeParam Opt => new("Opacity", 0f, 1f, Go, So);
    private static NodeParam Rot => new("Rotation", -180f, 180f, Grot, Srot);
    private static NodeParam Hard => new("Hardness", 0f, 1f, Ghard, Shard);
    private static NodeParam Wid => new("Width", 0f, 1f, Gw, Sw);
    private static NodeParam Hei => new("Height", 0f, 1f, Gh, Sh);

    private static NodeInputDescriptor V(string label) => new(label, NodePortKind.Vector);
    private static NodeInputDescriptor S(string label) => new(label, NodePortKind.Scalar);

    private static List<BrushTipNodeDescriptor> BuildDescriptors()
    {
        var list = new List<BrushTipNodeDescriptor>
        {
            new(BrushTipNodeKind.Output, "Output", NodeDescriptorCategory.Output,
                [S("mask")], NodePortKind.Scalar, []),

            // ── Coordinates ──────────────────────────────────────────────
            new(BrushTipNodeKind.Coordinates, "UV Map", NodeDescriptorCategory.Coordinate,
                [], NodePortKind.Vector, [], HasStandalonePreview: true),

            new(BrushTipNodeKind.RotateCoordinates, "Rotate Coord", NodeDescriptorCategory.Coordinate,
                [V("coord")], NodePortKind.Vector, PArray(
                    new("Center X", 0f, 1f, Gx, Sx),
                    new("Center Y", 0f, 1f, Gy, Sy),
                    new("Scale", 0.05f, 8f, Gs, Ss),
                    Rot)),

            new(BrushTipNodeKind.WarpCoordinates, "Warp Coord", NodeDescriptorCategory.Coordinate,
                [V("coord"), S("warp")], NodePortKind.Vector,             PArray(
                    new("Amount", 0f, 1f, Gd, Sd),
                    new("X Amp", 0f, 1f, Gw, Sw),
                    new("Y Amp", 0f, 1f, Gh, Sh),
                    new("Direction", -180f, 180f, Grot, Srot))),

            new(BrushTipNodeKind.PolarRadius, "Polar Radius", NodeDescriptorCategory.Coordinate,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Center X", 0f, 1f, Gx, Sx),
                    new("Center Y", 0f, 1f, Gy, Sy),
                    Wid, Hei,
                    new("Scale", 0.05f, 16f, Gs, Ss))),

            new(BrushTipNodeKind.PolarAngle, "Polar Angle", NodeDescriptorCategory.Coordinate,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Center X", 0f, 1f, Gx, Sx),
                    new("Center Y", 0f, 1f, Gy, Sy),
                    new("Repeats", 0.05f, 64f, Gs, Ss),
                    new("Phase", -180f, 180f, Grot, Srot))),

            new(BrushTipNodeKind.Value, "Value", NodeDescriptorCategory.Coordinate,
                [], NodePortKind.Scalar, [new("Value", 0f, 1f, Go, So)]),

            // ── Generators ───────────────────────────────────────────────
            new(BrushTipNodeKind.Circle, "Circle", NodeDescriptorCategory.Generator,
                [], NodePortKind.Scalar,             PArray(
                    new("Radius", 0f, 1f, Gr, Sr), Hard, Opt, Rot)),

            new(BrushTipNodeKind.Rectangle, "Rectangle", NodeDescriptorCategory.Generator,
                [], NodePortKind.Scalar,             PArray(Wid, Hei, Hard, Opt, Rot)),

            new(BrushTipNodeKind.RoundedRectangle, "Round Rect", NodeDescriptorCategory.Generator,
                [], NodePortKind.Scalar,             PArray(
                    Wid, Hei,
                    new("Radius", 0f, 1f, Gr, Sr), Hard, Opt, Rot)),

            new(BrushTipNodeKind.DistanceField, "Ellipse Field", NodeDescriptorCategory.Generator,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Center X", 0f, 1f, Gx, Sx),
                    new("Center Y", 0f, 1f, Gy, Sy),
                    Wid, Hei, Rot)),

            new(BrushTipNodeKind.BoxDistanceField, "Box Field", NodeDescriptorCategory.Generator,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Center X", 0f, 1f, Gx, Sx),
                    new("Center Y", 0f, 1f, Gy, Sy),
                    Wid, Hei, Rot)),

            new(BrushTipNodeKind.LinearGradient, "Linear Grad", NodeDescriptorCategory.Generator,
                [V("coord")], NodePortKind.Scalar, [Opt]),

            new(BrushTipNodeKind.Stripe, "Stripe", NodeDescriptorCategory.Generator,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Scale", 1f, 128f, Gs, Ss),
                    new("Density", 0f, 1f, Gd, Sd), Hard, Opt, Rot)),

            new(BrushTipNodeKind.ImageSampler, "Image Sampler", NodeDescriptorCategory.Generator,
                [], NodePortKind.Scalar, [Opt], HasImageSelector: true),

            new(BrushTipNodeKind.TextureStamp, "Texture Stamp", NodeDescriptorCategory.Generator,
                [], NodePortKind.Scalar,             PArray(
                    new("Scale", 0.05f, 8f, Gs, Ss),
                    new("Density", 0f, 1f, Gd, Sd))),

            new(BrushTipNodeKind.Noise, "Noise", NodeDescriptorCategory.Generator,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Density", 0f, 1f, Gd, Sd),
                    new("Scale", 0.05f, 64f, Gs, Ss), Opt)),

            new(BrushTipNodeKind.Bristle, "Bristle", NodeDescriptorCategory.Generator,
                [], NodePortKind.Scalar,             PArray(
                    new("Density", 0f, 1f, Gd, Sd), Wid, Hei, Hard, Opt)),

            // ── Effects ──────────────────────────────────────────────────
            new(BrushTipNodeKind.Erosion, "Erosion", NodeDescriptorCategory.Effect,
                [S("input")], NodePortKind.Scalar, [new("Radius", 0f, 1f, Gr, Sr)]),

            new(BrushTipNodeKind.DirectionalBlur, "Directional Blur", NodeDescriptorCategory.Effect,
                [S("input")], NodePortKind.Scalar,             PArray(
                    new("Angle", -180f, 180f, Grot, Srot),
                    new("Radius", 0f, 1f, Gr, Sr))),

            new(BrushTipNodeKind.RaggedEdge, "Ragged Edge", NodeDescriptorCategory.Effect,
                [S("input")], NodePortKind.Scalar,             PArray(
                    new("Strength", 0f, 1f, Gd, Sd),
                    new("Frequency", 0.5f, 64f, Gs, Ss))),

            new(BrushTipNodeKind.IsotropicBlur, "Blur", NodeDescriptorCategory.Effect,
                [S("input")], NodePortKind.Scalar, [new("Radius", 0f, 1f, Gr, Sr)]),

            new(BrushTipNodeKind.EdgeDetect, "Edge Detect", NodeDescriptorCategory.Effect,
                [S("input")], NodePortKind.Scalar, [new("Strength", 0f, 1f, Go, So)]),

            new(BrushTipNodeKind.Perlin, "Perlin", NodeDescriptorCategory.Generator,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Scale", 0.5f, 32f, Gs, Ss),
                    new("Detail", 0f, 1f, Gd, Sd),
                    Opt)),

            new(BrushTipNodeKind.Voronoi, "Voronoi", NodeDescriptorCategory.Generator,
                [V("coord")], NodePortKind.Scalar,             PArray(
                    new("Scale", 0.5f, 32f, Gs, Ss),
                    new("Density", 0f, 1f, Gd, Sd),
                    Opt)),

            new(BrushTipNodeKind.Transform, "Transform", NodeDescriptorCategory.Coordinate,
                [V("coord")], NodePortKind.Vector,             PArray(
                    new("Scale", 0.1f, 8f, Gs, Ss),
                    new("Offset X", -1f, 1f, Gx, Sx),
                    new("Offset Y", -1f, 1f, Gy, Sy))),

            new(BrushTipNodeKind.Remap, "Remap", NodeDescriptorCategory.Mask,
                [S("input")], NodePortKind.Scalar, PArray(
                    new("In Min", 0f, 1f, Gthr, Sthr),
                    new("In Max", 0f, 1f, Ghard, Shard),
                    new("Out Min", 0f, 1f, Go, So),
                    new("Out Max", 0f, 1f, Gs, Ss))),

            // ── Math / Combine ───────────────────────────────────────────
            new(BrushTipNodeKind.Add, "Add", NodeDescriptorCategory.Math,
                [S("A"), S("B")], NodePortKind.Scalar, []),

            new(BrushTipNodeKind.Subtract, "Subtract", NodeDescriptorCategory.Math,
                [S("A"), S("B")], NodePortKind.Scalar, []),

            new(BrushTipNodeKind.Multiply, "Multiply", NodeDescriptorCategory.Math,
                [S("A"), S("B")], NodePortKind.Scalar, []),

            new(BrushTipNodeKind.Max, "Max", NodeDescriptorCategory.Math,
                [S("A"), S("B")], NodePortKind.Scalar, []),

            new(BrushTipNodeKind.Min, "Min", NodeDescriptorCategory.Math,
                [S("A"), S("B")], NodePortKind.Scalar, []),

            new(BrushTipNodeKind.Mix, "Mix", NodeDescriptorCategory.Math,
                [S("A"), S("B")], NodePortKind.Scalar,             PArray(
                    new("Factor", 0f, 1f, Gd, Sd), Opt)),

            // ── Mask / Remap ─────────────────────────────────────────────
            new(BrushTipNodeKind.Threshold, "Threshold", NodeDescriptorCategory.Mask,
                [S("input")], NodePortKind.Scalar,             PArray(
                    new("Threshold", 0f, 1f, Gthr, Sthr), Opt)),

            new(BrushTipNodeKind.SmoothStep, "Smooth Step", NodeDescriptorCategory.Mask,
                [S("input")], NodePortKind.Scalar,             PArray(
                    new("Edge", 0f, 1f, Gthr, Sthr),
                    new("Softness", 0.001f, 1f, Ghard, Shard), Opt)),

            new(BrushTipNodeKind.Power, "Power", NodeDescriptorCategory.Mask,
                [S("input")], NodePortKind.Scalar,             PArray(
                    new("Exponent", 0.05f, 16f, Gs, Ss), Opt)),

            new(BrushTipNodeKind.Sine, "Sine", NodeDescriptorCategory.Mask,
                [S("input")], NodePortKind.Scalar,             PArray(
                    new("Frequency", 0.05f, 64f, Gs, Ss),
                    new("Phase", 0f, 1f, Gx, Sx), Opt)),

            new(BrushTipNodeKind.Absolute, "Absolute", NodeDescriptorCategory.Mask,
                [S("input")], NodePortKind.Scalar,             PArray(
                    new("Center", 0f, 1f, Gx, Sx), Opt)),

            new(BrushTipNodeKind.Invert, "Invert", NodeDescriptorCategory.Mask,
                [S("input")], NodePortKind.Scalar, [Opt]),
        };

        return list;
    }
}
