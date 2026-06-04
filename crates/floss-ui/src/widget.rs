use floss_core::Rect;
use crate::{event::Event, paint::PaintCtx};

/// How a widget wants to size itself along one axis.
#[derive(Clone, Copy, Debug)]
pub enum SizeHint {
    /// Fixed pixel size.
    Fixed(i32),
    /// Expand to fill remaining space. Weight is relative to other Expand children.
    Expand(u32),
    /// Size to content.
    Shrink,
}

impl Default for SizeHint { fn default() -> Self { Self::Shrink } }

/// Available space passed to a widget during layout.
#[derive(Clone, Copy, Debug)]
pub struct Constraints {
    pub max_width:  i32,
    pub max_height: i32,
}

impl Constraints {
    pub fn tight(w: i32, h: i32) -> Self { Self { max_width: w, max_height: h } }
    pub fn unbounded() -> Self { Self { max_width: i32::MAX, max_height: i32::MAX } }
}

/// Returned by event handlers.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EventResponse {
    /// Event was handled; stop propagation.
    Handled,
    /// Event was not handled; continue propagation.
    Ignored,
}

impl EventResponse {
    pub fn is_handled(self) -> bool { self == Self::Handled }
}

/// The core widget trait.
///
/// Three passes drive every frame:
///
/// 1. **Layout**: parent calls `layout()` to get the widget's desired size given
///    constraints, then calls `set_rect()` to assign the final position + size.
///    Container widgets must call `layout()` + `set_rect()` on all children inside
///    their own `set_rect()` implementation.
///
/// 2. **Paint**: `paint()` is called with a `PaintCtx` whose origin is at the
///    top-left of the window. Widgets draw themselves using their stored `rect()`,
///    then call `paint()` on children.
///
/// 3. **Event**: `event()` is called top-down. Widgets hit-test against their
///    `rect()`, then forward to children. Return `Handled` to stop propagation.
pub trait Widget: 'static {
    /// Measure: return desired (width, height) given constraints.
    /// Do NOT position children here.
    fn layout(&mut self, constraints: Constraints) -> (i32, i32);

    /// The parent assigns the final rect (position + size) by calling this.
    /// Container widgets MUST call layout() + set_rect() on all children here.
    fn set_rect(&mut self, rect: Rect);

    /// The rect assigned by the parent.
    fn rect(&self) -> Rect;

    /// Paint self and all children. `ctx` origin is top-left of the window.
    fn paint(&mut self, ctx: &mut PaintCtx);

    /// Handle an event. Hit-test against self.rect(), then forward to children.
    fn event(&mut self, event: &Event) -> EventResponse;

    /// Convenience: does this widget's rect contain a pointer position?
    fn hit_test(&self, pos: (f64, f64)) -> bool {
        let r = self.rect();
        pos.0 >= r.x as f64 && pos.0 < r.right() as f64
            && pos.1 >= r.y as f64 && pos.1 < r.bottom() as f64
    }
}

/// Type-erased widget box.
pub type BoxWidget = Box<dyn Widget>;

/// Helper: lay out a list of children in a row (horizontal) or column (vertical).
/// Returns the rects assigned to each child.
pub fn stack_layout(
    children: &mut [BoxWidget],
    rect: Rect,
    horizontal: bool,
    gap: i32,
) {
    if children.is_empty() { return; }

    // First pass: measure fixed children, count expand weights.
    let total_gap = gap * (children.len() as i32 - 1).max(0);
    let available = if horizontal {
        rect.w - total_gap
    } else {
        rect.h - total_gap
    };

    let mut fixed_total = 0i32;
    let mut expand_weight_total = 0u32;

    let sizes: Vec<(i32, SizeHint)> = children.iter_mut().map(|child| {
        let (hint_w, hint_h) = if horizontal {
            (SizeHint::Shrink, SizeHint::Expand(1))
        } else {
            (SizeHint::Expand(1), SizeHint::Shrink)
        };
        let _ = (hint_w, hint_h); // suppress unused warning

        let constraints = if horizontal {
            Constraints::tight(available, rect.h)
        } else {
            Constraints::tight(rect.w, available)
        };
        let (w, h) = child.layout(constraints);
        let main_size = if horizontal { w } else { h };
        (main_size, SizeHint::Shrink)
    }).collect();

    // For now: divide space evenly, proportional to measured sizes.
    // A proper implementation would use SizeHint from each widget.
    let total_desired: i32 = sizes.iter().map(|(s, _)| *s).sum();
    let cross = if horizontal { rect.h } else { rect.w };

    let mut cursor = if horizontal { rect.x } else { rect.y };
    for (i, child) in children.iter_mut().enumerate() {
        let desired_main = sizes[i].0;
        let main = if total_desired > 0 && available > 0 {
            // proportional allocation
            (desired_main as i64 * available as i64 / total_desired as i64) as i32
        } else {
            desired_main
        };

        let child_rect = if horizontal {
            Rect::new(cursor, rect.y, main.max(0), cross)
        } else {
            Rect::new(rect.x, cursor, cross, main.max(0))
        };
        child.set_rect(child_rect);
        cursor += main + gap;
    }
}
