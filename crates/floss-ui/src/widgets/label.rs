use std::sync::Arc;
use floss_core::Rect;
use crate::{
    event::Event, widget::EventResponse,
    font::RasterFont,
    paint::{Color, PaintCtx},
    widget::{Constraints, Widget},
};

pub struct Label {
    rect: Rect,
    text: String,
    color: Color,
    font: Arc<RasterFont>,
    cached_glyphs: Option<Vec<(i32, i32, Vec<u8>, u32, u32)>>,
}

impl Label {
    pub fn new(text: impl Into<String>, font: Arc<RasterFont>, color: Color) -> Self {
        Self {
            rect: Rect::ZERO,
            text: text.into(),
            color,
            font,
            cached_glyphs: None,
        }
    }

    pub fn set_text(&mut self, text: impl Into<String>) {
        self.text = text.into();
        self.cached_glyphs = None;
    }
}

impl Widget for Label {
    fn layout(&mut self, constraints: Constraints) -> (i32, i32) {
        let w = self.font.measure_width(&self.text).min(constraints.max_width);
        let h = self.font.line_height().min(constraints.max_height);
        (w, h)
    }

    fn set_rect(&mut self, rect: Rect) {
        self.rect = rect;
        self.cached_glyphs = None;
    }

    fn rect(&self) -> Rect { self.rect }

    fn paint(&mut self, ctx: &mut PaintCtx) {
        let glyphs = self.cached_glyphs.get_or_insert_with(|| {
            self.font.layout_text(&self.text)
        });
        let baseline = self.rect.y + self.font.ascent();
        ctx.draw_glyphs(self.rect.x, baseline, self.color, glyphs);
    }

    fn event(&mut self, _event: &Event) -> EventResponse {
        EventResponse::Ignored
    }
}
