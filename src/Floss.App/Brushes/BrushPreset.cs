using System.Collections.Generic;
using Avalonia.Media;

namespace Floss.App.Brushes;

public enum BrushKind
{
    Ink,
    Pencil,
    Marker,
    Airbrush,
    Eraser
}

public sealed record BrushPreset(
    string Name,
    BrushKind Kind,
    double Size,
    double Opacity,
    double Hardness,
    double Spacing,
    double PressureCurve,
    double VelocitySize,
    double VelocityOpacity,
    Color Color)
{
    public static IReadOnlyList<BrushPreset> Defaults { get; } =
    [
        new("Technical Pen", BrushKind.Ink, 8, 1.0, 0.92, 0.10, 1.25, 0.18, 0.08, Color.Parse("#111111")),
        new("Studio Pen", BrushKind.Ink, 16, 0.92, 0.72, 0.13, 1.45, 0.28, 0.16, Color.Parse("#111111")),
        new("Soft Pencil", BrushKind.Pencil, 22, 0.58, 0.28, 0.18, 1.1, 0.42, 0.35, Color.Parse("#1c1c1c")),
        new("Marker", BrushKind.Marker, 34, 0.70, 0.48, 0.20, 0.9, 0.12, 0.05, Color.Parse("#111111")),
        new("Airbrush", BrushKind.Airbrush, 48, 0.35, 0.12, 0.16, 1.0, 0.0, 0.0, Color.Parse("#111111"))
    ];
}
