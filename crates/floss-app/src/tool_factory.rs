use floss_brush::BrushPreset;
use floss_core::{InputProcessType, OutputProcessType};
use floss_tool::{
    BrushStrokeInputProcess, ClickInputProcess, ClosedAreaFillOutput, CompositeTool,
    DirectDrawOutput, DragInputProcess, EyedropperOutput, FloodFillOutput, HandOutput,
    IInputProcess, IOutputProcess, ITool, LassoInputProcess, MagicWandOutput, MoveLayerOutput,
    PolylineInputProcess, RectInputProcess, RotateOutput, SelectLayerOutput, SelectionAreaOutput,
    StrokeOutput, ZoomOutput,
};

use crate::tool_config::ToolPreset;

pub struct ToolFactory;

impl ToolFactory {
    pub fn create_tool(preset: &ToolPreset) -> Box<dyn ITool> {
        let input = Self::create_input(preset);
        let output = Self::create_output(preset);
        let alternate = Self::create_alternate(preset);
        Box::new(CompositeTool::new(input, output, alternate))
    }

    pub fn apply_brush_overrides(preset: &ToolPreset, brush: &BrushPreset) -> BrushPreset {
        preset.apply_to_brush_preset(brush)
    }

    fn create_alternate(preset: &ToolPreset) -> Option<Box<dyn ITool>> {
        if matches!(
            preset.input_process,
            InputProcessType::Pen | InputProcessType::Brush | InputProcessType::Eraser | InputProcessType::Smudge
        ) {
            Some(Box::new(CompositeTool::new(
                Box::new(ClickInputProcess::new()),
                Box::new(EyedropperOutput),
                None,
            )))
        } else {
            None
        }
    }

    fn create_input(preset: &ToolPreset) -> Box<dyn IInputProcess> {
        match preset.input_process {
            InputProcessType::Pen
            | InputProcessType::Brush
            | InputProcessType::Eraser
            | InputProcessType::Smudge => Box::new(BrushStrokeInputProcess::new(
                if preset.stabilization > 0.001 { preset.stabilization } else { 0.3 },
            )),
            InputProcessType::Lasso => Box::new(LassoInputProcess::new(
                if preset.stabilization > 0.001 { preset.stabilization } else { 0.3 },
            )),
            InputProcessType::Click => Box::new(ClickInputProcess::new()),
            InputProcessType::Polyline => Box::new(PolylineInputProcess::new()),
            InputProcessType::Drag
            | InputProcessType::MoveLayer
            | InputProcessType::Hand
            | InputProcessType::Rotate
            | InputProcessType::Zoom
            | InputProcessType::Liquify => Box::new(DragInputProcess::new()),
            InputProcessType::Rect => Box::new(RectInputProcess::new()),
            InputProcessType::None => Box::new(ClickInputProcess::new()),
        }
    }

    fn create_output(preset: &ToolPreset) -> Box<dyn IOutputProcess> {
        match preset.output_process {
            OutputProcessType::DirectDraw => Box::new(DirectDrawOutput::new()),
            OutputProcessType::ClosedAreaFill => Box::new(ClosedAreaFillOutput {
                antialiasing: preset.antialiasing_quality != crate::tool_config::AntialiasingQuality::None,
            }),
            OutputProcessType::Eyedropper => Box::new(EyedropperOutput),
            OutputProcessType::SelectionArea => Box::new(SelectionAreaOutput {
                operation: preset.select_op as i32,
            }),
            OutputProcessType::FloodFill => Box::new(FloodFillOutput {
                tolerance: preset.tolerance,
                fill_reference: preset.fill_reference as i32,
            }),
            OutputProcessType::Hand => Box::new(HandOutput { delta_x: 0.0, delta_y: 0.0 }),
            OutputProcessType::MagicWand => Box::new(MagicWandOutput {
                tolerance: preset.tolerance,
                operation: preset.select_op as i32,
                fill_reference: preset.fill_reference as i32,
            }),
            OutputProcessType::MoveLayer => Box::new(MoveLayerOutput { delta_x: 0.0, delta_y: 0.0 }),
            OutputProcessType::Rotate => Box::new(RotateOutput { angle_delta: 0.0 }),
            OutputProcessType::Stroke => Box::new(StrokeOutput {
                stroke_width: preset.polyline_stroke_width,
                close_path: preset.polyline_close_path,
                shape_kind: preset.shape_kind as i32,
                shape_draw_mode: preset.shape_draw_mode as i32,
            }),
            OutputProcessType::Zoom => Box::new(ZoomOutput { zoom_delta: preset.zoom_direction }),
            OutputProcessType::SelectLayer => Box::new(SelectLayerOutput),
            _ => Box::new(NoopOutput),
        }
    }
}

struct NoopOutput;

impl IOutputProcess for NoopOutput {
    fn execute(&mut self, _ctx: &mut floss_tool::ToolContext, _input: &floss_tool::process::ProcessedInput) {}
    fn preview(&mut self, _ctx: &mut floss_tool::ToolContext, _input: &floss_tool::process::ProcessedInput) {}
}
