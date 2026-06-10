namespace Floss.App.Input;

/// <summary>
/// All canvas input actions that the input router can resolve and dispatch.
/// Superset of CanvasButtonAction — includes both mouse-button actions and
/// modifier-triggered actions like brush-size gesture and layer pick.
/// </summary>
public enum CanvasAction
{
    None,
    PrimaryTool,
    PanCanvas,
    RotateCanvas,
    ZoomCanvas,
    Eyedropper,
    MoveLayer,
    BrushSize,
    LayerPick,
}
