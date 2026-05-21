using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Floss.App.Brushes;

public enum BrushTipStorageKind
{
    Procedural = 0,
    EmbeddedPng = 1,
    NodeGraph = 3,
}

public sealed class BrushTipData
{
    public BrushTipStorageKind Kind { get; set; } = BrushTipStorageKind.Procedural;
    public BrushTipShape Shape { get; set; } = BrushTipShape.Circle;
    public float AspectRatio { get; set; } = 1.0f;
    public byte[] PngBytes { get; set; } = [];
    public BrushTipNodeGraph? NodeGraph { get; set; }

    public IBrushTip CreateTip() => Kind switch
    {
        BrushTipStorageKind.EmbeddedPng when PngBytes.Length > 0
            => new ImageBrushTip(PngBytes),
        BrushTipStorageKind.NodeGraph when NodeGraph != null
            => new NodeBrushTip(NodeGraph),
        _ => new ProceduralBrushTip(Shape, AspectRatio)
    };

    public static BrushTipData FromTip(IBrushTip tip) => tip switch
    {
        ImageBrushTip img => new BrushTipData
        {
            Kind = BrushTipStorageKind.EmbeddedPng,
            PngBytes = img.GetPngBytes()
        },
        ProceduralBrushTip proc => new BrushTipData
        {
            Kind = BrushTipStorageKind.NodeGraph,
            Shape = proc.Shape,
            AspectRatio = proc.AspectRatio,
            NodeGraph = proc.Graph.DeepClone()
        },
        NodeBrushTip node => new BrushTipData
        {
            Kind = BrushTipStorageKind.NodeGraph,
            NodeGraph = node.Graph.DeepClone()
        },
        _ => new BrushTipData()
    };

    public BrushTipData DeepClone() => Kind switch
    {
        BrushTipStorageKind.EmbeddedPng => new BrushTipData
        {
            Kind = Kind,
            PngBytes = PngBytes.ToArray()
        },
        BrushTipStorageKind.NodeGraph => new BrushTipData
        {
            Kind = Kind,
            NodeGraph = NodeGraph?.DeepClone()
        },
        _ => new BrushTipData { Kind = Kind, Shape = Shape, AspectRatio = AspectRatio }
    };
}

public sealed class BrushShapePreset
{
    public string Name { get; set; } = "";
    public BrushTipData Tip { get; set; } = new();
    public BrushTipData? ShapeData { get; set; }

    public static BrushShapePreset FromPreset(BrushPreset preset, string name) => new()
    {
        Name = name,
        Tip = BrushTipData.FromTip(preset.Tip),
        ShapeData = preset.Shape != null
            ? new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = preset.Shape.Shape, AspectRatio = preset.Shape.AspectRatio }
            : null
    };

    public BrushPreset Apply(BrushPreset source)
    {
        var tip = Tip.CreateTip();
        ProceduralBrushTip? shape = ShapeData is { Kind: BrushTipStorageKind.Procedural }
            ? new ProceduralBrushTip(ShapeData.Shape, ShapeData.AspectRatio)
            : null;
        return source with { Tip = tip, Shape = shape };
    }
}

public sealed class BrushAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public string? Category { get; set; }
    public BrushPreset Preset { get; set; } = BrushPreset.Defaults[0];
    public BrushTipData Tip { get; set; } = new();

    // Persists preset.Shape (always procedural, so only shape+aspect needed).
    public BrushTipData? ShapeData { get; set; } = null;

    public BrushPreset ToPreset()
    {
        var preset = Preset with { Tip = Tip.CreateTip(), Tips = Preset.Tips };
        if (ShapeData is { Kind: BrushTipStorageKind.Procedural })
            preset = preset with { Shape = new ProceduralBrushTip(ShapeData.Shape, ShapeData.AspectRatio) };
        return preset;
    }

    public BrushAsset WithPreset(BrushPreset preset)
    {
        Preset = preset;
        Tip = BrushTipData.FromTip(preset.Tip);
        ShapeData = preset.Shape != null
            ? new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = preset.Shape.Shape, AspectRatio = preset.Shape.AspectRatio }
            : null;
        return this;
    }

    public BrushAsset CloneForSaveAs(string name)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Category = Category,
            Preset = Preset with { Name = name },
            Tip = Tip.DeepClone(),
            ShapeData = ShapeData?.DeepClone()
        };

    public static BrushAsset FromPreset(BrushPreset preset, string? category = null)
        => new()
        {
            Id = StableId(preset.Name),
            Preset = preset,
            Tip = BrushTipData.FromTip(preset.Tip),
            Category = category
        };

    private static string StableId(string name)
        => name.Trim().ToLowerInvariant().Replace(' ', '-');
}
