use std::num::NonZeroU32;
use std::sync::{Arc, Mutex, atomic::{AtomicU32, Ordering}};

use winit::{
    application::ApplicationHandler,
    event::{ElementState, MouseButton, WindowEvent},
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop},
    keyboard::{Key as WinitKey, NamedKey},
    window::{Window, WindowId},
};
use softbuffer::{Context, Surface};

use floss_ui::{
    event::{Event, Modifiers, NamedKey as UiKey, PointerButton},
    font::RasterFont,
    paint::{Color, PaintCtx},
    widget::Widget,
    widgets::{Button, HStack, Label, Panel, VStack},
};
use floss_core::Rect;
use floss_document::DrawingDocument;
use floss_core::Color as DocColor;

// ── Tablet pressure ────────────────────────────────────────────────────────

fn spawn_tablet_pressure_thread() -> Arc<AtomicU32> {
    let pressure = Arc::new(AtomicU32::new(f32::to_bits(1.0)));
    let pressure_bg = Arc::clone(&pressure);
    std::thread::spawn(move || {
        use evdev::{AbsoluteAxisType, InputEventKind};
        let Some((_path, mut device)) = evdev::enumerate().find(|(_, d)| {
            d.supported_absolute_axes()
                .map_or(false, |axes| axes.contains(AbsoluteAxisType::ABS_PRESSURE))
        }) else {
            eprintln!("[tablet] no pressure-capable evdev device found");
            return;
        };
        let (abs_min, abs_max) = device
            .get_abs_state().ok()
            .and_then(|s| {
                let info = s.get(AbsoluteAxisType::ABS_PRESSURE.0 as usize)?;
                Some((info.minimum as f32, info.maximum as f32))
            })
            .unwrap_or((0.0, 4095.0));
        let range = (abs_max - abs_min).max(1.0);
        loop {
            match device.fetch_events() {
                Ok(evs) => {
                    for ev in evs {
                        if let InputEventKind::AbsAxis(AbsoluteAxisType::ABS_PRESSURE) = ev.kind() {
                            let norm = ((ev.value() as f32 - abs_min) / range).clamp(0.0, 1.0);
                            pressure_bg.store(f32::to_bits(norm), Ordering::Relaxed);
                        }
                    }
                }
                Err(_) => std::thread::sleep(std::time::Duration::from_secs(1)),
            }
        }
    });
    pressure
}

// ── App state ──────────────────────────────────────────────────────────────

struct DrawState {
    doc:           DrawingDocument,
    composite:     Vec<u8>,    // RGBA framebuffer for the doc
    brush_color:   DocColor,
    brush_size:    f64,
    drawing:       bool,
    last_pos:      Option<(f64, f64)>,
    doc_w:         u32,
    doc_h:         u32,
}

impl DrawState {
    fn new() -> Self {
        let doc = DrawingDocument::new(2048, 1536);
        let w = doc.width() as u32;
        let h = doc.height() as u32;
        let composite = vec![255u8; w as usize * h as usize * 4];
        Self {
            doc, composite, brush_color: DocColor::BLACK, brush_size: 12.0,
            drawing: false, last_pos: None, doc_w: w, doc_h: h,
        }
    }

    fn add_sample(&mut self, x: f64, y: f64, pressure: f64) {
        if let Some((lx, ly)) = self.last_pos {
            if (x - lx).powi(2) + (y - ly).powi(2) < 4.0 { return; }
        }
        self.last_pos = Some((x, y));
        stamp_circle(&mut self.doc, self.brush_color, self.brush_size, x, y, pressure);
        let w = self.doc_w as i32; let h = self.doc_h as i32;
        composite_region(&self.doc, &mut self.composite, self.doc_w, 0, 0, w, h);
    }

    fn commit(&mut self) { self.drawing = false; self.last_pos = None; }
}

// ── Winit application ──────────────────────────────────────────────────────

struct App {
    window:   Option<Arc<Window>>,
    surface:  Option<Surface<Arc<Window>, Arc<Window>>>,
    sb_ctx:   Option<Context<Arc<Window>>>,
    draw:     Arc<Mutex<DrawState>>,
    pressure: Arc<AtomicU32>,
    mods:     Modifiers,
    font:     Arc<RasterFont>,
    // canvas rect is computed on resize
    canvas_rect: Rect,
    // sidebar widths
    left_w:  i32,
    right_w: i32,
}

