use serde::{Deserialize, Serialize};

/// Porter-Duff blend mode for brush compositing.
///
/// Maps to the standard blend modes used in digital painting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum BlendMode {
    /// Source over destination (standard painting).
    Normal,
    /// Multiply blend: darkens.
    Multiply,
    /// Screen blend: lightens.
    Screen,
    /// Overlay blend: combines multiply and screen.
    Overlay,
    /// Color dodge: brightens with reduced contrast.
    ColorDodge,
    /// Color burn: darkens with increased contrast.
    ColorBurn,
    /// Darken: keeps the darker of source/destination per channel.
    Darken,
    /// Lighten: keeps the lighter of source/destination per channel.
    Lighten,
    /// Hard light: like overlay but with source/dest swapped.
    HardLight,
    /// Soft light: softer version of hard light.
    SoftLight,
    /// Difference: absolute difference between source and destination.
    Difference,
    /// Eraser: subtracts source alpha from destination.
    Erase,
}

impl Default for BlendMode {
    fn default() -> Self {
        Self::Normal
    }
}

impl BlendMode {
    /// Returns true if this mode samples pixels below the stroke
    /// (needed for blend-mode brushes that read the canvas).
    pub fn reads_destination(&self) -> bool {
        matches!(
            self,
            BlendMode::Multiply
                | BlendMode::Screen
                | BlendMode::Overlay
                | BlendMode::ColorDodge
                | BlendMode::ColorBurn
                | BlendMode::Darken
                | BlendMode::Lighten
                | BlendMode::HardLight
                | BlendMode::SoftLight
                | BlendMode::Difference
        )
    }

    /// Returns true if this is the erase (dest-out) mode.
    pub fn is_erase(&self) -> bool {
        matches!(self, BlendMode::Erase)
    }
}
