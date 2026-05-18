use serde::{Deserialize, Serialize};

/// Identifies the input process (shape capture) for a tool preset.
///
/// Mirrors `Floss.App.InputProcessType` in the C# codebase.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum InputProcessType {
    None = 0,
    /// Freehand brush stroke with stabilization.
    Pen = 1,
    /// Same as pen but distinct for categorization.
    Brush = 2,
    /// Eraser stroke (inverted alpha).
    Eraser = 3,
    /// Smudge/blur stroke.
    Smudge = 4,
    /// Lasso selection.
    Lasso = 5,
    /// Polyline / straight-line tool.
    Polyline = 6,
    /// Rectangle / ellipse / line shape.
    Rect = 7,
    /// Single-click pick.
    Click = 8,
    /// Drag (hand, move layer).
    Drag = 9,
    /// Liquify push/twirl.
    Liquify = 10,
    /// Hand/pan viewport.
    Hand = 11,
    /// Rotate viewport.
    Rotate = 12,
    /// Zoom viewport.
    Zoom = 13,
    /// Move layer drag.
    MoveLayer = 14,
}

impl Default for InputProcessType {
    fn default() -> Self {
        Self::Brush
    }
}

/// Identifies the output process (what happens to the captured shape).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum OutputProcessType {
    None = 0,
    /// Paint directly onto the active layer.
    DirectDraw = 1,
    /// Fill a closed area or selection.
    ClosedAreaFill = 2,
    /// Selection area (lasso, rect → selection).
    SelectionArea = 3,
    /// Flood fill (magic wand style).
    FloodFill = 4,
    /// Gradient fill.
    Gradient = 5,
    /// Pick a color from the canvas.
    Eyedropper = 6,
    /// Move active layer.
    MoveLayer = 7,
    /// Stroke a path/shape with the current brush.
    Stroke = 8,
    /// Zoom the viewport.
    Zoom = 9,
    /// Pan the viewport.
    Hand = 10,
    /// Rotate the viewport.
    Rotate = 11,
    /// Magic wand selection.
    MagicWand = 12,
    /// Liquify warp.
    Liquify = 13,
    /// Select a layer by clicking on it.
    SelectLayer = 14,
}

impl Default for OutputProcessType {
    fn default() -> Self {
        Self::DirectDraw
    }
}

/// Auxiliary tool operation triggered by a modifier key (e.g. Shift → straight line).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ToolAuxOperationType {
    /// No aux operation active.
    None = 0,
    /// Shift key: lock to straight line from last anchor.
    StraightLine,
}
