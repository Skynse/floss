namespace Floss.App.Config;

/// <summary>Identifies which tool a preset creates. Modes (SelectMode, ShapeKind, etc.) refine behavior.</summary>
public enum ToolKind
{
    Brush,
    Pen,
    Eraser,
    Smudge,
    Select,
    MagicWand,
    Fill,
    LassoFill,
    Eyedropper,
    MoveLayer,
    SelectLayer,
    Object,
    Gradient,
    Shape,
    Polyline,
    Liquify,
    Assistant,
    Hand,
    Zoom,
    Rotate,
}

public static class ToolKindExtensions
{
    public static bool IsBrushFamily(this ToolKind kind)
        => kind is ToolKind.Brush or ToolKind.Pen or ToolKind.Eraser or ToolKind.Smudge;

    public static bool IsPaintTool(this ToolKind kind)
        => kind.IsBrushFamily()
            || kind is ToolKind.Fill or ToolKind.LassoFill or ToolKind.Gradient
                or ToolKind.Shape or ToolKind.Polyline;

    public static bool UsesBrushSettings(this ToolKind kind)
        => kind.IsBrushFamily() || kind == ToolKind.Liquify;

    /// <summary>Non-brush paint tools that store opacity/blend in <c>BrushOverride</c>.</summary>
    public static bool UsesPaintBrushOverride(this ToolKind kind)
        => kind is ToolKind.LassoFill or ToolKind.Fill or ToolKind.Gradient
            or ToolKind.Shape or ToolKind.Polyline;

    public static bool IsSelectionTool(this ToolKind kind)
        => kind is ToolKind.Select or ToolKind.MagicWand;

    public static bool IsViewportTool(this ToolKind kind)
        => kind is ToolKind.Hand or ToolKind.Zoom or ToolKind.Rotate;
}
