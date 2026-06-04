// Custom-painted widgets matching Avalonia FluentTheme + AppColors (see UI-REFERENCE.md).

use egui::{
    Align2, Color32, CornerRadius, FontId, Id, Pos2, Rect, Response, Sense, Stroke, StrokeKind, Ui,
    Vec2,
};

pub const B0: Color32 = Color32::from_rgb(0x18, 0x1a, 0x1f);
pub const B1: Color32 = Color32::from_rgb(0x20, 0x22, 0x27);
pub const B2: Color32 = Color32::from_rgb(0x28, 0x2a, 0x30);
pub const B3: Color32 = Color32::from_rgb(0x34, 0x36, 0x40);
pub const BSIDEBAR: Color32 = Color32::from_rgb(0x1c, 0x1e, 0x23);
pub const STR: Color32 = Color32::from_rgb(0x36, 0x38, 0x40);
pub const ACC: Color32 = Color32::from_rgb(0x00, 0x78, 0xf2);
pub const ACS: Color32 = Color32::from_rgb(0x0a, 0x4f, 0x9f);
pub const T1: Color32 = Color32::from_rgb(0xf0, 0xf2, 0xf5);
pub const T2: Color32 = Color32::from_rgb(0xd0, 0xd3, 0xd8);
pub const TM: Color32 = Color32::from_rgb(0x90, 0x95, 0x9c);
pub const SEL_BG: Color32 = Color32::from_rgb(0x2f, 0x3a, 0x48);
pub const SEL_BD: Color32 = Color32::from_rgb(0x48, 0x55, 0x66);
pub const WRN: Color32 = Color32::from_rgb(0xd2, 0x99, 0x22);
pub const ERR: Color32 = Color32::from_rgb(0xda, 0x36, 0x33);
pub const PN: Color32 = Color32::from_rgb(0xe8, 0x52, 0x7a);
pub const FOLD: Color32 = Color32::from_rgb(0x8a, 0xa3, 0xc4);
pub const FOLDA: Color32 = Color32::from_rgb(0xc5, 0xd8, 0xf0);
pub const EV: Color32 = Color32::from_rgb(0x8a, 0xa6, 0xcc);
pub const EOFF: Color32 = Color32::from_rgb(0x5b, 0x5b, 0x5b);
pub const HOV: Color32 = Color32::from_rgba_premultiplied(255, 255, 255, 20);
pub const PRS: Color32 = Color32::from_rgba_premultiplied(255, 255, 255, 36);
pub const DOT: Color32 = Color32::from_rgb(0xa0, 0xaa, 0xb4);
pub const R: f32 = 4.0;

/// Brush size palette values (MainWindow.BrushSizePalette.cs).
pub const BRUSH_SIZES: &[f64] = &[
    0.7, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 10.0, 12.0, 15.0, 17.0, 20.0, 25.0,
    30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 100.0, 120.0, 150.0, 170.0, 200.0, 250.0, 300.0, 400.0,
    500.0, 600.0, 700.0, 800.0, 1000.0, 1200.0, 1500.0, 1700.0, 2000.0,
];

/// Default swatch grid (MainWindow.axaml.cs).
pub const SWATCHES: &[Color32] = &[
    Color32::from_rgb(0xff, 0xff, 0xff),
    Color32::from_rgb(0xff, 0xcc, 0xcc),
    Color32::from_rgb(0xff, 0xdd, 0xaa),
    Color32::from_rgb(0xff, 0xee, 0x99),
    Color32::from_rgb(0xee, 0xff, 0x88),
    Color32::from_rgb(0xcc, 0xff, 0x99),
    Color32::from_rgb(0x99, 0xee, 0xcc),
    Color32::from_rgb(0x99, 0xcc, 0xff),
    Color32::from_rgb(0xc0, 0xae, 0xff),
    Color32::from_rgb(0xff, 0xaa, 0xdd),
    Color32::from_rgb(0xa0, 0xa0, 0xa0),
    Color32::from_rgb(0xff, 0x66, 0x66),
    Color32::from_rgb(0xff, 0xa0, 0x40),
    Color32::from_rgb(0xff, 0xee, 0x44),
    Color32::from_rgb(0xaa, 0xdd, 0x33),
    Color32::from_rgb(0x44, 0xcc, 0x55),
    Color32::from_rgb(0x33, 0xcc, 0xaa),
    Color32::from_rgb(0x44, 0x99, 0xff),
    Color32::from_rgb(0x99, 0x66, 0xff),
    Color32::from_rgb(0xff, 0x55, 0xbb),
    Color32::from_rgb(0x74, 0x74, 0x74),
    Color32::from_rgb(0xff, 0x22, 0x22),
    Color32::from_rgb(0xff, 0x77, 0x00),
    Color32::from_rgb(0xff, 0xdd, 0x00),
    Color32::from_rgb(0x88, 0xcc, 0x00),
    Color32::from_rgb(0x00, 0xbb, 0x33),
    Color32::from_rgb(0x00, 0xaa, 0x88),
    Color32::from_rgb(0x11, 0x66, 0xee),
    Color32::from_rgb(0x77, 0x33, 0xff),
    Color32::from_rgb(0xff, 0x22, 0xaa),
    Color32::from_rgb(0x38, 0x38, 0x38),
    Color32::from_rgb(0xaa, 0x00, 0x00),
    Color32::from_rgb(0xcc, 0x44, 0x00),
    Color32::from_rgb(0xaa, 0x88, 0x00),
    Color32::from_rgb(0x44, 0x88, 0x00),
    Color32::from_rgb(0x00, 0x66, 0x22),
    Color32::from_rgb(0x00, 0x66, 0x55),
    Color32::from_rgb(0x00, 0x33, 0xaa),
    Color32::from_rgb(0x33, 0x00, 0xaa),
    Color32::from_rgb(0xaa, 0x00, 0x66),
    Color32::from_rgb(0x22, 0x22, 0x22),
    Color32::from_rgb(0x66, 0x00, 0x00),
    Color32::from_rgb(0x88, 0x33, 0x00),
    Color32::from_rgb(0x66, 0x55, 0x00),
    Color32::from_rgb(0x22, 0x44, 0x00),
    Color32::from_rgb(0x00, 0x33, 0x11),
    Color32::from_rgb(0x00, 0x33, 0x33),
    Color32::from_rgb(0x00, 0x11, 0x77),
    Color32::from_rgb(0x22, 0x00, 0x66),
    Color32::from_rgb(0x66, 0x00, 0x33),
    Color32::from_rgb(0x0d, 0x0d, 0x0d),
    Color32::from_rgb(0x2d, 0x00, 0x00),
    Color32::from_rgb(0x3d, 0x18, 0x00),
    Color32::from_rgb(0x2d, 0x26, 0x00),
    Color32::from_rgb(0x0f, 0x1e, 0x00),
    Color32::from_rgb(0x00, 0x15, 0x08),
    Color32::from_rgb(0x00, 0x15, 0x15),
    Color32::from_rgb(0x00, 0x06, 0x33),
    Color32::from_rgb(0x0d, 0x00, 0x22),
    Color32::from_rgb(0x2d, 0x00, 0x15),
];

