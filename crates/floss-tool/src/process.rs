//! Input and output process traits plus key implementations.
//!
//! An **input process** captures a shape from pointer events (stroke, click,
//! lasso, rect, etc.) and produces `ProcessedInput` results.
//!
//! An **output process** takes a `ProcessedInput` and applies it to the
//! document (direct draw, eyedropper, fill, etc.).

use std::collections::VecDeque;

use floss_brush::BrushEngine;
use floss_core::Rect;
use floss_input::ToolAuxOperationType;

use crate::tool::{InputSample, ToolContext};

// ── Processed input ──────────────────────────────────────────────────────

/// The result of an input process: a set of stroke samples ready for rendering.
#[derive(Debug, Clone)]
pub struct StrokeInput {
    pub raw_samples: Vec<InputSample>,
    pub smoothed_samples: Vec<InputSample>,
}

/// An immediate result (e.g., straight-line anchor→click stroke).
#[derive(Debug, Clone)]
pub struct ImmediateInput {
    pub raw_samples: Vec<InputSample>,
}

/// Unified result type from input processes.
#[derive(Debug, Clone)]
pub enum ProcessedInput {
    Stroke(StrokeInput),
    Immediate(ImmediateInput),
    Lasso(StrokeInput),
    Rect { rect: Rect, from_center: bool },
    Click { x: f64, y: f64 },
}

// ── Input process trait ──────────────────────────────────────────────────

pub trait IInputProcess: Send + Sync {
    fn pointer_down(&mut self, sample: &InputSample);
    fn pointer_move(&mut self, sample: &InputSample);
    fn pointer_up(&mut self, sample: &InputSample);
    fn cancel(&mut self);
    fn commit(&mut self);

    fn is_active(&self) -> bool;
    fn get_immediate_result(&mut self) -> Option<ProcessedInput>;
    fn get_result(&mut self) -> Option<ProcessedInput>;
    fn get_preview(&self) -> Option<ProcessedInput>;
    fn set_tool_aux_mode(&mut self, _mode: ToolAuxOperationType) {}

    fn has_brush_cursor(&self) -> bool {
        false
    }
}

// ── Output process trait ─────────────────────────────────────────────────

pub trait IOutputProcess: Send + Sync {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput);
    fn preview(&mut self, ctx: &mut ToolContext, input: &ProcessedInput);
    fn cancel(&mut self, _ctx: &mut ToolContext) {}
}

// ── BrushStroke input process ────────────────────────────────────────────

/// Captures freehand stroke samples. Simplified port from C# BrushStrokeInputProcess.
pub struct BrushStrokeInputProcess {
    raw: Vec<InputSample>,
    smoothed: Vec<InputSample>,
    history: Vec<InputSample>,
    active: bool,
    stabilization: f64,
    immediate_result: Option<ProcessedInput>,
    phantom_arc_length: f64,
    tool_aux_mode: ToolAuxOperationType,
    straight_line_anchor: Option<InputSample>,
    last_known_pos: Option<InputSample>,
}

impl BrushStrokeInputProcess {
    pub fn new(stabilization: f64) -> Self {
        Self {
            raw: Vec::new(),
            smoothed: Vec::new(),
            history: Vec::new(),
            active: false,
            stabilization,
            immediate_result: None,
            phantom_arc_length: 0.0,
            tool_aux_mode: ToolAuxOperationType::None,
            straight_line_anchor: None,
            last_known_pos: None,
        }
    }

    fn apply_stabilization(&mut self, raw: InputSample) -> InputSample {
        if self.stabilization <= 0.0 {
            return raw;
        }

        let max_window = 20usize;
        let window_size = ((self.stabilization * max_window as f64).round() as usize)
            .clamp(1, max_window);

        self.history.push(raw);
        if self.history.len() > window_size {
            self.history.remove(0);
        }
        if self.history.len() == 1 {
            return raw;
        }

        let center = self.history.len() - 1;
        let sigma = (self.history.len() as f64 / 3.0).max(1.0);
        let mut total_weight = 0.0f64;
        let mut sum_x = 0.0f64;
        let mut sum_y = 0.0f64;
        let mut sum_pressure = 0.0f64;

        for (i, sample) in self.history.iter().enumerate() {
            let w = (-0.5 * (((i as f64 - center as f64) / sigma).powi(2))).exp();
            total_weight += w;
            sum_x += sample.x * w;
            sum_y += sample.y * w;
            sum_pressure += sample.pressure * w;
        }

        InputSample {
            x: sum_x / total_weight,
            y: sum_y / total_weight,
            pressure: sum_pressure / total_weight,
            ..raw
        }
    }
}

impl IInputProcess for BrushStrokeInputProcess {
    fn pointer_down(&mut self, sample: &InputSample) {
        self.raw.clear();
        self.smoothed.clear();
        self.history.clear();
        self.phantom_arc_length = 0.0;

        if self.tool_aux_mode == ToolAuxOperationType::StraightLine {
            if let Some(anchor) = self.straight_line_anchor {
                let mut forced_anchor = anchor;
                forced_anchor.pressure = 1.0;
                let mut target = *sample;
                target.pressure = 1.0;
                self.immediate_result = Some(ProcessedInput::Stroke(StrokeInput {
                    raw_samples: vec![forced_anchor, target],
                    smoothed_samples: vec![forced_anchor, target],
                }));
            }
        }

        self.straight_line_anchor = None;
        self.raw.push(*sample);
        let smoothed = self.apply_stabilization(*sample);
        self.smoothed.push(smoothed);
        self.active = true;
        self.last_known_pos = Some(*sample);
    }

    fn pointer_move(&mut self, sample: &InputSample) {
        self.last_known_pos = Some(*sample);
        if !self.active {
            return;
        }
        self.raw.push(*sample);
        let smoothed = self.apply_stabilization(*sample);
        self.smoothed.push(smoothed);

        // Track phantom arc length
        if self.smoothed.len() >= 2 {
            let last = &self.smoothed[self.smoothed.len() - 2];
            let dx = smoothed.x - last.x;
            let dy = smoothed.y - last.y;
            self.phantom_arc_length += (dx * dx + dy * dy).sqrt();
        }
    }

