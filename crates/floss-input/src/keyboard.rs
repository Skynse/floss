//! Keyboard input handling — key bindings, modifier keys, and shortcuts.
//!
//! Ported from `Floss.App.Input.KeyBinding.cs` and `Floss.App.Input.ModifierKeySettings.cs`.

use std::fmt;

use serde::{Deserialize, Serialize};

// ── Key identifiers ──────────────────────────────────────────────────────

/// Platform-independent key identifier.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum Key {
    None,
    Space,
    Escape,
    Back,
    Delete,
    Return,
    Enter,
    Tab,
    Left,
    Right,
    Up,
    Down,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    LeftShift, RightShift,
    LeftCtrl, RightCtrl,
    LeftAlt, RightAlt,
}

/// Modifier flags.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
pub struct KeyModifiers {
    pub ctrl: bool,
    pub shift: bool,
    pub alt: bool,
}

impl KeyModifiers {
    pub const NONE: Self = Self { ctrl: false, shift: false, alt: false };
    pub const CTRL: Self = Self { ctrl: true, shift: false, alt: false };
    pub const SHIFT: Self = Self { ctrl: false, shift: true, alt: false };
    pub const ALT: Self = Self { ctrl: false, shift: false, alt: true };

    pub fn has_any(&self) -> bool {
        self.ctrl || self.shift || self.alt
    }
}

// ── KeyBinding ───────────────────────────────────────────────────────────

/// A keyboard shortcut: a key plus required modifiers.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct KeyBinding {
    pub key: Key,
    pub modifiers: KeyModifiers,
}

impl KeyBinding {
    pub const EMPTY: Self = Self {
        key: Key::None,
        modifiers: KeyModifiers::NONE,
    };

    pub fn new(key: Key, modifiers: KeyModifiers) -> Self {
        Self { key, modifiers }
    }

    pub fn is_empty(&self) -> bool {
        self.key == Key::None
    }

    /// Returns true if this binding matches the given key + modifiers.
    pub fn matches(&self, key: Key, mods: KeyModifiers) -> bool {
        self.key == key && self.modifiers == mods
    }

    /// Display string like "Ctrl+S" or "Shift+Space".
    pub fn display(&self) -> String {
        if self.is_empty() {
            return "".into();
        }
        let mut parts = Vec::new();
        if self.modifiers.ctrl {
            parts.push("Ctrl");
        }
        if self.modifiers.shift {
            parts.push("Shift");
        }
        if self.modifiers.alt {
            parts.push("Alt");
        }
        let name = key_display(self.key);
        parts.push(&name);
        parts.join("+")
    }
}

impl fmt::Display for KeyBinding {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.display())
    }
}

fn key_display(key: Key) -> String {
    match key {
        Key::None => "".into(),
        Key::Space => "Space".into(),
        Key::Escape => "Esc".into(),
        Key::Back => "Backspace".into(),
        Key::Delete => "Del".into(),
        Key::Return => "Return".into(),
        Key::Enter => "Enter".into(),
        Key::Tab => "Tab".into(),
        Key::Left => "Left".into(),
        Key::Right => "Right".into(),
        Key::Up => "Up".into(),
        Key::Down => "Down".into(),
        Key::A | Key::B | Key::C | Key::D | Key::E | Key::F | Key::G | Key::H
        | Key::I | Key::J | Key::K | Key::L | Key::M | Key::N | Key::O | Key::P
        | Key::Q | Key::R | Key::S | Key::T | Key::U | Key::V | Key::W | Key::X
        | Key::Y | Key::Z => format!("{:?}", key),
        Key::D0 | Key::D1 | Key::D2 | Key::D3 | Key::D4 | Key::D5 | Key::D6
        | Key::D7 | Key::D8 | Key::D9 => format!("{}", key as u32 - Key::D0 as u32),
        _ => "?".into(),
    }
}

/// Update modifiers when a key is pressed.
pub fn modifiers_with_key_down(key: Key, current: KeyModifiers) -> KeyModifiers {
    match key {
        Key::LeftCtrl | Key::RightCtrl => KeyModifiers { ctrl: true, ..current },
        Key::LeftShift | Key::RightShift => KeyModifiers { shift: true, ..current },
        Key::LeftAlt | Key::RightAlt => KeyModifiers { alt: true, ..current },
        _ => current,
    }
}

/// Update modifiers when a key is released.
pub fn modifiers_after_key_up(key: Key, current: KeyModifiers) -> KeyModifiers {
    match key {
        Key::LeftCtrl | Key::RightCtrl => KeyModifiers { ctrl: false, ..current },
        Key::LeftShift | Key::RightShift => KeyModifiers { shift: false, ..current },
        Key::LeftAlt | Key::RightAlt => KeyModifiers { alt: false, ..current },
        _ => current,
    }
}

/// Returns true if the key is a modifier key.
pub fn is_modifier_key(key: Key) -> bool {
    matches!(
        key,
        Key::LeftShift | Key::RightShift | Key::LeftCtrl | Key::RightCtrl | Key::LeftAlt | Key::RightAlt
    )
}
