namespace Floss.App.Processes.Internal;

internal enum InputProcessType
{
    None = 0,
    Pen = 1,
    Brush = 2,
    Eraser = 3,
    Smudge = 4,
    Lasso = 5,
    Polyline = 6,
    Rect = 7,
    Click = 8,
    Drag = 9,
    Liquify = 10,
    Hand = 11,
    Rotate = 12,
    Zoom = 13,
    MoveLayer = 14,
    Object = 15,
}

internal static class InputProcessTypeExtensions
{
    public static bool IsBrushFamily(this InputProcessType t)
        => t is InputProcessType.Pen or InputProcessType.Brush or InputProcessType.Eraser or InputProcessType.Smudge;
}

internal enum OutputProcessType
{
    None = 0,
    DirectDraw,
    ClosedAreaFill,
    SelectionArea,
    FloodFill,
    Gradient,
    Eyedropper,
    MoveLayer,
    Stroke,
    Zoom,
    Hand,
    Rotate,
    MagicWand,
    Liquify,
    SelectLayer,
    Object,
}
