//! Geometry types for the brush engine.
//!
//! Enums for brush tip shape, angle source, and tip direction.

use serde::{Deserialize, Serialize};

/// The shape of a procedural brush tip.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum BrushTipShape {
    Circle,
    SoftRound,
    Flat,
    Ellipse,
    Rectangle,
    Chalk,
    Bristle,
    Scatter,
}

impl Default for BrushTipShape {
    fn default() -> Self {
        Self::Circle
    }
}

/// How the brush stamp's rotation angle is determined.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum AngleSource {
    /// Fixed angle only.
    None = 0,
    /// Angle follows the direction of the stroke movement.
    DirectionOfLine = 1,
    /// Angle follows pen tilt direction.
    PenTilt = 2,
    /// Angle follows pen barrel twist.
    PenTwist = 3,
}

impl Default for AngleSource {
    fn default() -> Self {
        Self::None
    }
}

/// Direction for asymmetric brush tips (e.g., flat brushes).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum BrushTipDirection {
    Horizontal,
    Vertical,
}

impl Default for BrushTipDirection {
    fn default() -> Self {
        Self::Horizontal
    }
}
