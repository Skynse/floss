using System;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;

namespace Floss.App.Processes;

/// <summary>
/// Keeps live CompositeTool instances aligned with their ToolPreset.
/// Tools are cached by preset id; scalar preset edits must be pushed here.
/// </summary>
internal static class ToolPresetSync
{
    public static void Apply(CompositeTool tool, ToolPreset preset)
    {
        ApplyInput(tool.Input, preset);
        ApplyOutput(tool.Output, preset);
    }

    private static double EffectiveStabilization(ToolPreset preset)
    {
        if (preset.BrushOverride?.Smoothing is { } brushSmoothing)
            return Math.Clamp(brushSmoothing, 0, 1);
        return Math.Clamp(preset.Stabilization, 0, 1);
    }

    private static void ApplyInput(IInputProcess input, ToolPreset preset)
    {
        switch (input)
        {
            case SmartShapeBrushInputProcess smartBrush:
                smartBrush.Stabilization = EffectiveStabilization(preset);
                smartBrush.SpeedAdaptiveStabilizer = preset.BrushOverride?.SpeedAdaptiveStabilizer ?? true;
                break;
            case BrushStrokeInputProcess brushStroke:
                brushStroke.Stabilization = EffectiveStabilization(preset);
                brushStroke.SpeedAdaptiveStabilizer = preset.BrushOverride?.SpeedAdaptiveStabilizer ?? true;
                break;
            case LassoInputProcess lasso:
                lasso.Stabilization = Math.Clamp(preset.Stabilization, 0, 1);
                break;
            case PolylineInputProcess polyline:
                polyline.ClosePath = preset.PolylineClosePath;
                break;
            case RectInputProcess rect:
                rect.ShapeKind = preset.ShapeKind;
                break;
            case LiquifyInputProcess liquify:
                liquify.BrushSize = preset.LiquifySize;
                break;
        }
    }

    private static void ApplyOutput(IOutputProcess output, ToolPreset preset)
    {
        var opacity = preset.BrushOverride?.Opacity ?? 1.0;
        var blendMode = preset.BrushOverride?.BlendMode ?? SkiaSharp.SKBlendMode.SrcOver;

        switch (output)
        {
            case SmartShapeBrushOutput smartDraw:
                smartDraw.Antialiasing = preset.Antialiasing;
                break;
            case DirectDrawOutput directDraw:
                directDraw.Antialiasing = preset.Antialiasing;
                break;
            case SelectionAreaOutput selection:
                selection.Operation = preset.SelectOp;
                selection.Antialiasing = preset.AntialiasingQuality != AntialiasingQuality.None;
                break;
            case MagicWandOutput wand:
                wand.Operation = preset.SelectOp;
                wand.Tolerance = preset.Tolerance;
                wand.FillReference = preset.FillReference;
                wand.ContiguousFill = preset.ContiguousFill;
                wand.AreaScaling = preset.AreaScaling;
                break;
            case FloodFillOutput fill:
                fill.Tolerance = preset.Tolerance;
                fill.FillReference = preset.FillReference;
                fill.ContiguousFill = preset.ContiguousFill;
                fill.AreaScaling = preset.AreaScaling;
                fill.Opacity = opacity;
                fill.BlendMode = blendMode;
                break;
            case ClosedAreaFillOutput closedFill:
                closedFill.Antialiasing = preset.AntialiasingQuality != AntialiasingQuality.None;
                closedFill.Opacity = opacity;
                closedFill.BlendMode = blendMode;
                break;
            case GradientOutput gradient:
                gradient.Antialiasing = preset.Antialiasing;
                gradient.GradientType = preset.GradientType;
                gradient.Opacity = opacity;
                gradient.BlendMode = blendMode;
                break;
            case StrokeOutput stroke:
                stroke.Antialiasing = preset.Antialiasing;
                stroke.StrokeWidth = preset.PolylineStrokeWidth;
                stroke.ClosePath = preset.PolylineClosePath;
                stroke.ShapeKind = preset.ShapeKind;
                stroke.ShapeDrawMode = preset.ShapeDrawMode;
                stroke.Opacity = opacity;
                stroke.BlendMode = blendMode;
                break;
            case EyedropperOutput eyedropper:
                eyedropper.SampleMode = preset.EyedropperSampleMode;
                eyedropper.ExcludeLockedLayers = preset.EyedropperExcludeLockedLayers;
                eyedropper.ExcludeReferenceLayers = preset.EyedropperExcludeReferenceLayers;
                break;
            case ZoomOutput zoom:
                zoom.Direction = preset.ZoomDirection;
                break;
        }
    }
}