    fn pointer_up(&mut self, sample: &InputSample) {
        if !self.active {
            return;
        }
        self.raw.push(*sample);
        let smoothed = self.apply_stabilization(*sample);
        self.smoothed.push(smoothed);
        self.active = false;
        if self.phantom_arc_length > 2.0 {
            self.straight_line_anchor = self.smoothed.last().copied();
        }
    }

    fn cancel(&mut self) {
        self.active = false;
        self.raw.clear();
        self.smoothed.clear();
        self.history.clear();
        self.immediate_result = None;
    }

    fn commit(&mut self) {
        self.active = false;
    }

    fn is_active(&self) -> bool {
        self.active
    }

    fn get_immediate_result(&mut self) -> Option<ProcessedInput> {
        self.immediate_result.take()
    }

    fn get_result(&mut self) -> Option<ProcessedInput> {
        if !self.active && !self.raw.is_empty() && self.phantom_arc_length > 2.0 {
            Some(ProcessedInput::Stroke(StrokeInput {
                raw_samples: std::mem::take(&mut self.raw),
                smoothed_samples: std::mem::take(&mut self.smoothed),
            }))
        } else {
            None
        }
    }

    fn get_preview(&self) -> Option<ProcessedInput> {
        if self.active && !self.smoothed.is_empty() {
            Some(ProcessedInput::Stroke(StrokeInput {
                raw_samples: self.raw.clone(),
                smoothed_samples: self.smoothed.clone(),
            }))
        } else {
            None
        }
    }

    fn has_brush_cursor(&self) -> bool {
        true
    }

    fn set_tool_aux_mode(&mut self, mode: ToolAuxOperationType) {
        self.tool_aux_mode = mode;
    }
}

// ── Click input process ──────────────────────────────────────────────────

pub struct ClickInputProcess {
    point: Option<(f64, f64)>,
}

impl ClickInputProcess {
    pub fn new() -> Self {
        Self { point: None }
    }
}

impl IInputProcess for ClickInputProcess {
    fn pointer_down(&mut self, sample: &InputSample) {
        self.point = Some((sample.x, sample.y));
    }
    fn pointer_move(&mut self, _sample: &InputSample) {}
    fn pointer_up(&mut self, _sample: &InputSample) {}
    fn cancel(&mut self) {
        self.point = None;
    }
    fn commit(&mut self) {}

    fn is_active(&self) -> bool {
        false
    }

    fn get_immediate_result(&mut self) -> Option<ProcessedInput> {
        self.point.take().map(|(x, y)| ProcessedInput::Click { x, y })
    }

    fn get_result(&mut self) -> Option<ProcessedInput> {
        self.point.take().map(|(x, y)| ProcessedInput::Click { x, y })
    }

    fn get_preview(&self) -> Option<ProcessedInput> {
        None
    }
}

// ── Lasso input process ──────────────────────────────────────────────────

pub struct LassoInputProcess {
    raw: Vec<InputSample>,
    smoothed: Vec<InputSample>,
    active: bool,
    stabilization: f64,
    last_smoothed: Option<InputSample>,
}

impl LassoInputProcess {
    pub fn new(stabilization: f64) -> Self {
        Self {
            raw: Vec::new(),
            smoothed: Vec::new(),
            active: false,
            stabilization,
            last_smoothed: None,
        }
    }

    fn apply_stabilization(&self, raw: InputSample, last_smoothed: Option<InputSample>) -> InputSample {
        let Some(last) = last_smoothed else {
            return raw;
        };
        if self.stabilization <= 0.0 {
            return raw;
        }
        let s = self.stabilization.clamp(0.0, 0.99);
        let alpha = 1.0 - s;
        InputSample {
            x: last.x + (raw.x - last.x) * alpha,
            y: last.y + (raw.y - last.y) * alpha,
            pressure: raw.pressure,
            ..raw
        }
    }
}

impl IInputProcess for LassoInputProcess {
    fn pointer_down(&mut self, sample: &InputSample) {
        self.raw.clear();
        self.smoothed.clear();
        self.raw.push(*sample);
        self.smoothed.push(*sample);
        self.last_smoothed = Some(*sample);
        self.active = true;
    }

    fn pointer_move(&mut self, sample: &InputSample) {
        if !self.active {
            return;
        }
        self.raw.push(*sample);
        let smoothed = self.apply_stabilization(*sample, self.last_smoothed);
        self.smoothed.push(smoothed);
        self.last_smoothed = Some(smoothed);
    }

    fn pointer_up(&mut self, sample: &InputSample) {
        if !self.active {
            return;
        }
        self.raw.push(*sample);
        let smoothed = self.apply_stabilization(*sample, self.last_smoothed);
        self.smoothed.push(smoothed);
        self.last_smoothed = Some(smoothed);
        self.active = false;
    }

    fn cancel(&mut self) {
        self.active = false;
        self.raw.clear();
        self.smoothed.clear();
        self.last_smoothed = None;
    }

    fn commit(&mut self) {}
    fn is_active(&self) -> bool { self.active }
    fn get_immediate_result(&mut self) -> Option<ProcessedInput> { None }

    fn get_result(&mut self) -> Option<ProcessedInput> {
        if !self.active && self.smoothed.len() >= 3 {
            Some(ProcessedInput::Lasso(StrokeInput {
                raw_samples: std::mem::take(&mut self.raw),
                smoothed_samples: std::mem::take(&mut self.smoothed),
            }))
        } else {
            None
        }
    }

    fn get_preview(&self) -> Option<ProcessedInput> {
        if self.active && self.smoothed.len() >= 2 {
            Some(ProcessedInput::Lasso(StrokeInput {
                raw_samples: self.raw.clone(),
                smoothed_samples: self.smoothed.clone(),
            }))
        } else {
            None
        }
    }
}

