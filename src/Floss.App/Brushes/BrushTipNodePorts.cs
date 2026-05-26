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
    public static NodePortKind GetOutputKind(BrushTipNodeKind kind)
        => BrushTipNodeRegistry.GetOutputKind(kind);

    public static NodePortKind GetInputKind(BrushTipNodeKind kind, int inputIndex)
        => BrushTipNodeRegistry.GetInputKind(kind, inputIndex);

    public static bool HasInputs(BrushTipNodeKind kind)
        => BrushTipNodeRegistry.HasInputs(kind);

    public static string InputLabel(BrushTipNodeKind kind, int inputIndex)
        => BrushTipNodeRegistry.InputLabel(kind, inputIndex);

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
