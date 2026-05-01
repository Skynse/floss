using System;
using System.Collections.Generic;
using Avalonia.Media;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes;

public enum BrushKind { Ink, Pencil, Marker, Airbrush, Eraser }

public sealed record BrushPreset(
    string Name,
    BrushKind Kind,
    double Size,
    double Opacity,
    double Hardness,
    double Spacing,
    Color Color,
    double Angle)
{
    public BrushDynamics Dynamics { get; init; } = new();
    public double Flow { get; init; } = 1.0;
    // 0 = pure brush color, 1 = fully samples canvas color per dab
    public double ColorMix { get; init; } = 0.0;
    // How fast the brush reloads with fresh paint. 1 = always fresh, 0 = color accumulates
    public double ColorLoad { get; init; } = 1.0;
    public double Grain { get; init; } = 0.0;
    public double Smoothing { get; init; } = 0.3;
    public IBrushTip Tip { get; init; } = new ProceduralBrushTip();
    // Optional silhouette mask multiplied against Tip at render time.
    // Null = no extra clip (Tip stamps as-is, which is correct for procedural tips).
    public ProceduralBrushTip? Shape { get; init; } = null;
    public AngleSource BaseAngleSource { get; init; } = AngleSource.None;
    public float AngleJitter { get; init; } = 0f; // 0.0 to 1.0
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
        new("Technical Pen", BrushKind.Ink, 8, 1.0, 0.95, 0.10, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(1.25f, 0.18f),
                Opacity = CurveOption.Off()
            },
            Smoothing = 0.45,

        },
        new("Studio Pen", BrushKind.Ink, 16, 0.92, 0.72, 0.13, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(1.45f, 0.28f),
                Opacity = CurveOption.PressureSpeed(1.45f, 0.16f)
            },
            Smoothing = 0.5
        },
        new("Soft Pencil", BrushKind.Pencil, 22, 0.58, 0.28, 0.18, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(1.1f, 0.42f),
                Opacity = CurveOption.PressureSpeed(1.1f, 0.35f)
            },
            Smoothing = 0.2, Grain = 0.45
        },
        new("Marker", BrushKind.Marker, 34, 0.70, 0.48, 0.20, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.PressureSpeed(0.9f, 0.12f),
                Opacity = CurveOption.Off()
            },
            Flow = 0.6, Smoothing = 0.55
        },
        new("Airbrush", BrushKind.Airbrush, 48, 0.35, 0.12, 0.16, Color.Parse("#000000"), 100)
        {
            Dynamics = new BrushDynamics
            {
                Size    = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.25, Smoothing = 0.65, Grain = 0.04
        },
    ];
}
