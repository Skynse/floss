//! Input handling — keyboard, modifier keys, and tablet abstraction.
//!
//! Ported from `Floss.App.Input.*`.
//!
//! Provides:
//! - `KeyBinding` — keyboard shortcuts with display formatting
//! - `ModifierKeySettings` — modifier→action resolution (Alt=eyedropper, etc.)
//! - `KeyModifiers` — modifier flag tracking

pub mod keyboard;
pub mod modifiers;

pub use keyboard::{is_modifier_key, Key, KeyBinding, KeyModifiers};
pub use modifiers::{ModifierAction, ModifierKeyAssignment, ModifierKeySettings, ToolAuxOperationType};
