use fontdue::{Font, FontSettings};

/// A loaded font with a fixed raster size.
pub struct RasterFont {
    font: Font,
    pub size_px: f32,
}

impl RasterFont {
    pub fn from_bytes(data: &[u8], size_px: f32) -> Result<Self, &'static str> {
        let font = Font::from_bytes(data, FontSettings::default())?;
        Ok(Self { font, size_px })
    }

    /// Rasterize a string into glyph bitmaps ready for PaintCtx::draw_glyphs.
    /// Returns a vec of (x_offset, y_offset, bitmap, width, height).
    pub fn layout_text(&self, text: &str) -> Vec<(i32, i32, Vec<u8>, u32, u32)> {
        let mut glyphs = Vec::new();
        let mut cursor_x = 0i32;

        for ch in text.chars() {
            let (metrics, bitmap) = self.font.rasterize(ch, self.size_px);
            if !bitmap.is_empty() {
                glyphs.push((
                    cursor_x + metrics.xmin,
                    -metrics.ymin - metrics.height as i32,
                    bitmap,
                    metrics.width as u32,
                    metrics.height as u32,
                ));
            }
            cursor_x += metrics.advance_width.ceil() as i32;
        }
        glyphs
    }

    /// Measure the pixel width of a string.
    pub fn measure_width(&self, text: &str) -> i32 {
        text.chars()
            .map(|ch| {
                let (metrics, _) = self.font.rasterize(ch, self.size_px);
                metrics.advance_width.ceil() as i32
            })
            .sum()
    }

    /// Line height in pixels.
    pub fn line_height(&self) -> i32 {
        let metrics = self.font.horizontal_line_metrics(self.size_px);
        metrics.map(|m| (m.ascent - m.descent + m.line_gap).ceil() as i32)
            .unwrap_or(self.size_px.ceil() as i32)
    }

    pub fn ascent(&self) -> i32 {
        self.font.horizontal_line_metrics(self.size_px)
            .map(|m| m.ascent.ceil() as i32)
            .unwrap_or(self.size_px.ceil() as i32)
    }
}
