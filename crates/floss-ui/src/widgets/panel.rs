use floss_core::Rect;
use crate::{
    event::Event, widget::EventResponse,
    paint::{Color, PaintCtx},
    widget::{BoxWidget, Constraints, Widget},
};

/// A container with a background color and optional border.
pub struct Panel {
    rect: Rect,
    bg: Color,
    border: Option<Color>,
    children: Vec<BoxWidget>,
    padding: i32,
}

impl Panel {
    pub fn new(bg: Color) -> Self {
        Self { rect: Rect::ZERO, bg, border: None, children: Vec::new(), padding: 0 }
    }

    pub fn with_border(mut self, color: Color) -> Self { self.border = Some(color); self }
    pub fn with_padding(mut self, px: i32) -> Self { self.padding = px; self }

    pub fn add(mut self, child: BoxWidget) -> Self {
        self.children.push(child);
        self
    }
}

impl Widget for Panel {
    fn layout(&mut self, constraints: Constraints) -> (i32, i32) {
        (constraints.max_width.min(i32::MAX / 2), constraints.max_height.min(i32::MAX / 2))
    }

    fn set_rect(&mut self, rect: Rect) {
        self.rect = rect;
        let inner = Rect::new(
            rect.x + self.padding,
            rect.y + self.padding,
            (rect.w - self.padding * 2).max(0),
            (rect.h - self.padding * 2).max(0),
        );
        // Stack children vertically by default.
        crate::widget::stack_layout(&mut self.children, inner, false, 0);
    }

    fn rect(&self) -> Rect { self.rect }

    fn paint(&mut self, ctx: &mut PaintCtx) {
        ctx.fill_rect(self.rect, self.bg);
        if let Some(border) = self.border {
            ctx.stroke_rect(self.rect, border);
        }
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