// ── Polyline input process ───────────────────────────────────────────────

pub struct PolylineInputProcess {
    points: Vec<InputSample>,
    cursor: Option<InputSample>,
    active: bool,
    committed: bool,
}

impl PolylineInputProcess {
    pub fn new() -> Self {
        Self {
            points: Vec::new(),
            cursor: None,
            active: false,
            committed: false,
        }
    }
}

impl IInputProcess for PolylineInputProcess {
    fn pointer_down(&mut self, sample: &InputSample) {
        if !self.active {
            self.active = true;
            self.committed = false;
            self.points.clear();
        }
        self.points.push(*sample);
        self.cursor = Some(*sample);
    }

    fn pointer_move(&mut self, sample: &InputSample) {
        if self.active {
            self.cursor = Some(*sample);
        }
    }

    fn pointer_up(&mut self, sample: &InputSample) {
        if self.active {
            self.cursor = Some(*sample);
        }
    }

    fn cancel(&mut self) {
        self.active = false;
        self.committed = false;
        self.points.clear();
        self.cursor = None;
    }

    fn commit(&mut self) {
        self.committed = true;
    }

    fn is_active(&self) -> bool { self.active }
    fn get_immediate_result(&mut self) -> Option<ProcessedInput> { None }

    fn get_result(&mut self) -> Option<ProcessedInput> {
        if self.committed && self.active && self.points.len() >= 2 {
            self.active = false;
            self.committed = false;
            Some(ProcessedInput::Lasso(StrokeInput {
                raw_samples: self.points.clone(),
                smoothed_samples: std::mem::take(&mut self.points),
            }))
        } else {
            None
        }
    }

    fn get_preview(&self) -> Option<ProcessedInput> {
        if self.active && !self.points.is_empty() {
            let mut smoothed = self.points.clone();
            if let Some(cursor) = self.cursor
                && self.points.len() >= 1
            {
                smoothed.push(cursor);
            }
            Some(ProcessedInput::Lasso(StrokeInput {
                raw_samples: self.points.clone(),
                smoothed_samples: smoothed,
            }))
        } else {
            None
        }
    }
}

// ── Rect input process ───────────────────────────────────────────────────

pub struct RectInputProcess {
    start: Option<InputSample>,
    current: Option<InputSample>,
    active: bool,
}

impl RectInputProcess {
    pub fn new() -> Self {
        Self {
            start: None,
            current: None,
            active: false,
        }
    }
}

impl IInputProcess for RectInputProcess {
    fn pointer_down(&mut self, sample: &InputSample) {
        self.start = Some(*sample);
        self.current = Some(*sample);
        self.active = true;
    }

    fn pointer_move(&mut self, sample: &InputSample) {
        if self.active {
            self.current = Some(*sample);
        }
    }

    fn pointer_up(&mut self, sample: &InputSample) {
        if self.active {
            self.current = Some(*sample);
            self.active = false;
        }
    }

    fn cancel(&mut self) {
        self.active = false;
        self.start = None;
        self.current = None;
    }

    fn commit(&mut self) {}
    fn is_active(&self) -> bool { self.active }
    fn get_immediate_result(&mut self) -> Option<ProcessedInput> { None }

    fn get_result(&mut self) -> Option<ProcessedInput> {
        match (self.start, self.current, self.active) {
            (Some(start), Some(end), false) => {
                let x = start.x.min(end.x).floor() as i32;
                let y = start.y.min(end.y).floor() as i32;
                let w = (start.x.max(end.x) - start.x.min(end.x)).ceil() as i32;
                let h = (start.y.max(end.y) - start.y.min(end.y)).ceil() as i32;
                Some(ProcessedInput::Rect {
                    rect: Rect::new(x, y, w.max(1), h.max(1)),
                    from_center: false,
                })
            }
            _ => None,
        }
    }

    fn get_preview(&self) -> Option<ProcessedInput> {
        match (self.start, self.current) {
            (Some(start), Some(end)) => {
                let x = start.x.min(end.x).floor() as i32;
                let y = start.y.min(end.y).floor() as i32;
                let w = (start.x.max(end.x) - start.x.min(end.x)).ceil() as i32;
                let h = (start.y.max(end.y) - start.y.min(end.y)).ceil() as i32;
                Some(ProcessedInput::Rect {
                    rect: Rect::new(x, y, w.max(1), h.max(1)),
                    from_center: false,
                })
            }
            _ => None,
        }
    }
}

// ── Drag input process (hand, move layer, zoom, rotate) ──────────────────

pub struct DragInputProcess {
    active: bool,
    start: Option<(f64, f64)>,
    current: Option<(f64, f64)>,
}

impl DragInputProcess {
    pub fn new() -> Self {
        Self {
            active: false,
            start: None,
            current: None,
        }
    }

    pub fn delta(&self) -> (f64, f64) {
        match (self.start, self.current) {
            (Some((sx, sy)), Some((cx, cy))) => (cx - sx, cy - sy),
            _ => (0.0, 0.0),
        }
    }
}

impl IInputProcess for DragInputProcess {
    fn pointer_down(&mut self, sample: &InputSample) {
        self.active = true;
        self.start = Some((sample.x, sample.y));
        self.current = Some((sample.x, sample.y));
    }
    fn pointer_move(&mut self, sample: &InputSample) {
        self.current = Some((sample.x, sample.y));
    }
    fn pointer_up(&mut self, _sample: &InputSample) {
        self.active = false;
    }
    fn cancel(&mut self) {
        self.active = false;
        self.start = None;
        self.current = None;
    }
    fn commit(&mut self) {
        self.active = false;
    }

    fn is_active(&self) -> bool {
        self.active
    }
    fn get_immediate_result(&mut self) -> Option<ProcessedInput> {
        None
    }
    fn get_result(&mut self) -> Option<ProcessedInput> {
        None
    }
    fn get_preview(&self) -> Option<ProcessedInput> {
        None
    }
}