fn font_sz(sz: f32) -> FontId {
    FontId::proportional(sz)
}

pub fn install_fonts(ctx: &egui::Context) {
    let mut fonts = egui::FontDefinitions::default();
    fonts.font_data.insert(
        "inter".into(),
        std::sync::Arc::new(egui::FontData::from_static(include_bytes!(
            "../../../assets/Inter-Regular.ttf"
        ))),
    );
    fonts
        .families
        .entry(egui::FontFamily::Proportional)
        .or_default()
        .insert(0, "inter".into());
    fonts
        .families
        .entry(egui::FontFamily::Monospace)
        .or_default()
        .insert(0, "inter".into());
    ctx.set_fonts(fonts);
}

pub fn apply(ctx: &egui::Context) {
    let mut v = egui::Visuals::dark();
    v.panel_fill = B1;
    v.window_fill = B0;
    v.faint_bg_color = B2;
    v.extreme_bg_color = B0;
    v.code_bg_color = B2;
    v.widgets.noninteractive.bg_fill = B3;
    v.widgets.inactive.bg_fill = B3;
    v.widgets.hovered.bg_fill = B3;
    v.widgets.active.bg_fill = ACS;
    v.widgets.noninteractive.fg_stroke.color = T2;
    v.widgets.inactive.fg_stroke.color = T2;
    v.widgets.hovered.fg_stroke.color = T1;
    v.widgets.active.fg_stroke.color = T1;
    v.widgets.inactive.bg_stroke.color = STR;
    v.widgets.hovered.bg_stroke.color = ACC;
    v.widgets.active.bg_stroke.color = ACC;
    for w in [
        &mut v.widgets.noninteractive,
        &mut v.widgets.inactive,
        &mut v.widgets.hovered,
        &mut v.widgets.active,
    ] {
        w.corner_radius = CornerRadius::same(4);
    }
    v.selection.bg_fill = ACC;
    v.selection.stroke.color = ACC;
    v.hyperlink_color = ACC;
    v.warn_fg_color = WRN;
    v.error_fg_color = ERR;
    v.dark_mode = true;
    v.window_shadow = egui::epaint::Shadow {
        offset: [0, 0],
        blur: 0,
        spread: 0,
        color: Color32::TRANSPARENT,
    };
    ctx.set_visuals(v);

    let mut s = (*ctx.global_style()).clone();
    s.spacing.item_spacing = Vec2::new(4.0, 2.0);
    s.spacing.button_padding = Vec2::new(6.0, 2.0);
    s.spacing.indent = 8.0;
    s.interaction.selectable_labels = true;
    ctx.set_global_style(s);
}

pub fn menu_frame() -> egui::Frame {
    egui::Frame::new()
        .fill(B1)
        .inner_margin(egui::Margin::symmetric(4, 0))
        .stroke(Stroke::new(1.0, STR))
}

pub fn sidebar_frame() -> egui::Frame {
    egui::Frame::new().fill(BSIDEBAR).inner_margin(0)
}

pub fn dock_frame() -> egui::Frame {
    egui::Frame::new().fill(BSIDEBAR).inner_margin(egui::Margin::symmetric(0, 4))
}

pub fn center_frame() -> egui::Frame {
    egui::Frame::new().fill(B0).inner_margin(0)
}