impl App {
    fn new() -> Self {
        // Embed a minimal font. In production load from disk.
        // For now fall back to an empty stub if loading fails.
        let font_data = include_bytes!("../../../assets/Inter-Regular.ttf");
        let font = Arc::new(
            RasterFont::from_bytes(font_data, 13.0)
                .expect("failed to load embedded font")
        );

        Self {
            window: None, surface: None, sb_ctx: None,
            draw: Arc::new(Mutex::new(DrawState::new())),
            pressure: spawn_tablet_pressure_thread(),
            mods: Modifiers::default(),
            font,
            canvas_rect: Rect::ZERO,
            left_w: 48,
            right_w: 240,
        }
    }

    fn canvas_rect_for(&self, win_w: u32, win_h: u32) -> Rect {
        Rect::new(
            self.left_w,
            0,
            (win_w as i32 - self.left_w - self.right_w).max(0),
            win_h as i32,
        )
    }

    fn paint_frame(&mut self) {
        let Some(window) = &self.window else { return };
        let Some(surface) = &mut self.surface else { return };

        let size = window.inner_size();
        let win_w = size.width;
        let win_h = size.height;
        if win_w == 0 || win_h == 0 { return; }

        if let (Ok(w), Ok(h)) = (NonZeroU32::try_from(win_w), NonZeroU32::try_from(win_h)) {
            let _ = surface.resize(w, h);
        }

        let mut sb_buf = match surface.buffer_mut() {
            Ok(b) => b,
            Err(_) => return,
        };

        let mut buf: Vec<u32> = vec![Color::BG_BASE.as_u32(); (win_w * win_h) as usize];
        let mut ctx = PaintCtx::new(&mut buf, win_w, win_h);

        // Left tool rail
        ctx.fill_rect(Rect::new(0, 0, self.left_w, win_h as i32), Color::BG_PANEL);

        // Right panel
        let right_x = win_w as i32 - self.right_w;
        ctx.fill_rect(Rect::new(right_x, 0, self.right_w, win_h as i32), Color::BG_PANEL);
        ctx.fill_rect(Rect::new(right_x, 0, 1, win_h as i32), Color::BORDER);

        // Canvas area — blit document composite
        let cr = self.canvas_rect;
        if !cr.is_empty() {
            let draw = self.draw.lock().unwrap();
            let (dw, dh) = (draw.doc_w, draw.doc_h);
            let scale = (cr.w as f64 / dw as f64).min(cr.h as f64 / dh as f64);
            let render_w = (dw as f64 * scale) as i32;
            let render_h = (dh as f64 * scale) as i32;
            let ox = cr.x + (cr.w - render_w) / 2;
            let oy = cr.y + (cr.h - render_h) / 2;

            // Checkerboard outside doc
            let cs = 12i32;
            for row in 0..(cr.h / cs + 1) {
                for col in 0..(cr.w / cs + 1) {
                    let cx = cr.x + col * cs;
                    let cy = cr.y + row * cs;
                    let inside_doc = cx >= ox && cy >= oy && cx < ox + render_w && cy < oy + render_h;
                    if inside_doc { continue; }
                    let l = if (col + row) % 2 == 0 { 0xbfbfbf } else { 0x8c8c8c };
                    ctx.fill_rect(
                        Rect::new(cx, cy, cs, cs).intersect(cr),
                        Color(l),
                    );
                }
            }

            // Nearest-neighbour scale of composite into canvas area
            if render_w > 0 && render_h > 0 {
                let src = &draw.composite;
                let stride = win_w as usize;
                for dy in 0..render_h {
                    let py = oy + dy;
                    if py < 0 || py >= win_h as i32 { continue; }
                    let src_y = (dy as f64 * dh as f64 / render_h as f64) as usize;
                    for dx in 0..render_w {
                        let px = ox + dx;
                        if px < 0 || px >= win_w as i32 { continue; }
                        let src_x = (dx as f64 * dw as f64 / render_w as f64) as usize;
                        let si = (src_y * dw as usize + src_x) * 4;
                        if si + 2 >= src.len() { continue; }
                        let r = src[si]     as u32;
                        let g = src[si + 1] as u32;
                        let b = src[si + 2] as u32;
                        buf[py as usize * stride + px as usize] = (r << 16) | (g << 8) | b;
                    }
                }
            }
        }

        sb_buf.copy_from_slice(&buf);
        let _ = sb_buf.present();
    }

