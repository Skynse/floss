//! Tool system — input processes, output processes, and tool dispatch.
//!
//! Ported from `Floss.App.Tools.*` and `Floss.App.Processes.*`.
//!
//! ## Key feature: modifier key queuing
//!
//! The `ToolController` supports queuing tool-switch requests from modifier
//! keys during active strokes. When Alt is pressed mid-stroke, the eyedropper
//! switch is queued and applied on pointer-up — **the stroke is never cancelled**.

pub mod tool;
pub mod controller;
pub mod composite;
pub mod process;

pub use controller::ToolController;
pub use composite::CompositeTool;
pub use process::{
    BrushStrokeInputProcess, ClickInputProcess, DirectDrawOutput, DragInputProcess,
    ClosedAreaFillOutput, EyedropperOutput, FloodFillOutput, HandOutput, LassoInputProcess,
    MagicWandOutput, MoveLayerOutput, PolylineInputProcess, RectInputProcess, RotateOutput,
    SelectLayerOutput, SelectionAreaOutput, StrokeOutput, ZoomOutput,
    IInputProcess, IOutputProcess, ProcessedInput, StrokeInput,
};
pub use tool::{ITool, InputPhase, InputSample, InputSource, ToolContext};