pub fn text(ui: &Ui, pos: Pos2, txt: &str, sz: f32, col: Color32, align: Align2) {
    let g = ui
        .painter()
        .layout_no_wrap(txt.to_string(), font_sz(sz), col);
    ui.painter()
        .galley(pos - g.size() * align.to_sign(), g, Color32::TRANSPARENT);
}

fn text_v(ui: &Ui, rect: Rect, txt: &str, sz: f32, col: Color32) {
    let g = ui
        .painter()
        .layout_no_wrap(txt.to_string(), font_sz(sz), col);
    ui.painter()
        .galley(rect.center() - g.size() * 0.5, g, Color32::TRANSPARENT);
}

pub fn sep(ui: &mut Ui) {
    let (rect, _) = ui.allocate_exact_size(Vec2::new(ui.available_width(), 1.0), Sense::hover());
    ui.painter().rect_filled(rect, 0.0, STR);
}

pub fn menu_btn(ui: &mut Ui, label: &str) -> Response {
    let pad = Vec2::new(8.0, 2.0);
    let g = ui.painter().layout_no_wrap(label.to_string(), font_sz(11.0), T2);
    let sz = Vec2::new(g.size().x + pad.x * 2.0, 22.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        if resp.hovered() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        let g2 = ui.painter().layout_no_wrap(label.to_string(), font_sz(11.0), T2);
        ui.painter()
            .galley(rect.center() - g2.size() * 0.5, g2, Color32::TRANSPARENT);
    }
    resp
}

pub fn icon_btn(ui: &mut Ui, icon: &str) -> Response {
    let sz = Vec2::new(24.0, 24.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        if resp.hovered() && !resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        if resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, PRS);
        }
        text_v(ui, rect, icon, 12.0, T2);
    }
    resp
}

pub fn tool_btn(ui: &mut Ui, label: &str, active: bool) -> Response {
    let sz = Vec2::new(28.0, 26.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        if active {
            ui.painter().rect_filled(rect, R, ACS);
        } else if resp.hovered() && !resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        if resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, PRS);
        }
        let fg = if active { T1 } else { T2 };
        text_v(ui, rect, label, 10.0, fg);
    }
    resp
}

pub fn color_well(ui: &mut Ui, col: Color32) -> Response {
    let outer = Vec2::new(26.0, 24.0);
    let (rect, resp) = ui.allocate_exact_size(outer, Sense::click());
    if ui.is_rect_visible(rect) {
        if resp.hovered() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        let inner = rect.shrink(3.0);
        ui.painter().circle_filled(inner.center(), inner.width() * 0.45, col);
        ui.painter().circle_stroke(
            inner.center(),
            inner.width() * 0.45,
            Stroke::new(1.0, STR),
        );
    }
    resp
}

pub fn mini_btn(ui: &mut Ui, label: &str) -> Response {
    let sz = Vec2::new(22.0, 22.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        if resp.hovered() && !resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        if resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, PRS);
        }
        text_v(ui, rect, label, 11.0, T2);
    }
    resp
}

pub fn layer_action_btn(ui: &mut Ui, kind: LayerAction) -> Response {
    let sz = Vec2::new(24.0, 22.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        if resp.hovered() && !resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        if resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, PRS);
        }
        paint_layer_action(ui, rect, kind);
    }
    resp
}

#[derive(Clone, Copy)]
pub enum LayerAction {
    Add,
    Folder,
    Duplicate,
    Up,
    Down,
    Delete,
}

fn paint_layer_action(ui: &Ui, rect: Rect, kind: LayerAction) {
    let p = ui.painter();
    let c = rect.center();
    let s = Stroke::new(1.2, T2);
    match kind {
        LayerAction::Add => {
            p.line_segment(
                [Pos2::new(c.x - 4.0, c.y), Pos2::new(c.x + 4.0, c.y)],
                s,
            );
            p.line_segment(
                [Pos2::new(c.x, c.y - 4.0), Pos2::new(c.x, c.y + 4.0)],
                s,
            );
        }
        LayerAction::Folder => {
            let r = Rect::from_center_size(c + Vec2::new(0.0, 1.0), Vec2::new(12.0, 9.0));
            p.rect_stroke(r, 2.0, s, StrokeKind::Middle);
            p.line_segment(
                [Pos2::new(r.left() + 1.0, r.top()), Pos2::new(r.left() + 4.0, r.top() - 2.0)],
                s,
            );
            p.line_segment(
                [Pos2::new(r.left() + 4.0, r.top() - 2.0), Pos2::new(r.left() + 8.0, r.top() - 2.0)],
                s,
            );
            p.line_segment(
                [Pos2::new(r.left() + 8.0, r.top() - 2.0), Pos2::new(r.left() + 10.0, r.top())],
                s,
            );
        }
        LayerAction::Duplicate => {
            let o = Vec2::new(2.0, -2.0);
            let r1 = Rect::from_center_size(c + o, Vec2::new(9.0, 11.0));
            let r2 = Rect::from_center_size(c - o, Vec2::new(9.0, 11.0));
            p.rect_stroke(r2, 2.0, s, StrokeKind::Middle);
            p.rect_stroke(r1, 2.0, s, StrokeKind::Middle);
        }
        LayerAction::Up => paint_chevron_tri(p, c + Vec2::new(0.0, 2.0), true),
        LayerAction::Down => paint_chevron_tri(p, c - Vec2::new(0.0, 2.0), false),
        LayerAction::Delete => {
            text_v(ui, rect, "×", 14.0, TM);
        }
    }
}

