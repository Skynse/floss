//! Undo/redo history for the document.
//!
//! History is a simple double-stack of commands. The `DrawingDocument`
//! owns the logic for applying/reversing commands — `History` is just
//! the storage layer.

use std::collections::HashMap;

use floss_core::{BlendMode, Rect};

use crate::layer::ExpressionColorMode;

// ── Commands ────────────────────────────────────────────────────────────

/// A reversible mutation to the document.
#[derive(Clone)]
pub enum HistoryCommand {
    /// Pixel region was painted on — stores the previous tile state.
    PaintTiles {
        layer_index: usize,
        before_tiles: HashMap<(i32, i32), Box<[u8; 64 * 64 * 4]>>,
        after_tiles: HashMap<(i32, i32), Box<[u8; 64 * 64 * 4]>>,
    },
    /// A layer was added at the given index.
    AddLayer {
        index: usize,
        layer_data: Vec<LayerSnapshot>,
    },
    /// A layer was removed — stores full layer data for restoration.
    RemoveLayer {
        index: usize,
        layer_data: Vec<LayerSnapshot>,
    },
    /// Layer properties changed.
    LayerPropertyChange {
        index: usize,
        before: LayerPropertySnapshot,
        after: LayerPropertySnapshot,
    },
    /// Layer moved from one index to another.
    MoveLayer {
        from_index: usize,
        to_index: usize,
        count: usize,
    },
    /// Selection changed.
    SelectionChange {
        before: Option<SelectionSnapshot>,
        after: Option<SelectionSnapshot>,
    },
    /// Active layer was cleared — stores the tile state before clear.
    ClearActiveLayer {
        index: usize,
        tiles: HashMap<(i32, i32), Box<[u8; 64 * 64 * 4]>>,
    },
}

// ── Snapshots ───────────────────────────────────────────────────────────

/// Full layer data for undo restoration (used by RemoveLayer).
#[derive(Clone)]
pub struct LayerSnapshot {
    pub name: String,
    pub visible: bool,
    pub locked: bool,
    pub blend_mode: BlendMode,
    pub opacity: f64,
    pub layer_color: Option<[u8; 4]>,
    pub expression_color: ExpressionColorMode,
    pub offset_x: i32,
    pub offset_y: i32,
    pub is_group: bool,
    pub is_open: bool,
    pub is_clipping: bool,
    pub is_alpha_locked: bool,
    pub is_reference: bool,
    pub is_paper: bool,
    pub indent_level: i32,
    pub parent_group: i32,
    pub pixel_snapshot: HashMap<(i32, i32), Box<[u8; 64 * 64 * 4]>>,
    pub width: i32,
    pub height: i32,
}

/// Layer property snapshot for undo.
#[derive(Clone, Debug)]
pub struct LayerPropertySnapshot {
    pub name: String,
    pub visible: bool,
    pub locked: bool,
    pub blend_mode: BlendMode,
    pub opacity: f64,
    pub layer_color: Option<[u8; 4]>,
    pub expression_color: ExpressionColorMode,
    pub offset_x: i32,
    pub offset_y: i32,
    pub is_open: bool,
    pub is_clipping: bool,
    pub is_alpha_locked: bool,
    pub is_reference: bool,
    pub is_paper: bool,
    pub indent_level: i32,
}

/// Selection state snapshot for undo.
#[derive(Clone, Debug)]
pub struct SelectionSnapshot {
    pub mask_tiles: HashMap<(i32, i32), Box<[u8; 64 * 64 * 4]>>,
    pub bounds: Rect,
}

// ── History stack ───────────────────────────────────────────────────────

/// The undo/redo stack.
#[derive(Default)]
pub struct History {
    undo_stack: Vec<HistoryCommand>,
    redo_stack: Vec<HistoryCommand>,
    max_steps: usize,
}

impl History {
    pub fn new(max_steps: usize) -> Self {
        Self {
            undo_stack: Vec::with_capacity(max_steps),
            redo_stack: Vec::new(),
            max_steps,
        }
    }

    /// Push a new command onto the undo stack, clearing the redo stack.
    pub fn push(&mut self, cmd: HistoryCommand) {
        self.redo_stack.clear();
        self.undo_stack.push(cmd);
        while self.undo_stack.len() > self.max_steps {
            self.undo_stack.remove(0);
        }
    }

    pub fn can_undo(&self) -> bool {
        !self.undo_stack.is_empty()
    }

    pub fn can_redo(&self) -> bool {
        !self.redo_stack.is_empty()
    }

    /// Pop the most recent undo command.
    pub fn pop_undo(&mut self) -> Option<HistoryCommand> {
        self.undo_stack.pop()
    }

    /// Pop the most recent redo command.
    pub fn pop_redo(&mut self) -> Option<HistoryCommand> {
        self.redo_stack.pop()
    }

    /// Push a command to the redo stack (after undoing).
    pub fn push_redo(&mut self, cmd: HistoryCommand) {
        self.redo_stack.push(cmd);
    }

    /// Move a previously-popped undo command back to undo (after redoing).
    pub fn push_undo(&mut self, cmd: HistoryCommand) {
        self.undo_stack.push(cmd);
    }

    pub fn clear(&mut self) {
        self.undo_stack.clear();
        self.redo_stack.clear();
    }
}