// ── DirectDraw output process ────────────────────────────────────────────

/// Renders brush strokes onto the active layer using the BrushEngine.
pub struct DirectDrawOutput {
    engine: BrushEngine,
}

impl DirectDrawOutput {
    pub fn new() -> Self {
        Self {
            engine: BrushEngine::new(),
        }
    }
}

impl IOutputProcess for DirectDrawOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        match input {
            ProcessedInput::Stroke(stroke) => {
                if let Some(brush) = ctx.active_preset {
                    let mut dirty = Rect::ZERO;
                    for pair in stroke.smoothed_samples.windows(2) {
                        let from = &pair[0];
                        let to = &pair[1];
                        dirty = dirty.union(self.engine.estimate_segment_region(
                            brush,
                            from.x,
                            from.y,
                            to.x,
                            to.y,
                        ));
                    }
                    let before_tiles = ctx.document.record_paint_region(dirty);
                    for pair in stroke.smoothed_samples.windows(2) {
                        let from = &pair[0];
                        let to = &pair[1];
                        let _ = self.engine.rasterize_segment(
                            ctx.document.active_layer_mut(),
                            brush,
                            from.x, from.y, from.pressure,
                            to.x, to.y, to.pressure,
                            from.tilt_x, from.tilt_y,
                            to.tilt_x, to.tilt_y,
                            0.0,
                            None,
                        );
                    }
                    ctx.document.commit_paint_region(dirty, before_tiles);
                }
            }
            ProcessedInput::Immediate(..) => {
                // Handled via get_immediate_result in CompositeTool
            }
            _ => {}
        }
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {
        // Preview is handled by the GPU compositor's stamp preview pass
    }
}

// ── Eyedropper output process ───────────────────────────────────────────

/// Picks a color from the canvas at the click point.
pub struct EyedropperOutput;

impl IOutputProcess for EyedropperOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        if let ProcessedInput::Click { x, y } = input {
            let pixel = ctx.document.active_layer_mut().pixels.get_pixel(
                x.round() as i32,
                y.round() as i32,
            );
            if pixel[3] > 0 {
                // Set the brush color to the picked color
                let color = floss_core::Color::from_bytes(pixel[2], pixel[1], pixel[0], 255);
                ctx.sampled_color = Some(color);
            }
        }
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Hand output process ──────────────────────────────────────────────────

/// Pans the viewport.
pub struct HandOutput {
    pub delta_x: f64,
    pub delta_y: f64,
}

