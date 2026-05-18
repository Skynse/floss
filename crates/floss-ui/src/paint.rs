use floss_core::Rect;

/// XRGB color for the framebuffer (softbuffer format: 0x00RRGGBB).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Color(pub u32);

impl Color {
    pub const fn rgb(r: u8, g: u8, b: u8) -> Self {
        Color(((r as u32) << 16) | ((g as u32) << 8) | b as u32)
    }

    pub const fn rgba(r: u8, g: u8, b: u8, a: u8) -> Self {
        Color((a as u32) << 24 | ((r as u32) << 16) | ((g as u32) << 8) | b as u32)
    }

    pub fn alpha(self) -> u8 { ((self.0 >> 24) & 0xff) as u8 }
    pub fn r(self) -> u8 { ((self.0 >> 16) & 0xff) as u8 }
    pub fn g(self) -> u8 { ((self.0 >> 8)  & 0xff) as u8 }
    pub fn b(self) -> u8 { (self.0          & 0xff) as u8 }

    /// As softbuffer u32 (ignores alpha, caller alpha-composites separately).
    pub fn as_u32(self) -> u32 { self.0 & 0x00ff_ffff }

    // Theme palette
    pub const TRANSPARENT:    Color = Color::rgba(0, 0, 0, 0);
    pub const BLACK:          Color = Color::rgb(0x00, 0x00, 0x00);
    pub const WHITE:          Color = Color::rgb(0xff, 0xff, 0xff);
    pub const BG_BASE:        Color = Color::rgb(0x1e, 0x20, 0x22);
    pub const BG_PANEL:       Color = Color::rgb(0x24, 0x27, 0x29);
    pub const BG_ELEMENT:     Color = Color::rgb(0x2a, 0x2d, 0x2f);
    pub const BG_HOVER:       Color = Color::rgb(0x35, 0x38, 0x3a);
    pub const BG_ACTIVE:      Color = Color::rgb(0x3b, 0x82, 0xf6);
    pub const BORDER:         Color = Color::rgb(0x3a, 0x3d, 0x3f);
    pub const TEXT:           Color = Color::rgb(0xe8, 0xea, 0xed);
    pub const TEXT_DIM:       Color = Color::rgb(0x8a, 0x8d, 0x90);
}

/// Drawing context for a single frame.
/// All coordinates are in physical pixels; the origin is the top-left of the window.
pub struct PaintCtx<'a> {
    pub buf: &'a mut Vec<u32>,
    pub width: u32,
    pub height: u32,
    /// Current translation applied to all drawing calls.
    origin: (i32, i32),
    /// Clip stack. draw calls are clipped to the top of this stack.
    clip: Rect,
}

impl<'a> PaintCtx<'a> {
    pub fn new(buf: &'a mut Vec<u32>, width: u32, height: u32) -> Self {
        Self {
            clip: Rect::new(0, 0, width as i32, height as i32),
            buf,
            width,
            height,
            origin: (0, 0),
        }
    }

    /// Translate origin for all subsequent draw calls.
    pub fn with_origin<F: FnOnce(&mut PaintCtx)>(&mut self, dx: i32, dy: i32, f: F) {
        let prev = self.origin;
        let prev_clip = self.clip;
        self.origin = (prev.0 + dx, prev.1 + dy);
        f(self);
        self.origin = prev;
        self.clip = prev_clip;
    }

    /// Restrict drawing to `rect` (in current local coordinates).
    pub fn with_clip<F: FnOnce(&mut PaintCtx)>(&mut self, rect: Rect, f: F) {
        let prev_clip = self.clip;
        let world = rect.translate(self.origin.0, self.origin.1);
        self.clip = self.clip.intersect(world);
        f(self);
        self.clip = prev_clip;
    }

    /// Fill a solid rectangle. `rect` is in current local coords.
    pub fn fill_rect(&mut self, rect: Rect, color: Color) {
        let world = rect.translate(self.origin.0, self.origin.1);
        let clipped = world.intersect(self.clip);
        if clipped.is_empty() { return; }

        let pixel = color.as_u32();
        let stride = self.width as usize;
        for y in clipped.y..clipped.bottom() {
            let row_start = y as usize * stride + clipped.x as usize;
            let row_end   = row_start + clipped.w as usize;
            if row_end <= self.buf.len() {
                self.buf[row_start..row_end].fill(pixel);
            }
        }
    }

