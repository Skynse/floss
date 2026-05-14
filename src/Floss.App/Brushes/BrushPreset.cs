using System;
using System.Collections.Generic;
using Avalonia.Media;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes;

public enum MixingMode { Standard, Perceptual }
public enum SmudgeMode { Blend, Smear, Smudge }
public enum BrushTipDirection { Horizontal, Vertical }
public enum BrushQuality { Low, High }

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
    public BrushQuality Quality { get; init; } = BrushQuality.High;
    public IBrushTip Tip { get; init; } = new ProceduralBrushTip();
    public SkiaSharp.SKBlendMode BlendMode { get; init; } = SkiaSharp.SKBlendMode.SrcOver;
    public ProceduralBrushTip? Shape { get; init; } = null;
    public AngleSource BaseAngleSource { get; init; } = AngleSource.None;
    public float AngleJitter { get; init; } = 0f;

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
        new("Technical Pen", 8, 1.0, 0.95, 0.10, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(1.25f, 0.18f),
                Opacity = CurveOption.Off()
            },
            Smoothing = 0.45,

        },
        new("Studio Pen",  16, 0.92, 0.72, 0.13, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(1.45f, 0.28f),
                Opacity = CurveOption.PressureSpeed(1.45f, 0.16f)
            },
            Smoothing = 0.5
        },
        new("Soft Pencil", 22, 0.58, 0.28, 0.18, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(1.1f, 0.42f),
                Opacity = CurveOption.PressureSpeed(1.1f, 0.35f)
            },
            Smoothing = 0.2, Grain = 0.45
        },
        new("Marker",      34, 0.70, 0.48, 0.20, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(0.9f, 0.12f),
                Opacity = CurveOption.Off()
            },
            Flow = 0.6, Smoothing = 0.55
        },
        new("Airbrush",    48, 0.35, 0.12, 0.16, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.25, Smoothing = 0.65, Grain = 0.04
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
            Smoothing = 0.45
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
            Smoothing = 0.45
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
            Smoothing = 0.45
        },
    ];
}
