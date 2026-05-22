using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using SkiaSharp;

namespace Floss.App.Brushes;

// Serializable brush preset fields shared by brush assets and tool-preset overrides.
public sealed class BrushPresetDocument
{
    public string Name { get; set; } = "";
    public double Size { get; set; }
    public double Opacity { get; set; }
    public double Hardness { get; set; }
    public double Spacing { get; set; }
    public uint Color { get; set; }
    public double Angle { get; set; }
    public string DynamicsJson { get; set; } = "";
    public double Flow { get; set; }
    public bool ColorMix { get; set; }
    public double ColorLoad { get; set; }
    public double ColorStretch { get; set; }
    public double BlurAmount { get; set; }
    public SmudgeMode SmudgeMode { get; set; }
    public MixingMode MixingMode { get; set; }
    public double AmountOfPaint { get; set; }
    public double DensityOfPaint { get; set; }
    public double TipDensity { get; set; }
    public double TipThickness { get; set; } = 1.0;
    public BrushTipDirection TipDirection { get; set; } = BrushTipDirection.Horizontal;
    public BrushTipSelectionMode TipSelectionMode { get; set; } = BrushTipSelectionMode.Single;
    public double Grain { get; set; }
    public double Smoothing { get; set; }
    public bool AutoSpacingActive { get; set; }
    public double AutoSpacingCoeff { get; set; } = 1.0;
    public double SpeedSpacingStrength { get; set; }
    public BrushQuality Quality { get; set; } = BrushQuality.High;
    public string? Texture { get; set; }
    public SKBlendMode BlendMode { get; set; }
    public BrushDynamics.AngleSource BaseAngleSource { get; set; }
    public float AngleJitter { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public List<BrushParameterGraph> ParameterGraphs { get; set; } = [];

    public static BrushPresetDocument FromPreset(BrushPreset preset) => new()
    {
        Name = preset.Name,
        Size = preset.Size,
        Opacity = preset.Opacity,
        Hardness = preset.Hardness,
        Spacing = preset.Spacing,
        Color = preset.Color.ToUInt32(),
        Angle = preset.Angle,
        DynamicsJson = preset.Dynamics.Serialize(),
        Flow = preset.Flow,
        ColorMix = preset.ColorMix,
        ColorLoad = preset.ColorLoad,
        ColorStretch = preset.ColorStretch,
        BlurAmount = preset.BlurAmount,
        SmudgeMode = preset.SmudgeMode,
        MixingMode = preset.MixingMode,
        AmountOfPaint = preset.AmountOfPaint,
        DensityOfPaint = preset.DensityOfPaint,
        TipDensity = preset.TipDensity,
        TipThickness = preset.TipThickness,
        TipDirection = preset.TipDirection,
        TipSelectionMode = preset.TipSelectionMode,
        Grain = preset.Grain,
        Smoothing = preset.Smoothing,
        AutoSpacingActive = preset.AutoSpacingActive,
        AutoSpacingCoeff = preset.AutoSpacingCoeff,
        SpeedSpacingStrength = preset.SpeedSpacingStrength,
        Quality = preset.Quality,
        Texture = preset.Texture,
        BlendMode = preset.BlendMode,
        BaseAngleSource = preset.BaseAngleSource,
        AngleJitter = preset.AngleJitter,
        FlipHorizontal = preset.FlipHorizontal,
        FlipVertical = preset.FlipVertical,
        ParameterGraphs = preset.ParameterGraphs.Select(g => g.DeepClone()).ToList()
    };

    public BrushPreset ToPreset(BrushTipData tip, BrushTipData? shapeData, Color? colorOverride = null)
    {
        var preset = new BrushPreset(Name, Size, Opacity, Hardness, Spacing, colorOverride ?? Avalonia.Media.Color.FromUInt32(Color), Angle)
        {
            Dynamics = BrushDynamics.Deserialize(DynamicsJson),
            Flow = Flow,
            ColorMix = ColorMix,
            ColorLoad = ColorLoad,
            ColorStretch = ColorStretch,
            BlurAmount = BlurAmount,
            SmudgeMode = SmudgeMode,
            MixingMode = MixingMode,
            AmountOfPaint = AmountOfPaint,
            DensityOfPaint = DensityOfPaint,
            TipDensity = TipDensity,
            TipThickness = TipThickness <= 0 ? 1.0 : TipThickness,
            TipDirection = TipDirection,
            TipSelectionMode = TipSelectionMode,
            Grain = Grain,
            Smoothing = Smoothing,
            AutoSpacingActive = AutoSpacingActive,
            AutoSpacingCoeff = AutoSpacingCoeff <= 0 ? 1.0 : AutoSpacingCoeff,
            SpeedSpacingStrength = SpeedSpacingStrength,
            Quality = Quality,
            Texture = Texture,
            BlendMode = BlendMode,
            BaseAngleSource = BaseAngleSource,
            AngleJitter = AngleJitter,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
            ParameterGraphs = ParameterGraphs.Select(g => g.DeepClone()).ToList(),
            Tip = tip.CreateTip()
        };

        if (shapeData is { Kind: BrushTipStorageKind.Procedural })
            preset = preset with { Shape = new ProceduralBrushTip(shapeData.Shape, shapeData.AspectRatio) };

        return preset;
    }
}
