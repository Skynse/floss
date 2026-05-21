namespace Floss.App.Brushes;

using System.Globalization;

public enum NodePortKind
{
    Scalar,
    Vector
}

/// <summary>
/// Port compatibility rules for the single-channel / vector node graph.
/// Vector ports carry per-pixel UV coordinates; scalar ports carry mask values.
/// </summary>
public static class BrushTipNodePorts
{
    public static NodePortKind GetOutputKind(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Coordinates or BrushTipNodeKind.RotateCoordinates
            or BrushTipNodeKind.WarpCoordinates => NodePortKind.Vector,
        _ => NodePortKind.Scalar
    };

    public static NodePortKind GetInputKind(BrushTipNodeKind kind, int inputIndex) => kind switch
    {
        BrushTipNodeKind.Output => NodePortKind.Scalar,
        BrushTipNodeKind.RotateCoordinates or BrushTipNodeKind.PolarRadius
            or BrushTipNodeKind.PolarAngle => NodePortKind.Vector,
        BrushTipNodeKind.WarpCoordinates when inputIndex == 0 => NodePortKind.Vector,
        BrushTipNodeKind.WarpCoordinates => NodePortKind.Scalar,
        BrushTipNodeKind.DistanceField or BrushTipNodeKind.BoxDistanceField
            or BrushTipNodeKind.LinearGradient or BrushTipNodeKind.Stripe
            or BrushTipNodeKind.Noise => NodePortKind.Vector,
        _ => NodePortKind.Scalar
    };

    public static bool HasInputs(BrushTipNodeKind kind) => kind switch
    {
        BrushTipNodeKind.Output => true,
        BrushTipNodeKind.RotateCoordinates or BrushTipNodeKind.PolarRadius
            or BrushTipNodeKind.PolarAngle => true,
        BrushTipNodeKind.WarpCoordinates => true,
        BrushTipNodeKind.DistanceField or BrushTipNodeKind.BoxDistanceField
            or BrushTipNodeKind.LinearGradient or BrushTipNodeKind.Stripe
            or BrushTipNodeKind.Noise => true,
        BrushTipNodeKind.Threshold or BrushTipNodeKind.SmoothStep
            or BrushTipNodeKind.Invert or BrushTipNodeKind.Power
            or BrushTipNodeKind.Sine or BrushTipNodeKind.Absolute => true,
        BrushTipNodeKind.Add or BrushTipNodeKind.Multiply or BrushTipNodeKind.Max
            or BrushTipNodeKind.Min or BrushTipNodeKind.Subtract or BrushTipNodeKind.Mix => true,
        _ => false
    };

    public static string InputLabel(BrushTipNodeKind kind, int inputIndex) => kind switch
    {
        BrushTipNodeKind.WarpCoordinates when inputIndex == 1 => "warp",
        BrushTipNodeKind.RotateCoordinates or BrushTipNodeKind.PolarRadius
            or BrushTipNodeKind.PolarAngle
            or BrushTipNodeKind.DistanceField or BrushTipNodeKind.BoxDistanceField
            or BrushTipNodeKind.LinearGradient or BrushTipNodeKind.Stripe
            or BrushTipNodeKind.Noise => "coord",
        BrushTipNodeKind.Mix when inputIndex == 0 => "A",
        BrushTipNodeKind.Mix when inputIndex == 1 => "B",
        BrushTipNodeKind.Add or BrushTipNodeKind.Multiply or BrushTipNodeKind.Max
            or BrushTipNodeKind.Min or BrushTipNodeKind.Subtract => inputIndex == 0 ? "A" : "B",
        BrushTipNodeKind.Output => "mask",
        _ => inputIndex == 0 ? "A" : "B"
    };

    public static bool CanConnect(BrushTipNodeKind sourceKind, BrushTipNodeKind targetKind, int targetInputIndex)
    {
        if (targetKind == BrushTipNodeKind.Output && targetInputIndex != 0)
            return false;
        if (sourceKind == BrushTipNodeKind.Output)
            return false;

        var outputKind = GetOutputKind(sourceKind);
        var inputKind = GetInputKind(targetKind, targetInputIndex);
        return outputKind == inputKind;
    }

    public static string FormatDisplayValue(float value)
        => value.ToString("F2", CultureInfo.InvariantCulture);
}
