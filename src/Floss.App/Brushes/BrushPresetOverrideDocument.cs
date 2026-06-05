using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace Floss.App.Brushes;

public enum BrushShapeOverrideMode
{
    Unset,
    Null,
    Value
}

// Per-sub-tool brush state layered on top of a brush asset. Null scalar fields are ignored on apply.
public sealed class BrushPresetOverrideDocument
{
    public double? Size { get; set; }
    public double? Opacity { get; set; }
    public double? Hardness { get; set; }
    public double? Spacing { get; set; }
    public double? Angle { get; set; }
    public double? Flow { get; set; }
    public double? Grain { get; set; }
    public double? Smoothing { get; set; }
    public bool? SpeedAdaptiveStabilizer { get; set; }
    public double? MaxSizePercent { get; set; }
    public bool? AutoSpacingActive { get; set; }
    public double? AutoSpacingCoeff { get; set; }
    public BrushGapMode? GapMode { get; set; }
    public bool? ColorMix { get; set; }
    public double? ColorLoad { get; set; }
    public double? ColorStretch { get; set; }
    public double? BlurAmount { get; set; }
    public SmudgeMode? SmudgeMode { get; set; }
    public MixingMode? MixingMode { get; set; }
    public double? AmountOfPaint { get; set; }
    public double? DensityOfPaint { get; set; }
    public double? TipDensity { get; set; }
    public double? TipThickness { get; set; }
    public BrushTipDirection? TipDirection { get; set; }
    public BrushTipSelectionMode? TipSelectionMode { get; set; }
    public BrushQuality? Quality { get; set; }
    public string? Texture { get; set; }
    public SKBlendMode? BlendMode { get; set; }
    public BrushDynamics.AngleSource? BaseAngleSource { get; set; }
    public float? AngleJitter { get; set; }
    public bool? FlipHorizontal { get; set; }
    public bool? FlipVertical { get; set; }
    public string? DynamicsJson { get; set; }
    public BrushTipData? Tip { get; set; }
    public List<BrushTipData>? Tips { get; set; }
    public BrushTipData? Shape { get; set; }
    public BrushShapeOverrideMode ShapeOverride { get; set; } = BrushShapeOverrideMode.Unset;
    public List<BrushParameterGraph>? ParameterGraphs { get; set; }

    public bool HasContent =>
        Size.HasValue || Opacity.HasValue || Hardness.HasValue || Spacing.HasValue || Angle.HasValue ||
        Flow.HasValue || Grain.HasValue || Smoothing.HasValue || SpeedAdaptiveStabilizer.HasValue || MaxSizePercent.HasValue ||
        AutoSpacingActive.HasValue || AutoSpacingCoeff.HasValue || GapMode.HasValue || ColorMix.HasValue || ColorLoad.HasValue ||
        ColorStretch.HasValue || BlurAmount.HasValue || SmudgeMode.HasValue || MixingMode.HasValue ||
        AmountOfPaint.HasValue || DensityOfPaint.HasValue || TipDensity.HasValue || TipThickness.HasValue ||
        TipDirection.HasValue || TipSelectionMode.HasValue || Quality.HasValue || Texture != null ||
        BlendMode.HasValue || BaseAngleSource.HasValue || AngleJitter.HasValue || FlipHorizontal.HasValue ||
        FlipVertical.HasValue || !string.IsNullOrEmpty(DynamicsJson) || Tip != null || Tips != null ||
        ShapeOverride != BrushShapeOverrideMode.Unset || ParameterGraphs != null;

    public static BrushPresetOverrideDocument FromPreset(BrushPreset preset)
    {
        var doc = BrushPresetDocument.FromPreset(preset);
        return new BrushPresetOverrideDocument
        {
            Size = doc.Size,
            Opacity = doc.Opacity,
            Hardness = doc.Hardness,
            Spacing = doc.Spacing,
            Angle = doc.Angle,
            Flow = doc.Flow,
            Grain = doc.Grain,
            Smoothing = doc.Smoothing,
            SpeedAdaptiveStabilizer = doc.SpeedAdaptiveStabilizer,
            MaxSizePercent = doc.MaxSizePercent,
            AutoSpacingActive = doc.AutoSpacingActive,
            AutoSpacingCoeff = doc.AutoSpacingCoeff,
            GapMode = doc.GapMode,
            ColorMix = doc.ColorMix,
            ColorLoad = doc.ColorLoad,
            ColorStretch = doc.ColorStretch,
            BlurAmount = doc.BlurAmount,
            SmudgeMode = doc.SmudgeMode,
            MixingMode = doc.MixingMode,
            AmountOfPaint = doc.AmountOfPaint,
            DensityOfPaint = doc.DensityOfPaint,
            TipDensity = doc.TipDensity,
            TipThickness = doc.TipThickness,
            TipDirection = doc.TipDirection,
            TipSelectionMode = doc.TipSelectionMode,
            Quality = doc.Quality,
            Texture = doc.Texture,
            BlendMode = doc.BlendMode,
            BaseAngleSource = doc.BaseAngleSource,
            AngleJitter = doc.AngleJitter,
            FlipHorizontal = doc.FlipHorizontal,
            FlipVertical = doc.FlipVertical,
            DynamicsJson = doc.DynamicsJson,
            Tip = BrushTipData.FromTip(preset.Tip),
            Tips = preset.Tips.Select(t => t.DeepClone()).ToList(),
            ShapeOverride = preset.Shape == null
                ? BrushShapeOverrideMode.Null
                : BrushShapeOverrideMode.Value,
            Shape = preset.Shape == null
                ? null
                : new BrushTipData
                {
                    Kind = BrushTipStorageKind.Procedural,
                    Shape = preset.Shape.Shape,
                    AspectRatio = preset.Shape.AspectRatio
                },
            ParameterGraphs = preset.ParameterGraphs.Select(g => g.DeepClone()).ToList()
        };
    }

