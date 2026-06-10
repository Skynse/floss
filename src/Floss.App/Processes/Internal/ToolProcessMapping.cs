using Floss.App.Config;
using Floss.App.Tools;

namespace Floss.App.Processes.Internal;

/// <summary>Maps public ToolKind + preset modes to internal input/output process types.</summary>
internal static class ToolProcessMapping
{
    public static InputProcessType GetInput(ToolPreset preset) => preset.Kind switch
    {
        ToolKind.Brush => InputProcessType.Brush,
        ToolKind.Pen => InputProcessType.Pen,
        ToolKind.Eraser => InputProcessType.Eraser,
        ToolKind.Smudge => InputProcessType.Smudge,
        ToolKind.Select => preset.SelectMode switch
        {
            SelectMode.Rect => InputProcessType.Rect,
            SelectMode.Lasso => InputProcessType.Lasso,
            SelectMode.PolylineLasso => InputProcessType.Polyline,
            _ => InputProcessType.Rect
        },
        ToolKind.MagicWand => InputProcessType.Click,
        ToolKind.Fill => InputProcessType.Click,
        ToolKind.LassoFill => InputProcessType.Lasso,
        ToolKind.Eyedropper => InputProcessType.Click,
        ToolKind.MoveLayer => InputProcessType.MoveLayer,
        ToolKind.SelectLayer => InputProcessType.Rect,
        ToolKind.Object => InputProcessType.Object,
        ToolKind.Gradient => InputProcessType.Drag,
        ToolKind.Shape => InputProcessType.Rect,
        ToolKind.Polyline => InputProcessType.Polyline,
        ToolKind.Liquify => InputProcessType.Liquify,
        ToolKind.Assistant => InputProcessType.Drag,
        ToolKind.Hand => InputProcessType.Hand,
        ToolKind.Zoom => InputProcessType.Zoom,
        ToolKind.Rotate => InputProcessType.Rotate,
        _ => InputProcessType.Brush
    };

    public static OutputProcessType GetOutput(ToolPreset preset) => preset.Kind switch
    {
        ToolKind.Brush or ToolKind.Pen or ToolKind.Eraser or ToolKind.Smudge => OutputProcessType.DirectDraw,
        ToolKind.Select => OutputProcessType.SelectionArea,
        ToolKind.MagicWand => OutputProcessType.MagicWand,
        ToolKind.Fill => OutputProcessType.FloodFill,
        ToolKind.LassoFill => OutputProcessType.ClosedAreaFill,
        ToolKind.Eyedropper => OutputProcessType.Eyedropper,
        ToolKind.MoveLayer => OutputProcessType.MoveLayer,
        ToolKind.SelectLayer => OutputProcessType.SelectLayer,
        ToolKind.Object => OutputProcessType.Object,
        ToolKind.Gradient => OutputProcessType.Gradient,
        ToolKind.Shape or ToolKind.Polyline => OutputProcessType.Stroke,
        ToolKind.Liquify => OutputProcessType.Liquify,
        ToolKind.Assistant => OutputProcessType.None,
        ToolKind.Hand => OutputProcessType.Hand,
        ToolKind.Zoom => OutputProcessType.Zoom,
        ToolKind.Rotate => OutputProcessType.Rotate,
        _ => OutputProcessType.DirectDraw
    };

    public static ToolKind FromLegacyEngine(ToolPresetEngine engine, ToolPreset preset) => engine switch
    {
        ToolPresetEngine.Brush => ToolKind.Brush,
        ToolPresetEngine.Eraser => ToolKind.Eraser,
        ToolPresetEngine.Smudge => ToolKind.Smudge,
        ToolPresetEngine.Select => ToolKind.Select,
        ToolPresetEngine.MagicWand => ToolKind.MagicWand,
        ToolPresetEngine.Fill => ToolKind.Fill,
        ToolPresetEngine.LassoFill => ToolKind.LassoFill,
        ToolPresetEngine.Eyedropper => ToolKind.Eyedropper,
        ToolPresetEngine.Move => ToolKind.Hand,
        ToolPresetEngine.MoveLayer => preset.Id == ToolGroupConfig.SelectLayerPresetId
            ? ToolKind.SelectLayer
            : ToolKind.MoveLayer,
        ToolPresetEngine.Gradient => ToolKind.Gradient,
        ToolPresetEngine.Shape => ToolKind.Shape,
        ToolPresetEngine.Polyline => ToolKind.Polyline,
        ToolPresetEngine.Liquify => ToolKind.Liquify,
        ToolPresetEngine.Assistant => ToolKind.Assistant,
        ToolPresetEngine.Object => ToolKind.Object,
        _ => ToolKind.Brush
    };

