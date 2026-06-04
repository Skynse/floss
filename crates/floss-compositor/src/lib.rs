//! CPU layer compositor for the Floss canvas.
//!
//! Composites all visible layers into a flat BGRA buffer.

pub mod compositor;

pub use compositor::LayerCompositor;
