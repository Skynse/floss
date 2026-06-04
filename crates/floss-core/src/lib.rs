//! Foundation types shared across all floss crates.

pub mod blend;
mod color;
mod geometry;
mod tool_enums;

pub use blend::{BlendMode, ExpressionColorMode};
pub use color::Color;
pub use geometry::{Rect, Transform};
pub use tool_enums::{InputProcessType, OutputProcessType, ToolAuxOperationType};