impl IOutputProcess for HandOutput {
    fn execute(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {
        // The app layer reads delta_x/delta_y to adjust the viewport transform
    }
    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Zoom output process ──────────────────────────────────────────────────

/// Zooms the viewport.
pub struct ZoomOutput {
    pub zoom_delta: f64,
}

impl IOutputProcess for ZoomOutput {
    fn execute(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Rotate output process ────────────────────────────────────────────────

/// Rotates the viewport.
pub struct RotateOutput {
    pub angle_delta: f64,
}

impl IOutputProcess for RotateOutput {
    fn execute(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── MoveLayer output process ─────────────────────────────────────────────

pub struct MoveLayerOutput {
    pub delta_x: f64,
    pub delta_y: f64,
}

impl IOutputProcess for MoveLayerOutput {
    fn execute(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Selection area output ────────────────────────────────────────────────

pub struct SelectionAreaOutput {
    pub operation: i32,
}

impl IOutputProcess for SelectionAreaOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        let before = ctx.document.selection().capture_snapshot();
        match input {
            ProcessedInput::Rect { rect, .. } => apply_selection_rect(ctx, &before, *rect, self.operation),
            ProcessedInput::Lasso(stroke) => {
                if let Some(bounds) = stroke_bounds(&stroke.smoothed_samples) {
                    apply_selection_rect(ctx, &before, bounds, self.operation);
                }
            }
            _ => {}
        }
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Stroke output ────────────────────────────────────────────────────────

pub struct StrokeOutput {
    pub stroke_width: f32,
    pub close_path: bool,
    pub shape_kind: i32,
    pub shape_draw_mode: i32,
}

impl IOutputProcess for StrokeOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        let Some(brush) = ctx.active_preset else {
            return;
        };

        let points = match input {
            ProcessedInput::Lasso(stroke) => stroke.smoothed_samples.clone(),
            ProcessedInput::Rect { rect, .. } => rect_shape_points(*rect, self.shape_kind),
            _ => return,
        };
        if points.len() < 2 {
            return;
        }

        let is_line = self.shape_kind == 2;
        let should_fill = !is_line && matches!(self.shape_draw_mode, 0 | 2);
        let should_stroke = is_line || matches!(self.shape_draw_mode, 1 | 2);

        let mut dirty_region = Rect::ZERO;
        if should_fill {
            dirty_region = dirty_region.union(stroke_bounds(&points).unwrap_or(Rect::ZERO));
        }
        if should_stroke {
            for pair in points.windows(2) {
                dirty_region = dirty_region.union(segment_region(pair[0], pair[1], self.stroke_width.max(1.0) as f64));
            }
            if self.close_path && points.len() >= 3 {
                dirty_region = dirty_region.union(segment_region(*points.last().unwrap(), points[0], self.stroke_width.max(1.0) as f64));
            }
        }
        let before_tiles = ctx.document.record_paint_region(dirty_region);
        if should_fill {
            fill_polygon_on_active_layer(ctx, &points, brush.color, self.close_path || !is_line);
        }
        if should_stroke {
            stroke_segments_on_active_layer(ctx, brush.color, &points, self.stroke_width.max(1.0) as i32, self.close_path && !is_line);
        }
        ctx.document.commit_paint_region(dirty_region, before_tiles);
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Select layer output ──────────────────────────────────────────────────

pub struct SelectLayerOutput;

impl IOutputProcess for SelectLayerOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        match input {
            ProcessedInput::Click { x, y } => {
                for index in (0..ctx.document.layer_count()).rev() {
                    let layer = ctx.document.layer_mut(index);
                    if !layer.visible || layer.is_group {
                        continue;
                    }
                    let pixel = layer.pixels.get_pixel(x.round() as i32 - layer.offset_x, y.round() as i32 - layer.offset_y);
                    if pixel[3] > 0 {
                        ctx.document.set_active_layer(index);
                        break;
                    }
                }
            }
            ProcessedInput::Rect { rect, .. } => {
                if let Some(index) = find_topmost_layer_in_rect(ctx, *rect) {
                    ctx.document.set_active_layer(index);
                }
            }
            ProcessedInput::Lasso(stroke) => {
                if let Some(bounds) = stroke_bounds(&stroke.smoothed_samples)
                    && let Some(index) = find_topmost_layer_in_rect(ctx, bounds)
                {
                    ctx.document.set_active_layer(index);
                }
            }
            _ => {}
        }
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Flood fill output ────────────────────────────────────────────────────

pub struct FloodFillOutput {
    pub tolerance: f64,
    pub fill_reference: i32,
}

impl IOutputProcess for FloodFillOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        let ProcessedInput::Click { x, y } = input else {
            return;
        };
        if ctx.document.layer_count() == 0 {
            return;
        }
        let active_index = ctx.document.active_layer_index();
        if ctx.document.layer(active_index).is_group || ctx.document.layer(active_index).locked {
            return;
        }
        let [r, g, b, a] = ctx.active_preset.map(|brush| brush.color.as_bytes()).unwrap_or([0, 0, 0, 255]);
        let width = ctx.document.width();
        let height = ctx.document.height();
        let seed_x = x.round() as i32;
        let seed_y = y.round() as i32;
        if seed_x < 0 || seed_y < 0 || seed_x >= width || seed_y >= height {
            return;
        }

        let reference = build_reference_buffer(ctx, self.fill_reference);
        let region = flood_fill_region(ctx, &reference, active_index, seed_x, seed_y, self.tolerance);
        if region.points.is_empty() {
            return;
        }

        let dirty = region.bounds;
        let before_tiles = ctx.document.record_paint_region(dirty);
        {
            let layer = ctx.document.active_layer_mut();
            for &(doc_x, doc_y) in &region.points {
                if layer.is_alpha_locked {
                    let existing = layer.pixels.get_pixel(doc_x - layer.offset_x, doc_y - layer.offset_y);
                    if existing[3] == 0 {
                        continue;
                    }
                }
                layer.pixels.set_pixel(doc_x - layer.offset_x, doc_y - layer.offset_y, b, g, r, a);
            }
        }
        ctx.document.commit_paint_region(dirty, before_tiles);
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

// ── Closed area fill output ──────────────────────────────────────────────

pub struct ClosedAreaFillOutput {
    pub antialiasing: bool,
}

impl IOutputProcess for ClosedAreaFillOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        let ProcessedInput::Lasso(stroke) = input else {
            return;
        };
        if stroke.smoothed_samples.len() < 3 {
            return;
        }
        let active_index = ctx.document.active_layer_index();
        if ctx.document.layer(active_index).is_group || ctx.document.layer(active_index).locked {
            return;
        }
        let Some(bounds) = stroke_bounds(&stroke.smoothed_samples) else {
            return;
        };
        let [r, g, b, a] = ctx.active_preset.map(|brush| brush.color.as_bytes()).unwrap_or([0, 0, 0, 255]);
        let dirty = bounds;
        let before_tiles = ctx.document.record_paint_region(dirty);
        let has_selection = ctx.document.has_selection();
        let selection_mask = if has_selection {
            Some(ctx.document.selection().capture_snapshot())
        } else {
            None
        };
        let layer = ctx.document.active_layer_mut();
        for y in bounds.y..bounds.bottom() {
            for x in bounds.x..bounds.right() {
                if has_selection && !selection_snapshot_contains(selection_mask.as_ref().unwrap(), x, y) {
                    continue;
                }
                if point_in_polygon(x as f64 + 0.5, y as f64 + 0.5, &stroke.smoothed_samples) {
                    if layer.is_alpha_locked {
                        let existing = layer.pixels.get_pixel(x - layer.offset_x, y - layer.offset_y);
                        if existing[3] == 0 {
                            continue;
                        }
                    }
                    layer.pixels.set_pixel(x - layer.offset_x, y - layer.offset_y, b, g, r, a);
                }
            }
        }
        ctx.document.commit_paint_region(dirty, before_tiles);
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

fn selection_snapshot_contains(snapshot: &floss_document::history::SelectionSnapshot, x: i32, y: i32) -> bool {
    let tile_size = 64i32;
    let tx = div_floor(x, tile_size);
    let ty = div_floor(y, tile_size);
    let Some(tile) = snapshot.mask_tiles.get(&(tx, ty)) else {
        return false;
    };
    let local_x = x.rem_euclid(tile_size) as usize;
    let local_y = y.rem_euclid(tile_size) as usize;
    let off = (local_y * tile_size as usize + local_x) * 4 + 3;
    tile[off] != 0
}

fn div_floor(value: i32, divisor: i32) -> i32 {
    let tile = value / divisor;
    if value < 0 && value % divisor != 0 {
        tile - 1
    } else {
        tile
    }
}

// ── Magic wand output ────────────────────────────────────────────────────

pub struct MagicWandOutput {
    pub tolerance: f64,
    pub operation: i32,
    pub fill_reference: i32,
}

impl IOutputProcess for MagicWandOutput {
    fn execute(&mut self, ctx: &mut ToolContext, input: &ProcessedInput) {
        let ProcessedInput::Click { x, y } = input else {
            return;
        };
        let active_index = ctx.document.active_layer_index();
        if ctx.document.layer(active_index).is_group {
            return;
        }
        let width = ctx.document.width();
        let height = ctx.document.height();
        let seed_x = x.round() as i32;
        let seed_y = y.round() as i32;
        if seed_x < 0 || seed_y < 0 || seed_x >= width || seed_y >= height {
            return;
        }
        let reference = build_reference_buffer(ctx, self.fill_reference);
        let region = flood_fill_region(ctx, &reference, active_index, seed_x, seed_y, self.tolerance);
        if region.points.is_empty() {
            return;
        }

        let mut mask = vec![false; (region.bounds.w * region.bounds.h) as usize];
        for &(px, py) in &region.points {
            let local_x = px - region.bounds.x;
            let local_y = py - region.bounds.y;
            let idx = local_y as usize * region.bounds.w as usize + local_x as usize;
            mask[idx] = true;
        }

        let before = ctx.document.selection().capture_snapshot();
        match self.operation {
            1 => ctx.document.selection_mut().add_from_mask(region.bounds, &mask),
            2 => ctx.document.selection_mut().subtract_from_mask(region.bounds, &mask),
            _ => ctx.document.selection_mut().replace_from_mask(region.bounds, &mask),
        }
        ctx.document.commit_selection_mutation(before);
    }

    fn preview(&mut self, _ctx: &mut ToolContext, _input: &ProcessedInput) {}
}

fn apply_selection_rect(ctx: &mut ToolContext, before: &floss_document::history::SelectionSnapshot, rect: Rect, operation: i32) {
    match operation {
        1 => ctx.document.selection_mut().add_rect(rect),
        2 => ctx.document.selection_mut().subtract_rect(rect),
        _ => {
            ctx.document.selection_mut().clear();
            ctx.document.selection_mut().add_rect(rect);
        }
    }
    ctx.document.commit_selection_mutation(before.clone());
}

fn stroke_bounds(samples: &[InputSample]) -> Option<Rect> {
    let first = samples.first()?;
    let mut min_x = first.x;
    let mut min_y = first.y;
    let mut max_x = first.x;
    let mut max_y = first.y;
    for sample in samples.iter().skip(1) {
        min_x = min_x.min(sample.x);
        min_y = min_y.min(sample.y);
        max_x = max_x.max(sample.x);
        max_y = max_y.max(sample.y);
    }
    Some(Rect::new(
        min_x.floor() as i32,
        min_y.floor() as i32,
        (max_x - min_x).ceil() as i32 + 1,
        (max_y - min_y).ceil() as i32 + 1,
    ))
}

struct FillRegion {
    bounds: Rect,
    points: Vec<(i32, i32)>,
}

fn build_reference_buffer(ctx: &mut ToolContext, fill_reference: i32) -> Vec<[u8; 4]> {
    let width = ctx.document.width();
    let height = ctx.document.height();
    let mut buffer = vec![[0u8; 4]; (width * height) as usize];
    for y in 0..height {
        for x in 0..width {
            buffer[(y * width + x) as usize] = sample_reference_pixel(ctx, fill_reference, x, y);
        }
    }
    buffer
}

fn sample_reference_pixel(ctx: &mut ToolContext, fill_reference: i32, x: i32, y: i32) -> [u8; 4] {
    let mut out = [0u8; 4];
    for index in 0..ctx.document.layer_count() {
        let layer = ctx.document.layer_mut(index);
        if !layer.visible || layer.is_group {
            continue;
        }
        if fill_reference == 1 && !layer.is_reference {
            continue;
        }
        let pixel = layer.pixels.get_pixel(x - layer.offset_x, y - layer.offset_y);
        if pixel[3] > 0 {
            out = pixel;
        }
    }
    out
}

fn flood_fill_region(
    ctx: &mut ToolContext,
    reference: &[[u8; 4]],
    active_index: usize,
    seed_x: i32,
    seed_y: i32,
    tolerance: f64,
) -> FillRegion {
    let width = ctx.document.width();
    let height = ctx.document.height();
    let seed_idx = (seed_y * width + seed_x) as usize;
    let target = reference[seed_idx];
    let tolerance_sq = ((tolerance * 255.0).powi(2) * 4.0) as i32;
    let mut visited = vec![false; (width * height) as usize];
    let mut queue = VecDeque::new();
    let mut points = Vec::new();
    let mut min_x = i32::MAX;
    let mut min_y = i32::MAX;
    let mut max_x = i32::MIN;
    let mut max_y = i32::MIN;
    visited[seed_idx] = true;
    queue.push_back((seed_x, seed_y));

    while let Some((x, y)) = queue.pop_front() {
        let idx = (y * width + x) as usize;
        if !color_match(reference[idx], target, tolerance_sq) {
            continue;
        }
        if ctx.document.has_selection() && !ctx.document.selection().contains_point(x, y) {
            continue;
        }

        points.push((x, y));
        min_x = min_x.min(x);
        min_y = min_y.min(y);
        max_x = max_x.max(x);
        max_y = max_y.max(y);

        for (nx, ny) in neighbors4(x, y, width, height) {
            let nidx = (ny * width + nx) as usize;
            if !visited[nidx] {
                visited[nidx] = true;
                queue.push_back((nx, ny));
            }
        }
    }

    if points.is_empty() {
        return FillRegion { bounds: Rect::ZERO, points };
    }

    if ctx.document.layer(active_index).is_alpha_locked {
        let layer = ctx.document.layer_mut(active_index);
        points.retain(|&(x, y)| {
            let pixel = layer.pixels.get_pixel(x - layer.offset_x, y - layer.offset_y);
            pixel[3] > 0
        });
    }

    if points.is_empty() {
        return FillRegion { bounds: Rect::ZERO, points };
    }

    FillRegion {
        bounds: Rect::new(min_x, min_y, max_x - min_x + 1, max_y - min_y + 1),
        points,
    }
}

fn neighbors4(x: i32, y: i32, width: i32, height: i32) -> [(i32, i32); 4] {
    [
        (if x + 1 < width { x + 1 } else { x }, y),
        (if x > 0 { x - 1 } else { x }, y),
        (x, if y + 1 < height { y + 1 } else { y }),
        (x, if y > 0 { y - 1 } else { y }),
    ]
}

fn color_match(a: [u8; 4], b: [u8; 4], tolerance_sq: i32) -> bool {
    let dr = a[2] as i32 - b[2] as i32;
    let dg = a[1] as i32 - b[1] as i32;
    let db = a[0] as i32 - b[0] as i32;
    let da = a[3] as i32 - b[3] as i32;
    dr * dr + dg * dg + db * db + da * da <= tolerance_sq
}

fn segment_region(from: InputSample, to: InputSample, width: f64) -> Rect {
    let min_x = from.x.min(to.x).floor() as i32;
    let min_y = from.y.min(to.y).floor() as i32;
    let max_x = from.x.max(to.x).ceil() as i32;
    let max_y = from.y.max(to.y).ceil() as i32;
    Rect::new(min_x, min_y, (max_x - min_x).max(1), (max_y - min_y).max(1)).inflate(width.ceil() as i32)
}

fn stroke_segments_on_active_layer(
    ctx: &mut ToolContext,
    color: floss_core::Color,
    points: &[InputSample],
    stroke_width: i32,
    close_path: bool,
) {
    let layer = ctx.document.active_layer_mut();
    if !layer.can_paint() {
        return;
    }
    for pair in points.windows(2) {
        raster_line(layer, pair[0], pair[1], color, stroke_width);
    }
    if close_path && points.len() >= 3 {
        raster_line(layer, *points.last().unwrap(), points[0], color, stroke_width);
    }
}

fn fill_polygon_on_active_layer(
    ctx: &mut ToolContext,
    points: &[InputSample],
    color: floss_core::Color,
    close_path: bool,
) {
    let [r, g, b, a] = color.as_bytes();
    if points.len() < 3 || !close_path {
        return;
    }
    let layer = ctx.document.active_layer_mut();
    if !layer.can_paint() {
        return;
    }
    let Some(bounds) = stroke_bounds(points) else {
        return;
    };
    for y in bounds.y..bounds.bottom() {
        for x in bounds.x..bounds.right() {
            if point_in_polygon(x as f64 + 0.5, y as f64 + 0.5, points) {
                layer.pixels.set_pixel(x - layer.offset_x, y - layer.offset_y, b, g, r, a);
            }
        }
    }
}

fn point_in_polygon(x: f64, y: f64, points: &[InputSample]) -> bool {
    let mut inside = false;
    let mut j = points.len() - 1;
    for i in 0..points.len() {
        let xi = points[i].x;
        let yi = points[i].y;
        let xj = points[j].x;
        let yj = points[j].y;
        let intersects = ((yi > y) != (yj > y))
            && (x < (xj - xi) * (y - yi) / ((yj - yi).abs().max(f64::EPSILON)) + xi);
        if intersects {
            inside = !inside;
        }
        j = i;
    }
    inside
}

fn raster_line(
    layer: &mut floss_document::DrawingLayer,
    from: InputSample,
    to: InputSample,
    color: floss_core::Color,
    stroke_width: i32,
) {
    let [r, g, b, a] = color.as_bytes();
    let dx = to.x - from.x;
    let dy = to.y - from.y;
    let steps = dx.abs().max(dy.abs()).ceil().max(1.0) as i32;
    for step in 0..=steps {
        let t = step as f64 / steps as f64;
        let x = from.x + dx * t;
        let y = from.y + dy * t;
        stamp_disc(layer, x.round() as i32 - layer.offset_x, y.round() as i32 - layer.offset_y, stroke_width.max(1), [b, g, r, a]);
    }
}

fn stamp_disc(layer: &mut floss_document::DrawingLayer, cx: i32, cy: i32, radius: i32, color: [u8; 4]) {
    let r = radius.max(1);
    let rr = r * r;
    for y in (cy - r)..=(cy + r) {
        for x in (cx - r)..=(cx + r) {
            let dx = x - cx;
            let dy = y - cy;
            if dx * dx + dy * dy <= rr {
                layer.pixels.set_pixel(x, y, color[0], color[1], color[2], color[3]);
            }
        }
    }
}

fn rect_shape_points(rect: Rect, shape_kind: i32) -> Vec<InputSample> {
    let x1 = rect.x as f64;
    let y1 = rect.y as f64;
    let x2 = rect.right() as f64;
    let y2 = rect.bottom() as f64;
    match shape_kind {
        1 => {
            let cx = (x1 + x2) * 0.5;
            let cy = (y1 + y2) * 0.5;
            let rx = (x2 - x1).abs() * 0.5;
            let ry = (y2 - y1).abs() * 0.5;
            (0..64)
                .map(|i| {
                    let a = i as f64 * std::f64::consts::TAU / 64.0;
                    InputSample {
                        x: cx + a.cos() * rx,
                        y: cy + a.sin() * ry,
                        pressure: 1.0,
                        tilt_x: 0.0,
                        tilt_y: 0.0,
                        twist: 0.0,
                        time_micros: 0,
                        source: crate::tool::InputSource::Mouse,
                        phase: crate::tool::InputPhase::Move,
                    }
                })
                .collect()
        }
        2 => vec![
            sample_at(x1, y1),
            sample_at(x2, y2),
        ],
        _ => vec![
            sample_at(x1, y1),
            sample_at(x2, y1),
            sample_at(x2, y2),
            sample_at(x1, y2),
        ],
    }
}

fn sample_at(x: f64, y: f64) -> InputSample {
    InputSample {
        x,
        y,
        pressure: 1.0,
        tilt_x: 0.0,
        tilt_y: 0.0,
        twist: 0.0,
        time_micros: 0,
        source: crate::tool::InputSource::Mouse,
        phase: crate::tool::InputPhase::Move,
    }
}

fn find_topmost_layer_in_rect(ctx: &mut ToolContext, rect: Rect) -> Option<usize> {
    for index in (0..ctx.document.layer_count()).rev() {
        let layer = ctx.document.layer(index);
        if !layer.visible || layer.is_group {
            continue;
        }
        let local = rect.translate(-layer.offset_x, -layer.offset_y);
        if layer.has_content_in(local) {
            return Some(index);
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use floss_brush::BrushPreset;
    use floss_core::Color;
    use floss_document::DrawingDocument;
    use floss_input::KeyModifiers;

    fn make_context<'a>(document: &'a mut DrawingDocument, brush: &'a BrushPreset) -> ToolContext<'a> {
        ToolContext {
            document,
            active_preset: Some(brush),
            brush: Some(crate::tool::BrushSnapshot {
                color: brush.color,
                size: brush.size,
            }),
            tool_aux_mode: ToolAuxOperationType::None,
            current_modifiers: KeyModifiers::NONE,
            sampled_color: None,
        }
    }

    fn sample(x: f64, y: f64) -> InputSample {
        InputSample {
            x,
            y,
            pressure: 1.0,
            tilt_x: 0.0,
            tilt_y: 0.0,
            twist: 0.0,
            time_micros: 0,
            source: crate::tool::InputSource::Mouse,
            phase: crate::tool::InputPhase::Move,
        }
    }

    #[test]
    fn rect_input_produces_rect_result() {
        let mut input = RectInputProcess::new();
        input.pointer_down(&sample(10.0, 20.0));
        input.pointer_move(&sample(30.0, 45.0));
        input.pointer_up(&sample(30.0, 45.0));
        match input.get_result() {
            Some(ProcessedInput::Rect { rect, .. }) => {
                assert_eq!(rect, Rect::new(10, 20, 20, 25));
            }
            other => panic!("unexpected result: {:?}", other),
        }
    }

    #[test]
    fn selection_area_output_replaces_selection_from_rect() {
        let mut document = DrawingDocument::new(128, 128);
        let brush = BrushPreset::simple("Brush", 8.0, Color::BLACK);
        let mut ctx = make_context(&mut document, &brush);
        let mut output = SelectionAreaOutput { operation: 0 };

        output.execute(&mut ctx, &ProcessedInput::Rect {
            rect: Rect::new(8, 12, 20, 16),
            from_center: false,
        });

        assert!(ctx.document.selection().contains_point(10, 14));
        assert!(!ctx.document.selection().contains_point(2, 2));
    }

    #[test]
    fn select_layer_output_picks_topmost_non_group_layer() {
        let mut document = DrawingDocument::new(64, 64);
        document.active_layer_mut().pixels.set_pixel(4, 4, 0, 0, 255, 255);
        document.add_layer();
        document.active_layer_mut().pixels.set_pixel(4, 4, 0, 255, 0, 255);

        let brush = BrushPreset::simple("Brush", 8.0, Color::BLACK);
        let mut ctx = make_context(&mut document, &brush);
        let mut output = SelectLayerOutput;
        output.execute(&mut ctx, &ProcessedInput::Click { x: 4.0, y: 4.0 });

        assert_eq!(ctx.document.active_layer_index(), 1);
    }

    #[test]
    fn stroke_output_line_marks_pixels() {
        let mut document = DrawingDocument::new(64, 64);
        let brush = BrushPreset::simple("Brush", 8.0, Color::from_bytes(255, 0, 0, 255));
        let mut ctx = make_context(&mut document, &brush);
        let mut output = StrokeOutput {
            stroke_width: 1.0,
            close_path: false,
            shape_kind: 2,
            shape_draw_mode: 1,
        };
        output.execute(&mut ctx, &ProcessedInput::Rect {
            rect: Rect::new(4, 4, 20, 20),
            from_center: false,
        });

        let pixel = ctx.document.active_layer_mut().pixels.get_pixel(4, 4);
        assert!(pixel[3] > 0);
    }

    #[test]
    fn flood_fill_output_fills_connected_region() {
        let mut document = DrawingDocument::new(32, 32);
        for x in 0..10 {
            document.active_layer_mut().pixels.set_pixel(x, 0, 0, 0, 0, 255);
            document.active_layer_mut().pixels.set_pixel(x, 9, 0, 0, 0, 255);
        }
        for y in 0..10 {
            document.active_layer_mut().pixels.set_pixel(0, y, 0, 0, 0, 255);
            document.active_layer_mut().pixels.set_pixel(9, y, 0, 0, 0, 255);
        }
        let brush = BrushPreset::simple("Brush", 8.0, Color::from_bytes(255, 0, 0, 255));
        let mut ctx = make_context(&mut document, &brush);
        let mut output = FloodFillOutput {
            tolerance: 0.0,
            fill_reference: 0,
        };

        output.execute(&mut ctx, &ProcessedInput::Click { x: 4.0, y: 4.0 });

        let pixel = ctx.document.active_layer_mut().pixels.get_pixel(4, 4);
        assert_eq!(pixel[2], 255);
        assert_eq!(pixel[3], 255);
    }

    #[test]
    fn magic_wand_output_creates_selection_region() {
        let mut document = DrawingDocument::new(32, 32);
        for x in 0..8 {
            for y in 0..8 {
                document.active_layer_mut().pixels.set_pixel(x, y, 0, 0, 0, 255);
            }
        }
        let brush = BrushPreset::simple("Brush", 8.0, Color::BLACK);
        let mut ctx = make_context(&mut document, &brush);
        let mut output = MagicWandOutput {
            tolerance: 0.0,
            operation: 0,
            fill_reference: 0,
        };

        output.execute(&mut ctx, &ProcessedInput::Click { x: 2.0, y: 2.0 });

        assert!(ctx.document.selection().contains_point(2, 2));
        assert!(!ctx.document.selection().contains_point(12, 12));
    }
}