    fn canvas_to_doc(&self, cx: f64, cy: f64) -> (f64, f64) {
        let draw = self.draw.lock().unwrap();
        let cr = self.canvas_rect;
        let (dw, dh) = (draw.doc_w as f64, draw.doc_h as f64);
        let scale = (cr.w as f64 / dw).min(cr.h as f64 / dh);
        let ox = cr.x as f64 + (cr.w as f64 - dw * scale) * 0.5;
        let oy = cr.y as f64 + (cr.h as f64 - dh * scale) * 0.5;
        ((cx - ox) / scale, (cy - oy) / scale)
    }

    fn translate_event(&self, event: &WindowEvent) -> Option<Event> {
        match event {
            WindowEvent::CursorMoved { position, .. } => {
                Some(Event::PointerMove { pos: (position.x, position.y) })
            }
            WindowEvent::MouseInput { state, button, .. } => {
                let btn = match button {
                    MouseButton::Left  => PointerButton::Primary,
                    MouseButton::Right => PointerButton::Secondary,
                    MouseButton::Middle => PointerButton::Middle,
                    MouseButton::Other(n) => PointerButton::Other(*n),
                    _ => PointerButton::Primary,
                };
                if state.is_pressed() {
                    // We don't have position here; winit fires CursorMoved before MouseInput
                    Some(Event::PointerDown { pos: (0.0, 0.0), button: btn })
                } else {
                    Some(Event::PointerUp { pos: (0.0, 0.0), button: btn })
                }
            }
            WindowEvent::ModifiersChanged(m) => {
                self.mods_from_winit(m.state());
                None
            }
            _ => None,
        }
    }

    fn mods_from_winit(&self, _state: winit::keyboard::ModifiersState) -> Modifiers {
        Modifiers::default() // TODO: map shift/ctrl/alt
    }
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        let attrs = Window::default_attributes()
            .with_title("Floss")
            .with_inner_size(winit::dpi::LogicalSize::new(1280u32, 800u32));

        let window = Arc::new(event_loop.create_window(attrs).unwrap());
        let ctx = Context::new(Arc::clone(&window)).unwrap();
        let surface = Surface::new(&ctx, Arc::clone(&window)).unwrap();

        let size = window.inner_size();
        self.canvas_rect = self.canvas_rect_for(size.width, size.height);

        self.sb_ctx  = Some(ctx);
        self.surface = Some(surface);
        self.window  = Some(window);

        self.paint_frame();
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
        match &event {
            WindowEvent::CloseRequested => event_loop.exit(),

            WindowEvent::Resized(size) => {
                self.canvas_rect = self.canvas_rect_for(size.width, size.height);
                self.paint_frame();
            }

            WindowEvent::RedrawRequested => {
                self.paint_frame();
            }

            WindowEvent::CursorMoved { position, .. } => {
                let pos = (position.x, position.y);
                let cr = self.canvas_rect;
                if cr.contains_point(pos.0 as i32, pos.1 as i32) {
                    let pressure = f32::from_bits(self.pressure.load(Ordering::Relaxed)) as f64;
                    let mut draw = self.draw.lock().unwrap();
                    if draw.drawing {
                        let (dx, dy) = {
                            let (dw, dh) = (draw.doc_w as f64, draw.doc_h as f64);
                            let scale = (cr.w as f64 / dw).min(cr.h as f64 / dh);
                            let ox = cr.x as f64 + (cr.w as f64 - dw * scale) * 0.5;
                            let oy = cr.y as f64 + (cr.h as f64 - dh * scale) * 0.5;
                            ((pos.0 - ox) / scale, (pos.1 - oy) / scale)
                        };
                        draw.add_sample(dx, dy, pressure);
                        drop(draw);
                        if let Some(w) = &self.window { w.request_redraw(); }
                    }
                }
            }

            WindowEvent::MouseInput { state, button: MouseButton::Left, .. } => {
                if state.is_pressed() {
                    // Get last known cursor position from winit — not available here.
                    // Mark drawing; actual position comes from the next CursorMoved.
                    self.draw.lock().unwrap().drawing = true;
                } else {
                    self.draw.lock().unwrap().commit();
                    if let Some(w) = &self.window { w.request_redraw(); }
                }
            }

            WindowEvent::KeyboardInput { event: ke, .. } => {
                if ke.state.is_pressed() {
                    let mut draw = self.draw.lock().unwrap();
                    if let WinitKey::Character(ch) = &ke.logical_key {
                        match ch.as_str() {
                            "[" => draw.brush_size = (draw.brush_size * 0.85).max(1.0),
                            "]" => draw.brush_size = (draw.brush_size * 1.18).min(500.0),
                            _ => {}
                        }
                    }
                }
            }

            _ => {}
        }
    }
}