    public BrushPreset ApplyTo(BrushPreset basePreset)
    {
        if (!HasContent) return basePreset;

        var result = basePreset with
        {
            Size = Size ?? basePreset.Size,
            Opacity = Opacity ?? basePreset.Opacity,
            Hardness = Hardness ?? basePreset.Hardness,
            Spacing = Spacing ?? basePreset.Spacing,
            Angle = Angle ?? basePreset.Angle,
            Flow = Flow ?? basePreset.Flow,
            Grain = Grain ?? basePreset.Grain,
            Smoothing = Smoothing ?? basePreset.Smoothing,
            SpeedAdaptiveStabilizer = SpeedAdaptiveStabilizer ?? basePreset.SpeedAdaptiveStabilizer,
            MaxSizePercent = MaxSizePercent ?? basePreset.MaxSizePercent,
            AutoSpacingActive = AutoSpacingActive ?? basePreset.AutoSpacingActive,
            AutoSpacingCoeff = AutoSpacingCoeff ?? basePreset.AutoSpacingCoeff,
            GapMode = GapMode ?? basePreset.GapMode,
            ColorMix = ColorMix ?? basePreset.ColorMix,
            ColorLoad = ColorLoad ?? basePreset.ColorLoad,
            ColorStretch = ColorStretch ?? basePreset.ColorStretch,
            BlurAmount = BlurAmount ?? basePreset.BlurAmount,
            SmudgeMode = SmudgeMode ?? basePreset.SmudgeMode,
            MixingMode = MixingMode ?? basePreset.MixingMode,
            AmountOfPaint = AmountOfPaint ?? basePreset.AmountOfPaint,
            DensityOfPaint = DensityOfPaint ?? basePreset.DensityOfPaint,
            TipDensity = TipDensity ?? basePreset.TipDensity,
            TipThickness = TipThickness ?? basePreset.TipThickness,
            TipDirection = TipDirection ?? basePreset.TipDirection,
            TipSelectionMode = TipSelectionMode ?? basePreset.TipSelectionMode,
            Quality = Quality ?? basePreset.Quality,
            Texture = Texture ?? basePreset.Texture,
            BlendMode = BlendMode ?? basePreset.BlendMode,
            BaseAngleSource = BaseAngleSource ?? basePreset.BaseAngleSource,
            AngleJitter = AngleJitter ?? basePreset.AngleJitter,
            FlipHorizontal = FlipHorizontal ?? basePreset.FlipHorizontal,
            FlipVertical = FlipVertical ?? basePreset.FlipVertical,
            Tip = Tip?.CreateTip() ?? basePreset.Tip,
            Tips = Tips?.Select(t => t.DeepClone()).ToList() ?? basePreset.Tips,
            Shape = ShapeOverride switch
            {
                BrushShapeOverrideMode.Null => null,
                BrushShapeOverrideMode.Value when Shape is { Kind: BrushTipStorageKind.Procedural } shape
                    => new ProceduralBrushTip(shape.Shape, shape.AspectRatio),
                _ => basePreset.Shape
            },
            ParameterGraphs = ParameterGraphs?.Select(g => g.DeepClone()).ToList() ?? basePreset.ParameterGraphs
        };

        if (!string.IsNullOrEmpty(DynamicsJson))
        {
            try { result = result with { Dynamics = BrushDynamics.Deserialize(DynamicsJson) }; }
            catch { /* keep base dynamics on parse failure */ }
        }

        return result;
    }

    public BrushPresetOverrideDocument DeepClone() => new()
    {
        Size = Size,
        Opacity = Opacity,
        Hardness = Hardness,
        Spacing = Spacing,
        Angle = Angle,
        Flow = Flow,
        Grain = Grain,
        Smoothing = Smoothing,
        SpeedAdaptiveStabilizer = SpeedAdaptiveStabilizer,
        MaxSizePercent = MaxSizePercent,
        AutoSpacingActive = AutoSpacingActive,
        AutoSpacingCoeff = AutoSpacingCoeff,
        GapMode = GapMode,
        ColorMix = ColorMix,
        ColorLoad = ColorLoad,
        ColorStretch = ColorStretch,
        BlurAmount = BlurAmount,
        SmudgeMode = SmudgeMode,
        MixingMode = MixingMode,
        AmountOfPaint = AmountOfPaint,
        DensityOfPaint = DensityOfPaint,
        TipDensity = TipDensity,
        TipThickness = TipThickness,
        TipDirection = TipDirection,
        TipSelectionMode = TipSelectionMode,
        Quality = Quality,
        Texture = Texture,
        BlendMode = BlendMode,
        BaseAngleSource = BaseAngleSource,
        AngleJitter = AngleJitter,
        FlipHorizontal = FlipHorizontal,
        FlipVertical = FlipVertical,
        DynamicsJson = DynamicsJson,
        Tip = Tip?.DeepClone(),
        Tips = Tips?.Select(t => t.DeepClone()).ToList(),
        Shape = Shape?.DeepClone(),
        ShapeOverride = ShapeOverride,
        ParameterGraphs = ParameterGraphs?.Select(g => g.DeepClone()).ToList()
    };
}
