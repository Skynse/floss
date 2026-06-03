using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Processes;

// Creates ITool instances from ToolPreset configurations using the input/output process architecture.
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
        var input = CreateInput(preset);
        var output = CreateOutput(preset);
        var alternate = CreateAlternate(preset);
        return new CompositeTool(input, output, alternate);
    }

    private ITool? CreateAlternate(ToolPreset preset)
    {
        if (preset.InputProcess is InputProcessType.Pen or InputProcessType.Brush
            or InputProcessType.Eraser or InputProcessType.Smudge)
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
        => preset.BrushOverride?.Smoothing is { } s
            ? s
            : preset.Stabilization > 0.001 ? preset.Stabilization : 0.0;

    private IInputProcess CreateInput(ToolPreset preset)
    {
        return preset.InputProcess switch
        {
            InputProcessType.Pen or InputProcessType.Brush or InputProcessType.Eraser or InputProcessType.Smudge
                => new BrushStrokeInputProcess { Stabilization = EffectiveStabilization(preset) },
            InputProcessType.Liquify => new LiquifyInputProcess(),
            InputProcessType.Lasso => new LassoInputProcess { Stabilization = preset.Stabilization > 0.001 ? preset.Stabilization : 0.0 },
            InputProcessType.Polyline => new PolylineInputProcess { ClosePath = preset.PolylineClosePath },
            InputProcessType.Rect => new RectInputProcess { ShapeKind = preset.ShapeKind },
            InputProcessType.Click => new ClickInputProcess(),
            InputProcessType.Drag or InputProcessType.MoveLayer
                or InputProcessType.Hand or InputProcessType.Rotate
                or InputProcessType.Zoom
                => new DragInputProcess(),
            _ => new BrushStrokeInputProcess()
        };
    }

    private IOutputProcess CreateOutput(ToolPreset preset)
    {
        var opacity = preset.BrushOverride?.Opacity ?? 1.0;
        var blendMode = preset.BrushOverride?.BlendMode ?? SKBlendMode.SrcOver;

        return preset.OutputProcess switch
        {
            OutputProcessType.DirectDraw => new DirectDrawOutput(_brushEngine, _document)
            {
                Antialiasing = preset.Antialiasing
            },
            OutputProcessType.ClosedAreaFill => new ClosedAreaFillOutput
            {
                Antialiasing = preset.AntialiasingQuality != AntialiasingQuality.None,
                Opacity = opacity,
                BlendMode = blendMode
            },
            OutputProcessType.SelectionArea => new SelectionAreaOutput
            {
                Operation = preset.SelectOp,
                Antialiasing = preset.AntialiasingQuality != AntialiasingQuality.None
            },
            OutputProcessType.FloodFill => new FloodFillOutput
            {
                Tolerance = preset.Tolerance,
                FillReference = preset.FillReference,
                Opacity = opacity,
                BlendMode = blendMode
            },
            OutputProcessType.Gradient => new GradientOutput
            {
                Antialiasing = preset.Antialiasing,
                GradientType = preset.GradientType,
                Opacity = opacity,
                BlendMode = blendMode
            },
            OutputProcessType.Eyedropper => new EyedropperOutput
            {
                SampleMode = preset.EyedropperSampleMode,
                ExcludeLockedLayers = preset.EyedropperExcludeLockedLayers,
                ExcludeReferenceLayers = preset.EyedropperExcludeReferenceLayers
            },
            OutputProcessType.MoveLayer => new MoveLayerOutput(),
            OutputProcessType.MagicWand => new MagicWandOutput
            {
                Tolerance = preset.Tolerance,
                Operation = preset.SelectOp,
                FillReference = preset.FillReference
            },
            OutputProcessType.Stroke => new StrokeOutput
            {
                Antialiasing = preset.Antialiasing,
                StrokeWidth = preset.PolylineStrokeWidth,
                ClosePath = preset.PolylineClosePath,
                ShapeKind = preset.ShapeKind,
                ShapeDrawMode = preset.ShapeDrawMode,
                Opacity = opacity,
                BlendMode = blendMode
            },
            OutputProcessType.Liquify => new LiquifyOutput(),
            OutputProcessType.Hand => new HandOutput(),
            OutputProcessType.Zoom => new ZoomOutput { Direction = preset.ZoomDirection },
            OutputProcessType.Rotate => new RotateOutput(),
            OutputProcessType.SelectLayer => new SelectLayerOutput(),
            _ => new DirectDrawOutput(_brushEngine, _document)
        };
    }
}