    /// Draw a 1px border around `rect`. `rect` is in current local coords.
    pub fn stroke_rect(&mut self, rect: Rect, color: Color) {
        // top / bottom
        self.fill_rect(Rect::new(rect.x, rect.y,           rect.w, 1),         color);
        self.fill_rect(Rect::new(rect.x, rect.bottom() - 1, rect.w, 1),        color);
        // left / right
        self.fill_rect(Rect::new(rect.x,           rect.y, 1, rect.h),         color);
        self.fill_rect(Rect::new(rect.right() - 1, rect.y, 1, rect.h),         color);
    }

    /// Blit pre-composited RGBA pixels (4 bytes each) into the framebuffer.
    /// Alpha is composited over whatever is already in the buffer.
    /// `x, y` are in current local coords.
    pub fn blit_rgba(&mut self, x: i32, y: i32, w: u32, h: u32, pixels: &[u8]) {
        let wx = x + self.origin.0;
        let wy = y + self.origin.1;
        let stride = self.width as usize;

        for row in 0..h as i32 {
            let py = wy + row;
            if py < self.clip.y || py >= self.clip.bottom() { continue; }
            for col in 0..w as i32 {
                let px = wx + col;
                if px < self.clip.x || px >= self.clip.right() { continue; }

                let src_idx = (row as usize * w as usize + col as usize) * 4;
                if src_idx + 3 >= pixels.len() { break; }

                let sa = pixels[src_idx + 3] as u32;
                if sa == 0 { continue; }

                let dst_idx = py as usize * stride + px as usize;
                if dst_idx >= self.buf.len() { continue; }

                let sr = pixels[src_idx]     as u32;
                let sg = pixels[src_idx + 1] as u32;
                let sb = pixels[src_idx + 2] as u32;

                if sa == 255 {
                    self.buf[dst_idx] = (sr << 16) | (sg << 8) | sb;
                } else {
                    let inv = 255 - sa;
                    let dst = self.buf[dst_idx];
                    let dr = (dst >> 16) & 0xff;
                    let dg = (dst >>  8) & 0xff;
                    let db =  dst        & 0xff;
                    let r = (sr * sa + dr * inv) / 255;
                    let g = (sg * sa + dg * inv) / 255;
                    let b = (sb * sa + db * inv) / 255;
                    self.buf[dst_idx] = (r << 16) | (g << 8) | b;
                }
            }
        }
    }

    /// Draw glyphs rasterized by fontdue.
    /// `bitmaps` is a slice of (x_offset, y_offset, metrics, bitmap) tuples
    /// as returned by `FontStore::layout_text`.
    pub fn draw_glyphs(
        &mut self,
        x: i32, y: i32,
        color: Color,
        glyphs: &[(i32, i32, Vec<u8>, u32, u32)], // (dx, dy, bitmap, w, h)
    ) {
        let cr = color.r() as u32;
        let cg = color.g() as u32;
        let cb = color.b() as u32;
        let wx = x + self.origin.0;
        let wy = y + self.origin.1;
        let stride = self.width as usize;

        for (dx, dy, bitmap, gw, gh) in glyphs {
            for row in 0..*gh as i32 {
                let py = wy + dy + row;
                if py < self.clip.y || py >= self.clip.bottom() { continue; }
                for col in 0..*gw as i32 {
                    let px = wx + dx + col;
                    if px < self.clip.x || px >= self.clip.right() { continue; }
                    let coverage = bitmap[(row as usize * *gw as usize) + col as usize] as u32;
                    if coverage == 0 { continue; }
                    let dst_idx = py as usize * stride + px as usize;
                    if dst_idx >= self.buf.len() { continue; }
                    let inv = 255 - coverage;
                    let dst = self.buf[dst_idx];
                    let dr = (dst >> 16) & 0xff;
                    let dg = (dst >>  8) & 0xff;
                    let db =  dst        & 0xff;
                    let r = (cr * coverage + dr * inv) / 255;
                    let g = (cg * coverage + dg * inv) / 255;
                    let b = (cb * coverage + db * inv) / 255;
                    self.buf[dst_idx] = (r << 16) | (g << 8) | b;
                }
            }
        }
    }
}
