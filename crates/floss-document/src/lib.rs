//! Document model for Floss — the core data layer.
//!
//! Provides:
//! - `TiledPixelBuffer` — sparse, infinite-canvas pixel storage
//! - `DrawingLayer` — named layer with visibility, lock, blend mode
//! - `DrawingDocument` — layer stack, selection, undo/redo
//! - `SelectionMask` — tiled bitmap mask for selection
//! - `History` — undo/redo command stack

pub mod tile;
pub mod layer;
pub mod document;
pub mod selection;
pub mod history;

pub use document::DrawingDocument;
pub use layer::{DrawingLayer, ExpressionColorMode};
pub use selection::SelectionMask;
pub use tile::TiledPixelBuffer;