    public static ToolKind FromLegacyProcesses(InputProcessType input, OutputProcessType output) => (input, output) switch
    {
        (InputProcessType.Pen, OutputProcessType.DirectDraw) => ToolKind.Pen,
        (InputProcessType.Brush, OutputProcessType.DirectDraw) => ToolKind.Brush,
        (InputProcessType.Eraser, OutputProcessType.DirectDraw) => ToolKind.Eraser,
        (InputProcessType.Smudge, OutputProcessType.DirectDraw) => ToolKind.Smudge,
        (_, OutputProcessType.SelectionArea) => ToolKind.Select,
        (InputProcessType.Click, OutputProcessType.MagicWand) => ToolKind.MagicWand,
        (InputProcessType.Click, OutputProcessType.FloodFill) => ToolKind.Fill,
        (InputProcessType.Lasso, OutputProcessType.ClosedAreaFill) => ToolKind.LassoFill,
        (InputProcessType.Rect or InputProcessType.Polyline or InputProcessType.Brush or InputProcessType.Pen, OutputProcessType.ClosedAreaFill) => ToolKind.LassoFill,
        (InputProcessType.Click, OutputProcessType.Eyedropper) => ToolKind.Eyedropper,
        (InputProcessType.Drag, OutputProcessType.Gradient) => ToolKind.Gradient,
        (InputProcessType.Rect, OutputProcessType.Stroke) => ToolKind.Shape,
        (InputProcessType.Polyline, OutputProcessType.Stroke) => ToolKind.Polyline,
        (InputProcessType.Liquify, OutputProcessType.Liquify) => ToolKind.Liquify,
        (InputProcessType.Hand, OutputProcessType.Hand) => ToolKind.Hand,
        (InputProcessType.Zoom, OutputProcessType.Zoom) => ToolKind.Zoom,
        (InputProcessType.Rotate, OutputProcessType.Rotate) => ToolKind.Rotate,
        (InputProcessType.MoveLayer or InputProcessType.Drag, OutputProcessType.MoveLayer) => ToolKind.MoveLayer,
        (_, OutputProcessType.SelectLayer) => ToolKind.SelectLayer,
        (InputProcessType.Object, OutputProcessType.Object) => ToolKind.Object,
        (InputProcessType.Drag, OutputProcessType.None) => ToolKind.Assistant,
        _ => ToolKind.Brush
    };

    public static void SyncModesFromLegacyInput(ToolPreset preset, InputProcessType input)
    {
        if (preset.Kind == ToolKind.Select)
        {
            preset.SelectMode = input switch
            {
                InputProcessType.Lasso => SelectMode.Lasso,
                InputProcessType.Polyline => SelectMode.PolylineLasso,
                _ => SelectMode.Rect
            };
        }
    }

    public static void SyncSelectModeFromKind(ToolPreset preset)
    {
        if (preset.Kind != ToolKind.Select) return;
        // SelectMode is authoritative; no-op unless we need to validate.
    }
}

/// <summary>Obsolete engine enum retained for JSON migration only.</summary>
internal enum ToolPresetEngine
{
    Brush, Eraser, Smudge,
    Select, MagicWand,
    Fill, LassoFill,
    Eyedropper, Move, MoveLayer,
    Gradient, Shape, Polyline,
    Liquify,
    Assistant,
    Object,
}
