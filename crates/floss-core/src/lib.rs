//! Foundation types shared across all floss crates.

mod blend;
mod color;
mod geometry;
mod tool_enums;

pub use blend::BlendMode;
pub use color::Color;
pub use geometry::{Rect, Transform};
pub use tool_enums::{InputProcessType, OutputProcessType, ToolAuxOperationType};
