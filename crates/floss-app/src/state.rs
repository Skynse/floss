//! Application state — the global model shared across all views.

use floss_core::Color;
use floss_document::DrawingDocument;
use floss_input::{ModifierKeySettings, KeyModifiers};

/// Global application state, accessible via gpui's model system.
pub struct AppState {
    /// The document being edited.
    pub document: DrawingDocument,
    /// Modifier key configuration.
    pub modifier_keys: ModifierKeySettings,
    /// Current modifier flags.
    pub current_modifiers: KeyModifiers,
    /// Current brush color.
    pub brush_color: Color,
    /// Current brush size.
    pub brush_size: f64,
}

impl AppState {
    pub fn new() -> Self {
        Self {
            document: DrawingDocument::new(2048, 1536),
            modifier_keys: ModifierKeySettings::create_defaults(),
            current_modifiers: KeyModifiers::NONE,
            brush_color: Color::BLACK,
            brush_size: 12.0,
        }
    }
}
