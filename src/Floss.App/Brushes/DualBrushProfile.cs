using System;
using Avalonia.Media;
using SkiaSharp;

namespace Floss.App.Brushes;

/// <summary>
/// Full secondary brush profile (CSP-style dual brush), not the legacy <see cref="BrushPreset.Shape"/> mask.
/// Each dab combines primary + secondary stamps using the secondary tip and its own ink/edge settings.
/// </summary>
public sealed record DualBrushProfile
{
    public bool Enabled { get; init; }

    public IBrushTip Tip { get; init; } = new ProceduralBrushTip(BrushTipShape.Circle);

    /// <summary>Secondary brush size in document pixels (independent of primary).</summary>
    public double Size { get; init; } = 30;

    public double Opacity { get; init; } = 1.0;
    public double Hardness { get; init; } = 0.9;
    public double Flow { get; init; } = 1.0;
    public double Spacing { get; init; } = 0.25;
    public double Grain { get; init; }
    public string? Texture { get; init; }
    public BrushQuality Quality { get; init; } = BrushQuality.High;
    public double Angle { get; init; }
    public BrushDynamics Dynamics { get; init; } = new();

    public static DualBrushProfile Disabled { get; } = new() { Enabled = false };

    public static DualBrushProfile CreateDefault() => new()
    {
        Enabled = true,
        Tip = new ProceduralBrushTip(BrushTipShape.SoftRound),
        Size = 30,
        Opacity = 1,
        Hardness = 0.5,
        Flow = 1,
    };

    public DualBrushProfile DeepClone() => this with
    {
        Tip = Tip switch
        {
            ProceduralBrushTip p => new ProceduralBrushTip(p.Shape, p.AspectRatio),
            ImageBrushTip img => new ImageBrushTip(img.GetPngBytes()),
            NodeBrushTip node => new NodeBrushTip(node.Graph.DeepClone()),
            _ => Tip
        },
        Dynamics = Dynamics.Clone()
    };
}

/// <summary>Serializable dual-brush fields for preset documents and brush files.</summary>
public sealed class DualBrushProfileDocument
{
    public bool Enabled { get; set; }
    public BrushTipData Tip { get; set; } = new();
    public double Size { get; set; } = 30;
    public double Opacity { get; set; } = 1.0;
    public double Hardness { get; set; } = 0.9;
    public double Flow { get; set; } = 1.0;
    public double Spacing { get; set; } = 0.25;
    public double Grain { get; set; }
    public string? Texture { get; set; }
    public BrushQuality Quality { get; set; } = BrushQuality.High;
    public double Angle { get; set; }
    public string DynamicsJson { get; set; } = "";

    public static DualBrushProfileDocument FromProfile(DualBrushProfile profile) => new()
    {
        Enabled = profile.Enabled,
        Tip = BrushTipData.FromTip(profile.Tip),
        Size = profile.Size,
        Opacity = profile.Opacity,
        Hardness = profile.Hardness,
        Flow = profile.Flow,
        Spacing = profile.Spacing,
        Grain = profile.Grain,
        Texture = profile.Texture,
        Quality = profile.Quality,
        Angle = profile.Angle,
        DynamicsJson = profile.Dynamics.Serialize()
    };

    public DualBrushProfile ToProfile() => new()
    {
        Enabled = Enabled,
        Tip = Tip.CreateTip(),
        Size = Size,
        Opacity = Opacity,
        Hardness = Hardness,
        Flow = Flow,
        Spacing = Spacing,
        Grain = Grain,
        Texture = Texture,
        Quality = Quality,
        Angle = Angle,
        Dynamics = BrushDynamics.Deserialize(DynamicsJson)
    };

    public static DualBrushProfileDocument FromLegacyShape(BrushTipData shape, double primarySize, bool enabled = false)
        => new()
        {
            Enabled = enabled,
            Tip = shape.DeepClone(),
            Size = Math.Max(8, primarySize * 0.5),
            Opacity = 1,
            Hardness = 0.5,
            Flow = 1
        };

    public DualBrushProfileDocument DeepClone() => new()
    {
        Enabled = Enabled,
        Tip = Tip.DeepClone(),
        Size = Size,
        Opacity = Opacity,
        Hardness = Hardness,
        Flow = Flow,
        Spacing = Spacing,
        Grain = Grain,
        Texture = Texture,
        Quality = Quality,
        Angle = Angle,
        DynamicsJson = DynamicsJson
    };
}
