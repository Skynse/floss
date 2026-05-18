/// Input events passed to widgets. floss-ui defines its own types;
/// the shell (floss-app) maps platform events to these.
#[derive(Clone, Debug)]
pub enum Event {
    PointerDown { pos: (f64, f64), button: PointerButton },
    PointerMove { pos: (f64, f64) },
    PointerUp   { pos: (f64, f64), button: PointerButton },
    PointerScroll { pos: (f64, f64), delta: (f64, f64) },
    KeyDown { key: Key, mods: Modifiers },
    KeyUp   { key: Key, mods: Modifiers },
    /// Window was resized. Widget tree must relayout.
    Resize  { width: u32, height: u32 },
    /// Tablet pressure changed (0.0–1.0). Separate from pointer events.
    TabletPressure(f32),
}

impl Event {
    /// Return pointer position if this is a pointer event.
    pub fn pointer_pos(&self) -> Option<(f64, f64)> {
        match self {
            Event::PointerDown { pos, .. }
            | Event::PointerMove { pos }
            | Event::PointerUp   { pos, .. }
            | Event::PointerScroll { pos, .. } => Some(*pos),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PointerButton {
    Primary,
    Secondary,
    Middle,
    Other(u16),
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Key {
    Char(char),
    Named(NamedKey),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NamedKey {
    Enter, Escape, Space, Tab, Backspace, Delete,
    ArrowLeft, ArrowRight, ArrowUp, ArrowDown,
    Home, End, PageUp, PageDown,
    F(u8),
    Shift, Control, Alt, Super,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Modifiers {
    pub shift:   bool,
    pub ctrl:    bool,
    pub alt:     bool,
    pub super_:  bool,
}