fn paint_chevron_tri(p: &egui::Painter, c: Pos2, up: bool) {
    let col = T2;
    let s = 3.5;
    let pts = if up {
        [
            Pos2::new(c.x - s, c.y + s * 0.4),
            Pos2::new(c.x + s, c.y + s * 0.4),
            Pos2::new(c.x, c.y - s * 0.6),
        ]
    } else {
        [
            Pos2::new(c.x - s, c.y - s * 0.4),
            Pos2::new(c.x + s, c.y - s * 0.4),
            Pos2::new(c.x, c.y + s * 0.6),
        ]
    };
    let mut m = egui::epaint::Mesh::default();
    let b = m.vertices.len() as u32;
    for pt in pts {
        m.vertices.push(egui::epaint::Vertex {
            pos: pt,
            uv: Pos2::ZERO,
            color: col,
        });
    }
    m.add_triangle(b, b + 1, b + 2);
    p.add(egui::Shape::mesh(m));
}

pub fn toggle(ui: &mut Ui, label: &str, value: &mut bool) -> Response {
    let pad = Vec2::new(8.0, 3.0);
    let g = ui.painter().layout_no_wrap(label.to_string(), font_sz(10.0), T2);
    let sz = Vec2::new(g.size().x + pad.x * 2.0, 22.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        if *value {
            ui.painter().rect_filled(rect, R, ACS);
            if resp.hovered() && !resp.is_pointer_button_down_on() {
                ui.painter().rect_filled(rect, R, PRS);
            }
        } else if resp.hovered() && !resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        if resp.is_pointer_button_down_on() {
            ui.painter().rect_filled(rect, R, PRS);
        }
        let fg = if *value { T1 } else { T2 };
        let g2 = ui.painter().layout_no_wrap(label.to_string(), font_sz(10.0), fg);
        ui.painter()
            .galley(rect.center() - g2.size() * 0.5, g2, Color32::TRANSPARENT);
    }
    if resp.clicked() {
        *value = !*value;
    }
    resp
}

