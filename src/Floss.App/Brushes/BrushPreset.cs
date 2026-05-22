using System;
using System.Collections.Generic;
using Avalonia.Media;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes;

public enum MixingMode { Standard, Perceptual }
public enum SmudgeMode { Blend, Smear, Smudge }
public enum BrushTipDirection { Horizontal, Vertical }
public enum BrushQuality { Low, High }
public enum BrushTipSelectionMode { Single, Sequential, Random }

public sealed record BrushPreset(
    string Name,
    double Size,
    double Opacity,
    double Hardness,
    double Spacing,
    Color Color,
    double Angle)
{
    public BrushDynamics Dynamics { get; init; } = new();
    public double Flow { get; init; } = 1.0;
    public bool ColorMix { get; init; } = false;
    public double ColorLoad { get; init; } = 1.0;
    public double ColorStretch { get; init; } = 0.5;
    public double BlurAmount { get; init; } = 0.0;
    public SmudgeMode SmudgeMode { get; init; } = SmudgeMode.Blend;
    public MixingMode MixingMode { get; init; } = MixingMode.Standard;
    public double AmountOfPaint { get; init; } = 1.0;
    public double DensityOfPaint { get; init; } = 1.0;
    public double TipDensity { get; init; } = 1.0;
    public double TipThickness { get; init; } = 1.0;
    public BrushTipDirection TipDirection { get; init; } = BrushTipDirection.Horizontal;
    public double Grain { get; init; } = 0.0;
    public string? Texture { get; init; } = null;
    public double Smoothing { get; init; } = 0.3;
    public bool AutoSpacingActive { get; init; } = false;
    public double AutoSpacingCoeff { get; init; } = 1.0;
    public double SpeedSpacingStrength { get; init; } = 0.0;
    public BrushQuality Quality { get; init; } = BrushQuality.High;
    public IBrushTip Tip { get; init; } = new ProceduralBrushTip();
    public SkiaSharp.SKBlendMode BlendMode { get; init; } = SkiaSharp.SKBlendMode.SrcOver;
    public ProceduralBrushTip? Shape { get; init; } = null;
    public AngleSource BaseAngleSource { get; init; } = AngleSource.None;
    public float AngleJitter { get; init; } = 0f;
    public bool FlipHorizontal { get; init; } = false;
    public bool FlipVertical { get; init; } = false;
    public IReadOnlyList<BrushTipData> Tips { get; init; } = [];
    public BrushTipSelectionMode TipSelectionMode { get; init; } = BrushTipSelectionMode.Single;
    public IReadOnlyList<BrushParameterGraph> ParameterGraphs { get; init; } = [];

    public ParameterDynamics SizeDynamics
    {
        get => BrushDynamics.ToParameterDynamics(Dynamics.Size);
        init
        {
            var dynamics = Dynamics.Clone();
            dynamics.Size = BrushDynamics.ToCurveOption(value);
            Dynamics = dynamics;
        }
    }

    public ParameterDynamics OpacityDynamics
    {
        get => BrushDynamics.ToParameterDynamics(Dynamics.Opacity);
        init
        {
            var dynamics = Dynamics.Clone();
            dynamics.Opacity = BrushDynamics.ToCurveOption(value);
            Dynamics = dynamics;
        }
    }

    public static IReadOnlyList<BrushPreset> Defaults { get; } =
    [
        new("Round Sable", 20, 0.90, 0.68, 0.11, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.55f),
                Opacity = CurveOption.Pressure(1.4f)
            },
            Flow = 0.86,
            Smoothing = 0.46,
            Tip = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.82f)
        },
        new("Technical Pen", 8, 1.0, 0.96, 0.09, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.25f),
                Opacity = CurveOption.Off()
            },
            Smoothing = 0.42,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        },
        new("Soft Round", 32, 0.78, 0.42, 0.14, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.45f),
                Opacity = CurveOption.Pressure(1.3f)
            },
            Flow = 0.72,
            Smoothing = 0.38,
            Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
        },
        new("Soft Graphite", 22, 0.58, 0.26, 0.17, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.1f),
                Opacity = CurveOption.Pressure(1.1f)
            },
            Flow = 0.74,
            Smoothing = 0.16,
            Grain = 0.52,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        },
        new("Chisel Marker", 42, 0.68, 0.46, 0.18, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(0.95f),
                Opacity = CurveOption.Off()
            },
            Flow = 0.58,
            Smoothing = 0.50,
            Tip = new ProceduralBrushTip(BrushTipShape.Rectangle, 2.8f)
        },
        new("Soft Airbrush", 64, 0.34, 0.08, 0.12, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.24,
            Smoothing = 0.62,
            Grain = 0.04,
            Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
        },
        new("Smudge",      24, 0.68, 0.75, 0.10, Color.Parse("#000000"), 0)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.58,
            ColorMix = true,
            ColorLoad = 1.0,
            ColorStretch = 0.79,
            BlurAmount = 0.81,
            MixingMode = MixingMode.Perceptual,
            AmountOfPaint = 0.74,
            DensityOfPaint = 1.0,
            TipThickness = 0.42,
            TipDirection = BrushTipDirection.Horizontal,
            SmudgeMode = SmudgeMode.Smudge,
            Smoothing = 0.45,
        },
        new("Blend",       24, 0.68, 0.75, 0.10, Color.Parse("#000000"), 0)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.58,
            ColorMix = true,
            ColorLoad = 1.0,
            ColorStretch = 0.2,
            BlurAmount = 0.81,
            AmountOfPaint = 0.0,
            DensityOfPaint = 0.0,
            TipThickness = 0.42,
            TipDirection = BrushTipDirection.Horizontal,
            SmudgeMode = SmudgeMode.Blend,
            Smoothing = 0.45,
        },
        new("Smear",       24, 0.68, 0.75, 0.10, Color.Parse("#000000"), 0)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.58,
            ColorMix = true,
            ColorLoad = 1.0,
            ColorStretch = 0.6,
            BlurAmount = 0.0,
            AmountOfPaint = 0.5,
            DensityOfPaint = 1.0,
            TipThickness = 0.42,
            TipDirection = BrushTipDirection.Horizontal,
            SmudgeMode = SmudgeMode.Smear,
            Smoothing = 0.45,
        },
    ];
}
