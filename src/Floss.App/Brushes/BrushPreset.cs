using System.Collections.Generic;
using Avalonia.Media;

namespace Floss.App.Brushes;

public enum BrushKind { Ink, Pencil, Marker, Airbrush, Eraser }

public sealed record BrushPreset(
    string    Name,
    BrushKind Kind,
    double    Size,
    double    Opacity,
    double    Hardness,
    double    Spacing,
    Color     Color)
{
    public ParameterDynamics SizeDynamics    { get; init; } = ParameterDynamics.DefaultSize;
    public ParameterDynamics OpacityDynamics { get; init; } = ParameterDynamics.DefaultOpacity;
    public double Flow      { get; init; } = 1.0;
    public double Grain     { get; init; } = 0.0;
    public double Smoothing { get; init; } = 0.3;
    public IBrushTip Tip    { get; init; } = new ProceduralBrushTip();

    public static IReadOnlyList<BrushPreset> Defaults { get; } =
    [
        new("Technical Pen", BrushKind.Ink, 8, 1.0, 0.95, 0.10, Color.Parse("#111111"))
        {
            SizeDynamics    = new() { PressureEnabled=true, Gamma=1.25f, VelocityEnabled=true,  VelocityStrength=0.18f },
            OpacityDynamics = new() { PressureEnabled=false },
            Smoothing = 0.45
        },
        new("Studio Pen", BrushKind.Ink, 16, 0.92, 0.72, 0.13, Color.Parse("#111111"))
        {
            SizeDynamics    = new() { PressureEnabled=true, Gamma=1.45f, VelocityEnabled=true,  VelocityStrength=0.28f },
            OpacityDynamics = new() { PressureEnabled=true, Gamma=1.45f, VelocityEnabled=true,  VelocityStrength=0.16f },
            Smoothing = 0.5
        },
        new("Soft Pencil", BrushKind.Pencil, 22, 0.58, 0.28, 0.18, Color.Parse("#1c1c1c"))
        {
            SizeDynamics    = new() { PressureEnabled=true, Gamma=1.1f,  VelocityEnabled=true,  VelocityStrength=0.42f },
            OpacityDynamics = new() { PressureEnabled=true, Gamma=1.1f,  VelocityEnabled=true,  VelocityStrength=0.35f },
            Smoothing = 0.2, Grain = 0.45
        },
        new("Marker", BrushKind.Marker, 34, 0.70, 0.48, 0.20, Color.Parse("#111111"))
        {
            SizeDynamics    = new() { PressureEnabled=true, Gamma=0.9f,  VelocityEnabled=true,  VelocityStrength=0.12f },
            OpacityDynamics = new() { PressureEnabled=false },
            Flow = 0.6, Smoothing = 0.55
        },
        new("Airbrush", BrushKind.Airbrush, 48, 0.35, 0.12, 0.16, Color.Parse("#111111"))
        {
            SizeDynamics    = new() { PressureEnabled=true, Gamma=1.0f,  VelocityEnabled=false },
            OpacityDynamics = new() { PressureEnabled=true, Gamma=1.0f,  VelocityEnabled=false },
            Flow = 0.25, Smoothing = 0.65, Grain = 0.04
        },
    ];
}
