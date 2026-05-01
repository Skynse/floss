using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Floss.App.Brushes;

public enum BrushTipStorageKind
{
    Procedural = 0,
    EmbeddedPng = 1,
    Compound = 2
}

public sealed class StampLayerData
{
    public BrushTipData Tip { get; set; } = new();
    public StampLayerBlend Blend { get; set; } = StampLayerBlend.Replace;
    public float Opacity { get; set; } = 1.0f;
    public float Scale { get; set; } = 1.0f;
    public float Rotation { get; set; } = 0.0f;
}

public sealed class BrushTipData
{
    public BrushTipStorageKind Kind { get; set; } = BrushTipStorageKind.Procedural;
    public BrushTipShape Shape { get; set; } = BrushTipShape.Circle;
    public float AspectRatio { get; set; } = 1.0f;
    public byte[] PngBytes { get; set; } = [];
    public List<StampLayerData> SubLayers { get; set; } = [];

    public IBrushTip CreateTip() => Kind switch
    {
        BrushTipStorageKind.EmbeddedPng when PngBytes.Length > 0
            => new ImageBrushTip(PngBytes),
        BrushTipStorageKind.Compound when SubLayers.Count > 0
            => new CompoundBrushTip(SubLayers.Select(l => new StampLayer
            {
                Tip = l.Tip.CreateTip(),
                Blend = l.Blend,
                Opacity = l.Opacity,
                Scale = l.Scale,
                Rotation = l.Rotation
            }).ToList()),
        _ => new ProceduralBrushTip(Shape, AspectRatio)
    };

    public static BrushTipData FromTip(IBrushTip tip) => tip switch
    {
        ImageBrushTip img => new BrushTipData
        {
            Kind = BrushTipStorageKind.EmbeddedPng,
            PngBytes = img.GetPngBytes()
        },
        CompoundBrushTip compound => new BrushTipData
        {
            Kind = BrushTipStorageKind.Compound,
            SubLayers = compound.Layers.Select(l => new StampLayerData
            {
                Tip = FromTip(l.Tip),
                Blend = l.Blend,
                Opacity = l.Opacity,
                Scale = l.Scale,
                Rotation = l.Rotation
            }).ToList()
        },
        ProceduralBrushTip proc => new BrushTipData
        {
            Kind = BrushTipStorageKind.Procedural,
            Shape = proc.Shape,
            AspectRatio = proc.AspectRatio
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
        BrushTipStorageKind.Compound => new BrushTipData
        {
            Kind = Kind,
            SubLayers = SubLayers.Select(l => new StampLayerData
            {
                Tip = l.Tip.DeepClone(),
                Blend = l.Blend,
                Opacity = l.Opacity,
                Scale = l.Scale,
                Rotation = l.Rotation
            }).ToList()
        },
        _ => new BrushTipData { Kind = Kind, Shape = Shape, AspectRatio = AspectRatio }
    };
}

public sealed class BrushAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public BrushPreset Preset { get; set; } = BrushPreset.Defaults[0];
    public BrushTipData Tip { get; set; } = new();

    // Persists preset.Shape (always procedural, so only shape+aspect needed).
    public BrushTipData? ShapeData { get; set; } = null;

    public BrushPreset ToPreset()
    {
        var preset = Preset with { Tip = Tip.CreateTip() };
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
            Preset = Preset with { Name = name },
            Tip = Tip.DeepClone(),
            ShapeData = ShapeData == null ? null : new BrushTipData { Kind = BrushTipStorageKind.Procedural, Shape = ShapeData.Shape, AspectRatio = ShapeData.AspectRatio }
        };

    public static BrushAsset FromPreset(BrushPreset preset)
        => new()
        {
            Id = StableId(preset.Name),
            Preset = preset,
            Tip = BrushTipData.FromTip(preset.Tip)
        };

    private static string StableId(string name)
        => name.Trim().ToLowerInvariant().Replace(' ', '-');
}
