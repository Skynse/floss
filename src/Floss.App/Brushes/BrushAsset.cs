using System;
using System.Linq;
using Avalonia.Media;

namespace Floss.App.Brushes;

public enum BrushTipStorageKind
{
    Procedural = 0,
    EmbeddedPng = 1
}

public sealed class BrushTipData
{
    public BrushTipStorageKind Kind { get; set; } = BrushTipStorageKind.Procedural;
    public BrushTipShape Shape { get; set; } = BrushTipShape.Circle;
    public float AspectRatio { get; set; } = 1.0f;
    public byte[] PngBytes { get; set; } = [];

    public IBrushTip CreateTip()
        => Kind == BrushTipStorageKind.EmbeddedPng && PngBytes.Length > 0
            ? new ImageBrushTip(PngBytes)
            : new ProceduralBrushTip(Shape, AspectRatio);

    public static BrushTipData FromTip(IBrushTip tip)
    {
        return tip switch
        {
            ImageBrushTip imageTip => new BrushTipData
            {
                Kind = BrushTipStorageKind.EmbeddedPng,
                PngBytes = imageTip.GetPngBytes()
            },
            ProceduralBrushTip proceduralTip => new BrushTipData
            {
                Kind = BrushTipStorageKind.Procedural,
                Shape = proceduralTip.Shape,
                AspectRatio = proceduralTip.AspectRatio
            },
            _ => new BrushTipData()
        };
    }
}

public sealed class BrushAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public BrushPreset Preset { get; set; } = BrushPreset.Defaults[0];
    public BrushTipData Tip { get; set; } = new();

    public BrushPreset ToPreset()
        => Preset with { Tip = Tip.CreateTip() };

    public BrushAsset WithPreset(BrushPreset preset)
    {
        Preset = preset;
        Tip = BrushTipData.FromTip(preset.Tip);
        return this;
    }

    public BrushAsset CloneForSaveAs(string name)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Preset = Preset with { Name = name },
            Tip = new BrushTipData
            {
                Kind = Tip.Kind,
                Shape = Tip.Shape,
                AspectRatio = Tip.AspectRatio,
                PngBytes = Tip.PngBytes.ToArray()
            }
        };

    public static BrushAsset FromPreset(BrushPreset preset)
        => new BrushAsset
        {
            Id = StableId(preset.Name),
            Preset = preset,
            Tip = BrushTipData.FromTip(preset.Tip)
        };

    private static string StableId(string name)
        => name.Trim().ToLowerInvariant().Replace(' ', '-');
}
