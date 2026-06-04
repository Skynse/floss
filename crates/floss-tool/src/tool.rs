//! Tool traits and common types.
//!
//! Defines the `ITool` trait that all tools implement, plus the
//! `ToolContext` that provides the tool with document access and state.

use floss_core::{Color, Rect, Transform};
use floss_document::{DrawingDocument, DrawingLayer};
use floss_input::{KeyModifiers, ToolAuxOperationType};

/// Context passed to tool methods — provides access to document and global state.
pub struct ToolContext<'a> {
    pub document: &'a mut DrawingDocument,
    pub active_preset: Option<&'a floss_brush::BrushPreset>,
    pub brush: Option<BrushSnapshot>,
    pub tool_aux_mode: ToolAuxOperationType,
    pub current_modifiers: KeyModifiers,
    pub sampled_color: Option<Color>,
}

/// Snapshot of current brush state for rendering.
#[derive(Debug, Clone)]
pub struct BrushSnapshot {
    pub color: Color,
    pub size: f64,
}

/// The trait all tools must implement.
pub trait ITool: Send + Sync {
    /// Called when the tool becomes active.
    fn activate(&mut self, ctx: &mut ToolContext);
    /// Called when the tool is deactivated (switched away).
    fn deactivate(&mut self, ctx: &mut ToolContext);
    /// Pointer pressed down.
    fn pointer_down(&mut self, ctx: &mut ToolContext, sample: &InputSample);
    /// Pointer moved.
    fn pointer_move(&mut self, ctx: &mut ToolContext, sample: &InputSample);
    /// Pointer released.
    fn pointer_up(&mut self, ctx: &mut ToolContext, sample: &InputSample);
    /// Cancel the current operation.
    fn cancel(&mut self, ctx: &mut ToolContext);

    /// Whether the tool has a pending operation that can be committed.
    fn has_pending_operation(&self) -> bool;
    /// Whether the tool can be committed from a simple click.
    fn can_commit_from_click(&self) -> bool;
    /// Commit the pending operation (for modal tools like polyline).
    fn commit(&mut self, ctx: &mut ToolContext);

    /// Optional alternate tool (used via modifier key — e.g., eyedropper on Alt).
    fn alternate(&self) -> Option<&dyn ITool> { None }
    fn alternate_mut(&mut self) -> Option<&mut (dyn ITool + '_)> { None }
}

/// A single input sample from a pointer/tablet event.
#[derive(Debug, Clone, Copy)]
pub struct InputSample {
    pub x: f64,
    pub y: f64,
    pub pressure: f64,
    pub tilt_x: f32,
    pub tilt_y: f32,
    pub twist: f32,
    pub time_micros: i64,
    pub source: InputSource,
    pub phase: InputPhase,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InputSource {
    Mouse,
    Pen,
    Touch,
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InputPhase {
    Down,
    Move,
    Up,
}
