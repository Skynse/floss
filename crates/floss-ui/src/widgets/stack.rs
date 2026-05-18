use floss_core::Rect;
use crate::{
    event::Event, widget::EventResponse,
    paint::PaintCtx,
    widget::{BoxWidget, Constraints, Widget, stack_layout},
};

macro_rules! impl_stack {
    ($name:ident, $horizontal:expr) => {
        pub struct $name {
            rect: Rect,
            children: Vec<BoxWidget>,
            gap: i32,
        }

        impl $name {
            pub fn new() -> Self {
                Self { rect: Rect::ZERO, children: Vec::new(), gap: 0 }
            }
            pub fn with_gap(mut self, px: i32) -> Self { self.gap = px; self }
            pub fn add(mut self, child: BoxWidget) -> Self {
                self.children.push(child);
                self
            }
        }

        impl Widget for $name {
            fn layout(&mut self, constraints: Constraints) -> (i32, i32) {
                (constraints.max_width.min(i32::MAX / 2),
                 constraints.max_height.min(i32::MAX / 2))
            }

            fn set_rect(&mut self, rect: Rect) {
                self.rect = rect;
                stack_layout(&mut self.children, rect, $horizontal, self.gap);
            }

            fn rect(&self) -> Rect { self.rect }

            fn paint(&mut self, ctx: &mut PaintCtx) {
                for child in &mut self.children {
                    child.paint(ctx);
                }
            }

            fn event(&mut self, event: &Event) -> EventResponse {
                if let Some(pos) = event.pointer_pos() {
                    if !self.hit_test(pos) { return EventResponse::Ignored; }
                }
                for child in self.children.iter_mut().rev() {
                    if child.event(event).is_handled() {
                        return EventResponse::Handled;
                    }
                }
                EventResponse::Ignored
            }
        }
    };
}

impl_stack!(HStack, true);
impl_stack!(VStack, false);
