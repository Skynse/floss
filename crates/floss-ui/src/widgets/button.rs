use std::sync::Arc;
use floss_core::Rect;
use crate::{
    event::{Event, PointerButton}, widget::EventResponse,
    font::RasterFont,
    paint::{Color, PaintCtx},
    widget::{Constraints, Widget},
};

pub struct Button {
    rect: Rect,
    label: String,
    font: Arc<RasterFont>,
    bg: Color,
    bg_hover: Color,
    fg: Color,
    hovered: bool,
    padding: i32,
    on_click: Option<Box<dyn Fn() + 'static>>,
    cached_glyphs: Option<Vec<(i32, i32, Vec<u8>, u32, u32)>>,
}

impl Button {
    pub fn new(label: impl Into<String>, font: Arc<RasterFont>) -> Self {
        Self {
            rect: Rect::ZERO,
            label: label.into(),
            font,
            bg:       Color::BG_ELEMENT,
            bg_hover: Color::BG_HOVER,
            fg:       Color::TEXT,
            hovered:  false,
            padding:  8,
            on_click: None,
            cached_glyphs: None,
        }
    }

    pub fn with_bg(mut self, bg: Color, hover: Color) -> Self {
        self.bg = bg;
        self.bg_hover = hover;
        self
    }

    pub fn on_click(mut self, f: impl Fn() + 'static) -> Self {
        self.on_click = Some(Box::new(f));
        self
    }
}

impl Widget for Button {
    fn layout(&mut self, constraints: Constraints) -> (i32, i32) {
        let text_w = self.font.measure_width(&self.label);
        let text_h = self.font.line_height();
        let w = (text_w + self.padding * 2).min(constraints.max_width);
        let h = (text_h + self.padding * 2).min(constraints.max_height);
        (w, h)
    }

    fn set_rect(&mut self, rect: Rect) {
        self.rect = rect;
        self.cached_glyphs = None;
    }

    fn rect(&self) -> Rect { self.rect }

    fn paint(&mut self, ctx: &mut PaintCtx) {
        let bg = if self.hovered { self.bg_hover } else { self.bg };
        ctx.fill_rect(self.rect, bg);
        ctx.stroke_rect(self.rect, Color::BORDER);

        let glyphs = self.cached_glyphs.get_or_insert_with(|| {
            self.font.layout_text(&self.label)
        });

        let text_w = self.font.measure_width(&self.label);
        let text_x = self.rect.x + (self.rect.w - text_w) / 2;
        let text_y = self.rect.y + (self.rect.h - self.font.line_height()) / 2 + self.font.ascent();
        ctx.draw_glyphs(text_x, text_y, self.fg, glyphs);
    }

    fn event(&mut self, event: &Event) -> EventResponse {
        match event {
            Event::PointerMove { pos } => {
                self.hovered = self.hit_test(*pos);
                EventResponse::Ignored
            }
            Event::PointerDown { pos, button: PointerButton::Primary } => {
                if self.hit_test(*pos) {
                    if let Some(f) = &self.on_click { f(); }
                    return EventResponse::Handled;
                }
                EventResponse::Ignored
            }
            _ => EventResponse::Ignored,
        }
    }
}
