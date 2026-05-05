using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Tools;

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
        return new CompositeTool(input, output);
    }

    private IInputProcess CreateInput(ToolPreset preset)
    {
        return preset.InputProcess switch
        {
            InputProcessType.BrushStroke => new BrushStrokeInputProcess { Stabilization = preset.Stabilization > 0.001 ? preset.Stabilization : 0.3 },
            InputProcessType.Lasso => new LassoInputProcess { Stabilization = preset.Stabilization > 0.001 ? preset.Stabilization : 0.3 },
            InputProcessType.Polyline => new PolylineInputProcess { ClosePath = preset.PolylineClosePath },
            InputProcessType.Rect => new RectInputProcess(),
            InputProcessType.Click => new ClickInputProcess(),
            InputProcessType.Drag => new DragInputProcess(),
            _ => new BrushStrokeInputProcess()
        };
    }

    private IOutputProcess CreateOutput(ToolPreset preset)
    {
        return preset.OutputProcess switch
        {
            OutputProcessType.DirectDraw => new DirectDrawOutput(_brushEngine, _document)
            {
                Antialiasing = preset.Antialiasing
            },
            OutputProcessType.ClosedAreaFill => new ClosedAreaFillOutput
            {
                Antialiasing = preset.Antialiasing
            },
            OutputProcessType.SelectionArea => new SelectionAreaOutput
            {
                Operation = preset.SelectMode switch
                {
                    SelectMode.Rect => SelectOp.Replace,
                    _ => SelectOp.Replace
                }
            },
            OutputProcessType.FloodFill => new FloodFillOutput
            {
                Tolerance = preset.Tolerance
            },
            OutputProcessType.Gradient => new GradientOutput
            {
                Antialiasing = preset.Antialiasing,
                GradientType = preset.GradientType
            },
            OutputProcessType.Eyedropper => new EyedropperOutput(),
            OutputProcessType.MoveLayer => new MoveLayerOutput(),
            OutputProcessType.MagicWand => new MagicWandOutput
            {
                Tolerance = preset.Tolerance,
                Operation = SelectOp.Replace
            },
            OutputProcessType.Stroke => new StrokeOutput
            {
                Antialiasing = preset.Antialiasing,
                StrokeWidth = preset.PolylineStrokeWidth,
                ClosePath = preset.PolylineClosePath,
                ShapeKind = preset.ShapeKind,
                ShapeDrawMode = preset.ShapeDrawMode
            },
            _ => new DirectDrawOutput(_brushEngine, _document)
        };
    }
}