// ── Entry point ────────────────────────────────────────────────────────────

fn main() {
    let event_loop = EventLoop::new().unwrap();
    event_loop.set_control_flow(ControlFlow::Wait);
    let mut app = App::new();
    event_loop.run_app(&mut app).unwrap();
}

// ── Drawing helpers (unchanged from before) ────────────────────────────────

fn composite_region(
    doc: &DrawingDocument, buf: &mut [u8], stride: u32,
    x0: i32, y0: i32, x1: i32, y1: i32,
) {
    for y in y0..y1 { for x in x0..x1 {
        let idx = (y as usize * stride as usize + x as usize) * 4;
        buf[idx] = 255; buf[idx+1] = 255; buf[idx+2] = 255; buf[idx+3] = 255;
    }}
    for i in 0..doc.layer_count() {
        let layer = doc.layer(i);
        if !layer.visible || layer.is_group { continue; }
        let op = layer.opacity;
        for y in y0..y1 { for x in x0..x1 {
            let px = layer.pixels.try_read_pixel(x, y);
            if px[3] == 0 { continue; }
            let idx = (y as usize * stride as usize + x as usize) * 4;
            let sa = (px[3] as f64 * op) as u32;
            if sa == 0 { continue; }
            let da = buf[idx+3] as u32;
            let oa = sa + da * (255 - sa) / 255;
            if oa == 0 { continue; }
            buf[idx]   = ((px[2] as u32 * sa + buf[idx]   as u32 * da * (255-sa)/255) / oa) as u8;
            buf[idx+1] = ((px[1] as u32 * sa + buf[idx+1] as u32 * da * (255-sa)/255) / oa) as u8;
            buf[idx+2] = ((px[0] as u32 * sa + buf[idx+2] as u32 * da * (255-sa)/255) / oa) as u8;
            buf[idx+3] = oa as u8;
        }}
    }
}

fn stamp_circle(doc: &mut DrawingDocument, color: DocColor, size: f64, cx: f64, cy: f64, pressure: f64) {
    let r = (size * pressure.max(0.1)).ceil() as i32;
    let icx = cx.round() as i32; let icy = cy.round() as i32;
    let sr = (color.r() * 255.0) as u32;
    let sg = (color.g() * 255.0) as u32;
    let sb = (color.b() * 255.0) as u32;
    for dy in -r..=r { for dx in -r..=r {
        let dist = ((dx*dx + dy*dy) as f64).sqrt();
        if dist > r as f64 { continue; }
        let a = ((r as f64 - dist + 0.5).clamp(0.0, 1.0) * 255.0 * pressure).round() as u8;
        if a == 0 { continue; }
        let x = icx + dx; let y = icy + dy;
        let e = doc.active_layer_mut().pixels.get_pixel(x, y);
        let da = e[3] as u32; let sa = a as u32;
        let oa = sa + da * (255 - sa) / 255;
        if oa == 0 { continue; }
        let ob = ((sb * sa + e[0] as u32 * da * (255-sa) / 255) / oa) as u8;
        let og = ((sg * sa + e[1] as u32 * da * (255-sa) / 255) / oa) as u8;
        let or_ = ((sr * sa + e[2] as u32 * da * (255-sa) / 255) / oa) as u8;
        doc.active_layer_mut().pixels.set_pixel(x, y, ob, og, or_, oa as u8);
    }}
}