pub fn text_input(ui: &mut Ui, text: &mut String, id: Id) -> Response {
    let pad = Vec2::new(8.0, 4.0);
    let sz = Vec2::new(ui.available_width().max(60.0), 26.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    let focused = ui.memory(|m| m.has_focus(id));
    let p = ui.painter();
    let (bd_w, bd_c) = if focused {
        (1.5, ACC)
    } else if resp.hovered() {
        (1.0, TM)
    } else {
        (1.0, STR)
    };
    if ui.is_rect_visible(rect) {
        p.rect_filled(rect, 6.0, B3);
        p.rect_stroke(rect, 6.0, Stroke::new(bd_w, bd_c), StrokeKind::Middle);
    }
    if resp.clicked() {
        ui.memory_mut(|m| m.request_focus(id));
    }
    let display = if focused {
        let mut pw = text.clone();
        let mut changed = false;
        ui.ctx().input(|inp| {
            for ev in &inp.events {
                if let egui::Event::Text(t) = ev {
                    pw.push_str(t);
                    changed = true;
                }
            }
        });
        if changed {
            *text = pw.clone();
        }
        pw
    } else {
        text.clone()
    };
    if ui.is_rect_visible(rect) {
        let g = p.layout_no_wrap(
            if display.is_empty() {
                "Layer name".into()
            } else {
                display.clone()
            },
            font_sz(11.0),
            if display.is_empty() && !focused {
                TM
            } else {
                T1
            },
        );
        let gs = g.size();
        p.galley(rect.left_top() + pad, g, Color32::TRANSPARENT);
        if focused {
            let cx = rect.left() + pad.x + gs.x + 1.0;
            p.line_segment(
                [
                    Pos2::new(cx, rect.top() + pad.y),
                    Pos2::new(cx, rect.bottom() - pad.y),
                ],
                Stroke::new(1.0, T1),
            );
        }
    }
    resp
}

pub fn slider(ui: &mut Ui, value: &mut f32, range: std::ops::RangeInclusive<f32>) -> Response {
    let th = 16.0;
    let pad = 2.0;
    let sz = Vec2::new(ui.available_width().max(40.0), th + pad * 2.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click_and_drag());
    let track = Rect::from_min_size(rect.left_top() + Vec2::Y * pad, Vec2::new(rect.width(), th));
    let p = ui.painter();
    let t = ((*value - *range.start()) / (*range.end() - *range.start())).clamp(0.0, 1.0);
    if ui.is_rect_visible(rect) {
        p.rect_filled(track, 4.0, Color32::from_rgb(0x24, 0x27, 0x30));
        let fw = track.width() * t;
        if fw > 0.0 {
            p.rect_filled(
                Rect::from_min_size(track.min, Vec2::new(fw, th)),
                4.0,
                Color32::from_rgb(0x4a, 0x7b, 0xdb),
            );
        }
        let tx = (track.left() + fw).clamp(track.left() + 4.0, track.right() - 4.0);
        let tc = Color32::from_rgb(0x96, 0xb0, 0xdf);
        p.circle_filled(Pos2::new(tx, track.center().y), 4.5, tc);
    }
    if resp.dragged() {
        if let Some(mp) = resp.interact_pointer_pos() {
            *value = *range.start()
                + ((mp.x - track.left()) / track.width()).clamp(0.0, 1.0)
                    * (*range.end() - *range.start());
        }
    }
    resp
}

pub fn dock_tab<A: PartialEq + Copy>(ui: &mut Ui, value: &mut A, this: A, label: &str) -> Response {
    let pad = Vec2::new(12.0, 6.0);
    let g = ui.painter().layout_no_wrap(label.to_string(), font_sz(11.0), T2);
    let sz = Vec2::new(g.size().x + pad.x * 2.0, 30.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    let active = *value == this;
    let p = ui.painter();
    if ui.is_rect_visible(rect) {
        if active {
            p.rect_filled(rect, 0.0, Color32::from_rgba_premultiplied(0, 120, 242, 12));
        } else if resp.hovered() {
            p.rect_filled(rect, R, HOV);
        }
        let fg = if active { T1 } else { TM };
        let g2 = ui.painter().layout_no_wrap(label.to_string(), font_sz(11.0), fg);
        p.galley(rect.center() - g2.size() * 0.5, g2, Color32::TRANSPARENT);
        if active {
            let y = rect.bottom() - 1.0;
            p.line_segment(
                [
                    Pos2::new(rect.left() + 4.0, y),
                    Pos2::new(rect.right() - 4.0, y),
                ],
                Stroke::new(2.0, ACC),
            );
        }
    }
    if resp.clicked() {
        *value = this;
    }
    resp
}

pub fn doc_tab(ui: &mut Ui, title: &str, active: bool) -> Response {
    let pad = Vec2::new(8.0, 4.0);
    let g = ui
        .painter()
        .layout_no_wrap(title.to_string(), font_sz(11.0), if active { T1 } else { T2 });
    let sz = Vec2::new(g.size().x.clamp(96.0, 200.0) + pad.x * 2.0 + 20.0, 23.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        let bg = if active { B2 } else { B1 };
        let bd = if active { ACC } else { STR };
        ui.painter().rect_filled(rect, CornerRadius {
            nw: 2,
            ne: 2,
            sw: 0,
            se: 0,
        }, bg);
        let inner = rect.shrink(1.0);
        ui.painter().rect_stroke(
            inner,
            CornerRadius {
                nw: 2,
                ne: 2,
                sw: 0,
                se: 0,
            },
            Stroke::new(1.0, bd),
            StrokeKind::Inside,
        );
        let g2 = ui.painter().layout_no_wrap(title.to_string(), font_sz(11.0), if active { T1 } else { T2 });
        ui.painter()
            .galley(rect.left_top() + pad, g2, Color32::TRANSPARENT);
        let close = Rect::from_min_size(
            Pos2::new(rect.right() - 18.0, rect.center().y - 7.0),
            Vec2::new(14.0, 14.0),
        );
        text_v(ui, close, "×", 12.0, TM);
    }
    resp
}

pub fn combo(ui: &mut Ui, id: Id, selected: &str, add_contents: impl FnOnce(&mut Ui)) {
    let pad = Vec2::new(8.0, 3.0);
    let sz = Vec2::new(ui.available_width().max(60.0), 24.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    let p = ui.painter();
    let bd = if resp.hovered() { TM } else { STR };
    if ui.is_rect_visible(rect) {
        p.rect_filled(rect, 6.0, B0);
        p.rect_stroke(rect, 6.0, Stroke::new(1.0, bd), StrokeKind::Middle);
        let g = p.layout_no_wrap(selected.to_string(), font_sz(11.0), T2);
        p.galley(rect.left_top() + pad, g, Color32::TRANSPARENT);
        let cx = rect.right() - 12.0;
        let cy = rect.center().y;
        p.line_segment(
            [
                Pos2::new(cx - 3.0, cy - 1.5),
                Pos2::new(cx, cy + 2.5),
            ],
            Stroke::new(1.0, TM),
        );
        p.line_segment(
            [
                Pos2::new(cx, cy + 2.5),
                Pos2::new(cx + 3.0, cy - 1.5),
            ],
            Stroke::new(1.0, TM),
        );
    }
    if resp.clicked() {
        ui.memory_mut(|m| m.toggle_popup(id));
    }
    egui::popup_above_or_below_widget(
        ui,
        id,
        &resp,
        egui::AboveOrBelow::Below,
        egui::PopupCloseBehavior::CloseOnClickOutside,
        |ui: &mut Ui| {
            ui.set_min_width(rect.width());
            add_contents(ui);
            None::<()>
        },
    );
}

pub fn brush_size_palette(ui: &mut Ui, current: f64, on_pick: &mut impl FnMut(f64)) {
    const COLS: usize = 7;
    ui.spacing_mut().item_spacing = Vec2::new(1.0, 1.0);
    egui::Grid::new("brush_sizes")
        .num_columns(COLS)
        .spacing([1.0, 1.0])
        .show(ui, |ui| {
            for (i, &size) in BRUSH_SIZES.iter().enumerate() {
                if i > 0 && i % COLS == 0 {
                    ui.end_row();
                }
                if size_btn(ui, size, (current - size).abs() < 0.05).clicked() {
                    on_pick(size);
                }
            }
        });
}

fn size_btn(ui: &mut Ui, size: f64, selected: bool) -> Response {
    let sz = Vec2::new(22.0, 22.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        if selected {
            ui.painter().rect_filled(rect, R, ACS);
        } else if resp.hovered() {
            ui.painter().rect_filled(rect, R, HOV);
        }
        paint_size_dot(ui, rect, size, selected);
    }
    resp
}

fn paint_size_dot(ui: &Ui, rect: Rect, size: f64, selected: bool) {
    let p = ui.painter();
    let c = rect.center();
    let col = if selected { T1 } else { DOT };
    if size >= 250.0 {
        let txt = if size >= 1000.0 {
            format!("{:.0}k", size / 1000.0)
        } else {
            format!("{:.0}", size)
        };
        text_v(ui, rect, &txt, 8.0, col);
        return;
    }
    let normalized = (size + 1.0).log10() / 2001.0_f64.log10();
    let dot = (normalized * 22.0).clamp(3.0, 18.0) as f32;
    p.circle_filled(c, dot * 0.5, col);
    if size >= 10.0 && size < 100.0 {
        text_v(ui, rect, &format!("{:.0}", size), 7.0, Color32::WHITE);
    }
}

pub fn swatch_grid(ui: &mut Ui, cols: usize, on_pick: &mut impl FnMut(Color32)) {
    ui.spacing_mut().item_spacing = Vec2::new(2.0, 2.0);
    egui::Grid::new("swatches")
        .num_columns(cols)
        .spacing([2.0, 2.0])
        .show(ui, |ui| {
            for (i, &c) in SWATCHES.iter().enumerate() {
                if i > 0 && i % cols == 0 {
                    ui.end_row();
                }
                if swatch(ui, c).clicked() {
                    on_pick(c);
                }
            }
        });
}

pub fn swatch(ui: &mut Ui, col: Color32) -> Response {
    let sz = Vec2::new(14.0, 14.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    if ui.is_rect_visible(rect) {
        ui.painter().rect_filled(rect, 2.0, col);
        ui.painter()
            .rect_stroke(rect, 2.0, Stroke::new(1.0, STR), StrokeKind::Middle);
        if resp.hovered() {
            ui.painter()
                .rect_stroke(rect, 2.0, Stroke::new(1.0, ACC), StrokeKind::Middle);
        }
    }
    resp
}

pub fn hsv_picker(ui: &mut Ui, h: &mut f32, s: &mut f32, v: &mut f32) -> Response {
    let total_h = 130.0;
    let hue_h = 14.0;
    let pad = 4.0;
    let w = ui.available_width().max(120.0);
    let sz = Vec2::new(w, total_h);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click_and_drag());
    if !ui.is_rect_visible(rect) {
        return resp;
    }
    let p = ui.painter();
    let sv_rect = Rect::from_min_max(
        rect.min + Vec2::new(pad, pad),
        Pos2::new(rect.right() - pad, rect.bottom() - pad - hue_h - 4.0),
    );
    let hue_rect = Rect::from_min_max(
        Pos2::new(sv_rect.left(), sv_rect.bottom() + 4.0),
        Pos2::new(sv_rect.right(), rect.bottom() - pad),
    );

    // SV field: white→hue horizontal, transparent→black vertical
    let steps = 24;
    for iy in 0..steps {
        for ix in 0..steps {
            let u = ix as f32 / steps as f32;
            let t = iy as f32 / steps as f32;
            let (r, g, b) = hsv_to_rgb(*h, u, 1.0 - t);
            let cell = Rect::from_min_max(
                Pos2::new(
                    sv_rect.left() + sv_rect.width() * u,
                    sv_rect.top() + sv_rect.height() * t,
                ),
                Pos2::new(
                    sv_rect.left() + sv_rect.width() * (u + 1.0 / steps as f32),
                    sv_rect.top() + sv_rect.height() * (t + 1.0 / steps as f32),
                ),
            );
            p.rect_filled(cell, 0.0, Color32::from_rgb(r, g, b));
        }
    }
    p.rect_stroke(sv_rect, 2.0, Stroke::new(1.0, STR), StrokeKind::Middle);

    // Hue bar
    for ix in 0..steps {
        let u = ix as f32 / steps as f32;
        let (r, g, b) = hsv_to_rgb(u * 360.0, 1.0, 1.0);
        let cell = Rect::from_min_max(
            Pos2::new(
                hue_rect.left() + hue_rect.width() * u,
                hue_rect.top(),
            ),
            Pos2::new(
                hue_rect.left() + hue_rect.width() * (u + 1.0 / steps as f32),
                hue_rect.bottom(),
            ),
        );
        p.rect_filled(cell, 2.0, Color32::from_rgb(r, g, b));
    }
    p.rect_stroke(hue_rect, 2.0, Stroke::new(1.0, STR), StrokeKind::Middle);

    if let Some(pos) = resp.interact_pointer_pos() {
        if resp.dragged() || resp.clicked() {
            if sv_rect.contains(pos) {
                *s = ((pos.x - sv_rect.left()) / sv_rect.width()).clamp(0.0, 1.0);
                *v = (1.0 - (pos.y - sv_rect.top()) / sv_rect.height()).clamp(0.0, 1.0);
            } else if hue_rect.contains(pos) {
                *h = ((pos.x - hue_rect.left()) / hue_rect.width()).clamp(0.0, 1.0) * 360.0;
            }
        }
    }

    let dot_col = Color32::WHITE;
    let sx = sv_rect.left() + sv_rect.width() * *s;
    let sy = sv_rect.top() + sv_rect.height() * (1.0 - *v);
    p.circle_stroke(Pos2::new(sx, sy), 5.0, Stroke::new(1.5, dot_col));
    p.circle_stroke(Pos2::new(sx, sy), 5.0, Stroke::new(1.0, Color32::BLACK));

    let hx = hue_rect.left() + hue_rect.width() * (*h / 360.0);
    p.line_segment(
        [
            Pos2::new(hx, hue_rect.top() - 1.0),
            Pos2::new(hx, hue_rect.bottom() + 1.0),
        ],
        Stroke::new(2.0, Color32::WHITE),
    );
    resp
}

pub fn hex_input(ui: &mut Ui, hex: &mut String, id: Id) -> Response {
    let sz = Vec2::new(88.0, 24.0);
    let (rect, resp) = ui.allocate_exact_size(sz, Sense::click());
    let focused = ui.memory(|m| m.has_focus(id));
    let p = ui.painter();
    if ui.is_rect_visible(rect) {
        p.rect_filled(rect, 4.0, B3);
        p.rect_stroke(
            rect,
            4.0,
            Stroke::new(if focused { 1.5 } else { 1.0 }, if focused { ACC } else { STR }),
            StrokeKind::Middle,
        );
    }
    if resp.clicked() {
        ui.memory_mut(|m| m.request_focus(id));
    }
    if focused {
        let mut pw = hex.clone();
        ui.ctx().input(|inp| {
            for ev in &inp.events {
                if let egui::Event::Text(t) = ev {
                    pw.push_str(t);
                }
            }
        });
        *hex = pw;
    }
    if ui.is_rect_visible(rect) {
        let g = p.layout_no_wrap(hex.clone(), font_sz(11.0), T1);
        p.galley(rect.left_top() + Vec2::new(8.0, 4.0), g, Color32::TRANSPARENT);
    }
    resp
}

pub fn rgb_to_hsv(r: u8, g: u8, b: u8) -> (f32, f32, f32) {
    let rf = r as f32 / 255.0;
    let gf = g as f32 / 255.0;
    let bf = b as f32 / 255.0;
    let max = rf.max(gf).max(bf);
    let min = rf.min(gf).min(bf);
    let d = max - min;
    let h = if d < 1e-5 {
        0.0
    } else if max == rf {
        60.0 * (((gf - bf) / d) % 6.0)
    } else if max == gf {
        60.0 * (((bf - rf) / d) + 2.0)
    } else {
        60.0 * (((rf - gf) / d) + 4.0)
    };
    let h = if h < 0.0 { h + 360.0 } else { h };
    let s = if max < 1e-5 { 0.0 } else { d / max };
    (h, s, max)
}

pub fn hsv_to_rgb(h: f32, s: f32, v: f32) -> (u8, u8, u8) {
    let c = v * s;
    let x = c * (1.0 - ((h / 60.0) % 2.0 - 1.0).abs());
    let m = v - c;
    let (rp, gp, bp) = match h as i32 {
        0..=59 => (c, x, 0.0),
        60..=119 => (x, c, 0.0),
        120..=179 => (0.0, c, x),
        180..=239 => (0.0, x, c),
        240..=299 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    (
        ((rp + m) * 255.0).round() as u8,
        ((gp + m) * 255.0).round() as u8,
        ((bp + m) * 255.0).round() as u8,
    )
}

pub fn paint_checkerboard(p: &egui::Painter, rect: Rect) {
    let cs = 6.0;
    let x0 = (rect.min.x / cs).floor() as i32;
    let y0 = (rect.min.y / cs).floor() as i32;
    let x1 = ((rect.max.x + cs) / cs).ceil() as i32;
    let y1 = ((rect.max.y + cs) / cs).ceil() as i32;
    for y in y0..y1 {
        for x in x0..x1 {
            let c = if (x + y) & 1 == 0 {
                Color32::from_gray(136)
            } else {
                Color32::from_gray(187)
            };
            p.rect_filled(
                Rect::from_min_size(Pos2::new(x as f32 * cs, y as f32 * cs), Vec2::splat(cs)),
                0.0,
                c,
            );
        }
    }
}

#[allow(clippy::too_many_arguments)]
pub fn layer_row(
    ui: &mut Ui,
    li: usize,
    depth: usize,
    is_active: bool,
    is_selected: bool,
    is_group: bool,
    is_clip: bool,
    visible: bool,
    expanded: bool,
    name: &str,
    status: &str,
) -> LayerRow {
    let rh = 52.0;
    let indent = depth as f32 * 8.0;
    let (rect, resp) = ui.allocate_exact_size(Vec2::new(ui.available_width(), rh), Sense::click());
    let p = ui.painter();
    let (bg, bd) = if is_active {
        (ACC, ACC)
    } else if is_selected {
        (SEL_BG, SEL_BD)
    } else {
        (B2, STR)
    };
    let dim = if is_active {
        Color32::from_rgb(0x9f, 0xb6, 0xd6)
    } else if is_selected {
        T2
    } else {
        TM
    };
    let fg = if is_active || is_selected { T1 } else { T2 };

    if ui.is_rect_visible(rect) {
        let r = rect.shrink2(Vec2::new(0.0, 1.0));
        p.rect_filled(r, R, bg);
        p.rect_stroke(r, R, Stroke::new(1.0, bd), StrokeKind::Middle);
    }

    let mut x = rect.left() + indent + 4.0;
    let yc = rect.center().y;

    if is_clip {
        let cr = Rect::from_min_size(Pos2::new(x, rect.top() + 4.0), Vec2::new(3.0, rh - 8.0));
        p.rect_filled(cr, 2.0, PN);
    }
    x += 4.0;

    let mut tg = false;
    if is_group {
        let arr = Rect::from_min_size(Pos2::new(x, yc - 8.0), Vec2::new(16.0, 16.0));
        if ui
            .interact(arr, ui.make_persistent_id(("chev", li)), Sense::click())
            .clicked()
        {
            tg = true;
        }
        paint_chevron(ui, arr, expanded, if is_active { FOLDA } else { FOLD });
    }
    x += 16.0;

    let eye_r = Rect::from_min_size(Pos2::new(x, yc - 8.0), Vec2::new(20.0, 16.0));
    let mut tv = false;
    if ui
        .interact(eye_r, ui.make_persistent_id(("eye", li)), Sense::click())
        .clicked()
    {
        tv = true;
    }
    paint_eye(ui, eye_r, visible);
    x += 20.0;

    let thumb = Rect::from_min_size(Pos2::new(x, yc - 20.0), Vec2::new(48.0, 40.0));
    if ui.is_rect_visible(thumb) {
        paint_checkerboard(p, thumb);
        p.rect_stroke(thumb, 2.0, Stroke::new(1.0, STR), StrokeKind::Middle);
        let inner = thumb.shrink(4.0);
        p.rect_filled(inner, 2.0, Color32::from_rgba_premultiplied(60, 70, 90, 80));
    }
    x += 48.0 + 4.0;

    if ui.is_rect_visible(rect) {
        text(
            ui,
            Pos2::new(x, rect.top() + 10.0),
            name,
            11.0,
            fg,
            Align2::LEFT_TOP,
        );
        text(
            ui,
            Pos2::new(x, rect.top() + 26.0),
            status,
            9.0,
            dim,
            Align2::LEFT_TOP,
        );
    }

    LayerRow {
        click: resp.clicked(),
        tog_vis: tv,
        tog_grp: tg,
    }
}

pub struct LayerRow {
    pub click: bool,
    pub tog_vis: bool,
    pub tog_grp: bool,
}

fn paint_chevron(ui: &Ui, rect: Rect, open: bool, col: Color32) {
    let p = ui.painter();
    let cx = rect.center().x;
    let cy = rect.center().y;
    let s = 3.5;
    let pts: [Pos2; 3] = if open {
        [
            Pos2::new(cx - s, cy - s * 0.5),
            Pos2::new(cx + s, cy - s * 0.5),
            Pos2::new(cx, cy + s * 0.6),
        ]
    } else {
        [
            Pos2::new(cx - s * 0.2, cy - s),
            Pos2::new(cx + s * 0.7, cy),
            Pos2::new(cx - s * 0.2, cy + s),
        ]
    };
    let mut m = egui::epaint::Mesh::default();
    let b = m.vertices.len() as u32;
    for pt in pts {
        m.vertices.push(egui::epaint::Vertex {
            pos: pt,
            uv: Pos2::ZERO,
            color: col,
        });
    }
    m.add_triangle(b, b + 1, b + 2);
    p.add(egui::Shape::mesh(m));
}

fn paint_eye(ui: &Ui, rect: Rect, visible: bool) {
    let p = ui.painter();
    let col = if visible { EV } else { EOFF };
    let cx = rect.center().x;
    let cy = rect.center().y;
    let s = rect.width().min(rect.height()) * 0.33;
    let pts = [
        Pos2::new(cx - s, cy),
        Pos2::new(cx - s * 0.4, cy - s * 0.7),
        Pos2::new(cx + s * 0.4, cy - s * 0.7),
        Pos2::new(cx + s, cy),
        Pos2::new(cx + s * 0.4, cy + s * 0.7),
        Pos2::new(cx - s * 0.4, cy + s * 0.7),
    ];
    p.add(egui::Shape::line(pts.to_vec(), Stroke::new(1.0, col)));
    if visible {
        p.circle_filled(Pos2::new(cx, cy), s * 0.28, col);
    } else {
        p.line_segment(
            [
                Pos2::new(cx - s * 0.6, cy - s * 0.6),
                Pos2::new(cx + s * 0.6, cy + s * 0.6),
            ],
            Stroke::new(1.0, ERR),
        );
    }
}
