using System;
using Floss.App.Config;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Document.Assistants;
using Floss.App.Tools;
using Floss.App.Tools.Assistants;
using SkiaSharp;

namespace Floss.App.Processes;

// Creates ITool instances from ToolPreset configurations.
public sealed class ToolFactory
{
    private readonly DrawingDocument _document;
    private readonly BrushEngine _brushEngine;

    public ToolFactory(DrawingDocument document, BrushEngine brushEngine)
    {
        _document = document;
        _brushEngine = brushEngine;
    }

    public ITool CreateTool(ToolPreset preset)
    {
        if (preset.Kind == ToolKind.Assistant)
            return new AssistantTool(_document, AssistantCreateSettings.FromPreset(preset));

        var input = CreateInput(preset);
        var output = CreateOutput(preset);
        if (input is SmartShapeBrushInputProcess smartInput && output is SmartShapeBrushOutput smartOutput)
        {
            smartInput.BindOutput(smartOutput);
            smartOutput.BindInput(smartInput);
        }
        if (input is ObjectInputProcess objectInput && output is ObjectOutput objectOutput)
            objectOutput.BindInput(objectInput);
        var alternate = CreateAlternate(preset);
        return new CompositeTool(input, output, alternate);
    }

    private ITool? CreateAlternate(ToolPreset preset)
    {
        if (preset.Kind.IsBrushFamily())
        {
            return new CompositeTool(
                new BrushStrokeInputProcess(),
                new EyedropperOutput
                {
                    SampleMode = preset.EyedropperSampleMode,
                    ExcludeLockedLayers = preset.EyedropperExcludeLockedLayers,
                    ExcludeReferenceLayers = preset.EyedropperExcludeReferenceLayers
                });
        }
        return null;
    }

    private static double EffectiveStabilization(ToolPreset preset)
    {
        if (preset.BrushOverride?.Smoothing is { } brushSmoothing)
            return Math.Clamp(brushSmoothing, 0, 1);
        return Math.Clamp(preset.Stabilization, 0, 1);
    }

    private SmartShapeBrushOutput CreateSmartShapeBrushOutput(ToolPreset preset)
    {
        var output = new SmartShapeBrushOutput(_brushEngine, _document)
        {
            Antialiasing = preset.Antialiasing
        };
        return output;
    }

    private IInputProcess CreateInput(ToolPreset preset) => preset.Kind switch
    {
        ToolKind.Brush or ToolKind.Pen or ToolKind.Eraser or ToolKind.Smudge
            => new SmartShapeBrushInputProcess
            {
                Stabilization = EffectiveStabilization(preset),
                SpeedAdaptiveStabilizer = preset.BrushOverride?.SpeedAdaptiveStabilizer ?? true
            },
        ToolKind.Select when preset.SelectMode == SelectMode.Lasso
            => new LassoInputProcess { Stabilization = Math.Clamp(preset.Stabilization, 0, 1) },
        ToolKind.Select when preset.SelectMode == SelectMode.PolylineLasso
            => new PolylineInputProcess { ClosePath = false },
        ToolKind.Select
            => new RectInputProcess { ShapeKind = ShapeKind.Rectangle },
        ToolKind.LassoFill
            => new LassoInputProcess { Stabilization = Math.Clamp(preset.Stabilization, 0, 1) },
        ToolKind.Polyline
            => new PolylineInputProcess { ClosePath = preset.PolylineClosePath },
        ToolKind.Shape
            => new RectInputProcess { ShapeKind = preset.ShapeKind },
        ToolKind.MagicWand or ToolKind.Fill or ToolKind.Eyedropper
            => new ClickInputProcess(),
        ToolKind.Gradient or ToolKind.MoveLayer or ToolKind.Hand or ToolKind.Rotate or ToolKind.Zoom
            => new DragInputProcess(),
        ToolKind.Liquify => new LiquifyInputProcess(),
        ToolKind.Object => new ObjectInputProcess(_document)
        {
            SelectableFlags = preset.SelectableObjectFlags,
        },
        ToolKind.SelectLayer => new RectInputProcess { ShapeKind = ShapeKind.Rectangle },
        _ => new BrushStrokeInputProcess()
    };

    private IOutputProcess CreateOutput(ToolPreset preset)
    {
        var opacity = preset.BrushOverride?.Opacity ?? 1.0;
        var blendMode = preset.BrushOverride?.BlendMode ?? SKBlendMode.SrcOver;

        return preset.Kind switch
        {
            ToolKind.Brush or ToolKind.Pen or ToolKind.Eraser or ToolKind.Smudge
                => CreateSmartShapeBrushOutput(preset),
            ToolKind.LassoFill => new ClosedAreaFillOutput
            {
                Antialiasing = preset.AntialiasingQuality != AntialiasingQuality.None,
                Opacity = opacity,
                BlendMode = blendMode
            },
            ToolKind.Select => new SelectionAreaOutput
            {
                Operation = preset.SelectOp,
                Antialiasing = preset.AntialiasingQuality != AntialiasingQuality.None
            },
            ToolKind.Fill => new FloodFillOutput
            {
                Tolerance = preset.Tolerance,
                FillReference = preset.FillReference,
                ContiguousFill = preset.ContiguousFill,
                AreaScaling = preset.AreaScaling,
                Opacity = opacity,
                BlendMode = blendMode
            },
            ToolKind.Gradient => new GradientOutput
            {
                Antialiasing = preset.Antialiasing,
                GradientType = preset.GradientType,
                Opacity = opacity,
                BlendMode = blendMode
            },
            ToolKind.Eyedropper => new EyedropperOutput
            {
                SampleMode = preset.EyedropperSampleMode,
                ExcludeLockedLayers = preset.EyedropperExcludeLockedLayers,
                ExcludeReferenceLayers = preset.EyedropperExcludeReferenceLayers
            },
            ToolKind.MoveLayer => new MoveLayerOutput(),
            ToolKind.MagicWand => new MagicWandOutput
            {
                Tolerance = preset.Tolerance,
                Operation = preset.SelectOp,
                FillReference = preset.FillReference,
                ContiguousFill = preset.ContiguousFill,
                AreaScaling = preset.AreaScaling
            },
            ToolKind.Shape or ToolKind.Polyline => new StrokeOutput
            {
                Antialiasing = preset.Antialiasing,
                StrokeWidth = preset.PolylineStrokeWidth,
                ClosePath = preset.PolylineClosePath,
                ShapeKind = preset.ShapeKind,
                ShapeDrawMode = preset.ShapeDrawMode,
                Opacity = opacity,
                BlendMode = blendMode
            },
            ToolKind.Liquify => new LiquifyOutput(),
            ToolKind.Hand => new HandOutput(),
            ToolKind.Zoom => new ZoomOutput { Direction = preset.ZoomDirection },
            ToolKind.Rotate => new RotateOutput(),
            ToolKind.SelectLayer => new SelectLayerOutput(),
            ToolKind.Object => new ObjectOutput(),
            _ => new DirectDrawOutput(_brushEngine, _document)
        };
    }
}
