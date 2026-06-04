use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;

use eframe::egui;
use egui::{Color32, ColorImage, Context, Pos2, Rect, Sense, TextureHandle, TextureOptions, Ui, Vec2};

use floss_compositor::LayerCompositor;
use floss_core::{BlendMode, Color as DocColor, Rect as DocRect};
use floss_document::DrawingDocument;

use crate::tablet;
use crate::widgets;

#[derive(Clone, Copy, PartialEq, Eq)]
enum RightTab {
    Layers,
    Color,
    Properties,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum ActiveTool {
    Pen,
    Brush,
    Eraser,
    Smudge,
    Fill,
    Pick,
    Move,
    Zoom,
    Hand,
}

impl ActiveTool {
    const ALL: [ActiveTool; 9] = [
        ActiveTool::Pen,
        ActiveTool::Brush,
        ActiveTool::Eraser,
        ActiveTool::Smudge,
        ActiveTool::Fill,
        ActiveTool::Pick,
        ActiveTool::Move,
        ActiveTool::Zoom,
        ActiveTool::Hand,
    ];

    fn label(self) -> &'static str {
        match self {
            ActiveTool::Pen => "Pen",
            ActiveTool::Brush => "Br",
            ActiveTool::Eraser => "Er",
            ActiveTool::Smudge => "Sm",
            ActiveTool::Fill => "Fl",
            ActiveTool::Pick => "Pk",
            ActiveTool::Move => "Mv",
            ActiveTool::Zoom => "Zm",
            ActiveTool::Hand => "Hd",
        }
    }
}

pub struct FlossApp {
    doc: DrawingDocument,
    compositor: LayerCompositor,
    composite_tex: Option<TextureHandle>,
    brush_color: DocColor,
    brush_size: f64,
    brush_flow: f32,
    brush_opacity: f32,
    last_pos: Option<(f64, f64)>,
    pressure: Arc<AtomicU32>,
    canvas_zoom: f32,
    canvas_pan: Vec2,
    needs_upload: bool,
    expanded_groups: std::collections::HashSet<usize>,
    selected_layer: Option<usize>,
    right_tab: RightTab,
    layer_name_buf: String,
    doc_title: String,
    active_tool: ActiveTool,
    color_h: f32,
    color_s: f32,
    color_v: f32,
    hex_buf: String,
}

impl FlossApp {
    pub fn new(psd_path: Option<PathBuf>) -> Self {
        let (h, s, v) = widgets::rgb_to_hsv(0, 0, 0);
        let mut this = Self {
            doc: DrawingDocument::new(2048, 1536),
            compositor: LayerCompositor::new(2048, 1536),
            composite_tex: None,
            brush_color: DocColor::BLACK,
            brush_size: 12.0,
            brush_flow: 1.0,
            brush_opacity: 1.0,
            last_pos: None,
            pressure: tablet::spawn(),
            canvas_zoom: 1.0,
            canvas_pan: Vec2::ZERO,
            needs_upload: true,
            expanded_groups: std::collections::HashSet::new(),
            selected_layer: None,
            right_tab: RightTab::Layers,
            layer_name_buf: String::new(),
            doc_title: "Untitled.psd".into(),
            active_tool: ActiveTool::Brush,
            color_h: h,
            color_s: s,
            color_v: v,
            hex_buf: "#000000".into(),
        };
        this.sync_hex_from_brush();
        if let Some(path) = psd_path {
            this.load_psd(&path);
        }
        this
    }

    fn sync_hex_from_brush(&mut self) {
        self.hex_buf = format!(
            "#{:02X}{:02X}{:02X}",
            (self.brush_color.r() * 255.0) as u8,
            (self.brush_color.g() * 255.0) as u8,
            (self.brush_color.b() * 255.0) as u8,
        );
        let (h, s, v) = widgets::rgb_to_hsv(
            (self.brush_color.r() * 255.0) as u8,
            (self.brush_color.g() * 255.0) as u8,
            (self.brush_color.b() * 255.0) as u8,
        );
        self.color_h = h;
        self.color_s = s;
        self.color_v = v;
    }

    fn set_brush_from_hsv(&mut self) {
        let (r, g, b) = widgets::hsv_to_rgb(self.color_h, self.color_s, self.color_v);
        self.brush_color = DocColor::from_bytes(r, g, b, 255);
        self.sync_hex_from_brush();
    }

    fn set_brush_rgb(&mut self, r: u8, g: u8, b: u8) {
        self.brush_color = DocColor::from_bytes(r, g, b, 255);
        self.sync_hex_from_brush();
    }

    fn try_apply_hex(&mut self) {
        let s = self.hex_buf.trim().trim_start_matches('#');
        if s.len() != 6 {
            return;
        }
        let Ok(v) = u32::from_str_radix(s, 16) else {
            return;
        };
        self.set_brush_rgb(((v >> 16) & 0xff) as u8, ((v >> 8) & 0xff) as u8, (v & 0xff) as u8);
    }

    fn load_psd(&mut self, path: &std::path::Path) {
        let file = match std::fs::File::open(path) {
            Ok(f) => f,
            Err(e) => {
                eprintln!("Cannot open {}: {}", path.display(), e);
                return;
            }
        };
        let mut buf = std::io::BufReader::new(file);
        match floss_psd::reader::read_psd(&mut buf) {
            Ok(psd) => {
                self.doc = floss_psd::import_psd(psd);
                self.compositor = LayerCompositor::new(self.doc.width(), self.doc.height());
                self.compositor.invalidate(None);
                self.needs_upload = true;
                self.expanded_groups.clear();
                self.doc_title = path
                    .file_name()
                    .and_then(|n| n.to_str())
                    .unwrap_or("Document.psd")
                    .to_string();
            }
            Err(e) => eprintln!("PSD import failed: {}", e),
        }
    }

    fn upload_texture(&mut self, ctx: &Context) {
        let composite = self.compositor.composite(&mut self.doc);
        let w = self.doc.width() as usize;
        let h = self.doc.height() as usize;
        let pixels: Vec<Color32> = composite
            .chunks_exact(4)
            .map(|p| Color32::from_rgba_unmultiplied(p[2], p[1], p[0], p[3]))
            .collect();
        let img = ColorImage {
            size: [w, h],
            pixels,
            source_size: Vec2::new(w as f32, h as f32),
        };
        if let Some(ref mut tex) = self.composite_tex {
            tex.set(img, TextureOptions::NEAREST);
        } else {
            self.composite_tex =
                Some(ctx.load_texture("floss-canvas", img, TextureOptions::NEAREST));
        }
    }

    fn collect_subtree(&self, li: usize, depth: usize, out: &mut Vec<(usize, usize)>) {
        out.push((li, depth));
        if self.doc.layer(li).is_group && self.expanded_groups.contains(&li) {
            for i in 0..self.doc.layer_count() {
                if self.doc.layer(i).parent_group == li as i32 {
                    self.collect_subtree(i, depth + 1, out);
                }
            }
        }
    }

    fn status_line(&self, li: usize) -> String {
        let l = self.doc.layer(li);
        if l.is_group {
            let mut n = 0;
            for i in 0..self.doc.layer_count() {
                if self.doc.layer(i).parent_group == li as i32 {
                    n += 1;
                }
            }
            return format!("{} layer{}", n, if n == 1 { "" } else { "s" });
        }
        let mut f = Vec::new();
        if l.locked {
            f.push("Lock");
        }
        if l.is_alpha_locked {
            f.push("Alpha");
        }
        if l.is_reference {
            f.push("Ref");
        }
        if l.is_clipping {
            f.push("Clip");
        }
        if l.is_paper {
            f.push("Paper");
        }
        let sfx = if f.is_empty() {
            String::new()
        } else {
            format!("  {}", f.join(" "))
        };
        format!(
            "{}%  {}{}",
            (l.opacity * 100.0) as i32,
            blend_label(l.blend_mode),
            sfx
        )
    }

    fn brush_ui_color(&self) -> Color32 {
        Color32::from_rgb(
            (self.brush_color.r() * 255.0) as u8,
            (self.brush_color.g() * 255.0) as u8,
            (self.brush_color.b() * 255.0) as u8,
        )
    }
}

fn blend_label(m: BlendMode) -> &'static str {
    match m {
        BlendMode::Normal => "Normal",
        BlendMode::Multiply => "Multiply",
        BlendMode::Screen => "Screen",
        BlendMode::Overlay => "Overlay",
        BlendMode::SoftLight => "Soft Light",
        BlendMode::HardLight => "Hard Light",
        BlendMode::ColorDodge => "Color Dodge",
        BlendMode::ColorBurn => "Color Burn",
        BlendMode::Darken => "Darken",
        BlendMode::Lighten => "Lighten",
        BlendMode::Difference => "Difference",
        BlendMode::PassThrough => "Pass Through",
        _ => "Blend",
    }
}

impl eframe::App for FlossApp {
    fn ui(&mut self, ui: &mut Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        widgets::apply(&ctx);

        if self.needs_upload || self.composite_tex.is_none() {
            self.upload_texture(&ctx);
            self.needs_upload = false;
        }

        // Menu bar — full width, 22px
        egui::Panel::top("menu")
            .frame(widgets::menu_frame())
            .exact_height(22.0)
            .resizable(false)
            .show_inside(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.set_height(22.0);
                    for name in ["File", "Edit", "View", "Layer", "Brush", "Window", "Help"] {
                        let _ = widgets::menu_btn(ui, name);
                    }
                });
            });

        // Left tool rail — 48px, BgSidebar
        egui::Panel::left("tools")
            .frame(widgets::sidebar_frame().stroke(egui::Stroke::new(1.0, widgets::STR)))
            .exact_width(48.0)
            .resizable(false)
            .show_inside(ui, |ui| {
                ui.vertical_centered(|ui| {
                    ui.add_space(4.0);
                    for tool in ActiveTool::ALL {
                        if widgets::tool_btn(ui, tool.label(), self.active_tool == tool).clicked()
                        {
                            self.active_tool = tool;
                        }
                    }
                    widgets::sep(ui);
                    ui.add_space(4.0);
                    if widgets::color_well(ui, self.brush_ui_color()).clicked() {
                        self.right_tab = RightTab::Color;
                    }
                });
            });

        // Right dock — 250px default
        egui::Panel::right("dock")
            .frame(
                widgets::dock_frame().stroke(egui::Stroke::new(1.0, widgets::STR)),
            )
            .default_width(250.0)
            .min_width(200.0)
            .max_width(440.0)
            .resizable(true)
            .show_inside(ui, |ui| {
                ui.set_min_width(200.0);
                ui.horizontal(|ui| {
                    widgets::dock_tab(ui, &mut self.right_tab, RightTab::Layers, "Layers");
                    widgets::dock_tab(ui, &mut self.right_tab, RightTab::Color, "Color");
                    widgets::dock_tab(
                        ui,
                        &mut self.right_tab,
                        RightTab::Properties,
                        "Properties",
                    );
                });
                widgets::sep(ui);
                ui.add_space(4.0);
                match self.right_tab {
                    RightTab::Layers => self.layer_panel(ui),
                    RightTab::Color => self.color_panel(ui),
                    RightTab::Properties => self.properties_panel(ui),
                }
            });

        // Center: tab bar | status | canvas | footer (footer only under canvas)
        egui::CentralPanel::default()
            .frame(widgets::center_frame())
            .show_inside(ui, |ui| {
                self.center_column(ui);
            });
    }
}

impl FlossApp {
    fn center_column(&mut self, ui: &mut Ui) {
        // Tab bar 26px
        ui.allocate_ui_with_layout(
            Vec2::new(ui.available_width(), 26.0),
            egui::Layout::left_to_right(egui::Align::Center),
            |ui| {
                ui.set_height(26.0);
                ui.horizontal(|ui| {
                    let _ = widgets::doc_tab(ui, &self.doc_title, true);
                    if widgets::mini_btn(ui, "+").clicked() {}
                });
            },
        );
        widgets::sep(ui);

        // Status bar 18px
        ui.allocate_ui_with_layout(
            Vec2::new(ui.available_width(), 18.0),
            egui::Layout::left_to_right(egui::Align::Center),
            |ui| {
                ui.set_height(18.0);
                ui.label(
                    egui::RichText::new(format!(
                        "{} × {}   Zoom {:.0}%   {}",
                        self.doc.width(),
                        self.doc.height(),
                        self.canvas_zoom * 100.0,
                        self.doc.active_layer().name,
                    ))
                    .size(10.0)
                    .color(widgets::TM),
                );
            },
        );
        widgets::sep(ui);

        // Canvas workspace
        let footer_h = 20.0;
        let canvas_h = (ui.available_height() - footer_h).max(0.0);
        ui.allocate_ui_with_layout(
            Vec2::new(ui.available_width(), canvas_h),
            egui::Layout::top_down(egui::Align::LEFT),
            |ui| {
                self.canvas_ui(ui);
            },
        );

        // Footer 20px
        ui.allocate_ui_with_layout(
            Vec2::new(ui.available_width(), footer_h),
            egui::Layout::left_to_right(egui::Align::Center),
            |ui| {
                ui.set_height(footer_h);
                let sw = Rect::from_min_size(ui.cursor().min, Vec2::new(14.0, 14.0));
                ui.painter().rect_filled(sw, 2.0, self.brush_ui_color());
                ui.painter().rect_stroke(
                    sw,
                    2.0,
                    egui::Stroke::new(1.0, widgets::STR),
                    egui::StrokeKind::Middle,
                );
                ui.add_space(8.0);
                let pct = f32::from_bits(self.pressure.load(Ordering::Relaxed)) * 100.0;
                ui.label(
                    egui::RichText::new(format!(
                        "{}   {:.0}px   Flow {:.0}%   Opacity {:.0}%   Pressure {:.0}%",
                        self.active_tool.label(),
                        self.brush_size,
                        self.brush_flow * 100.0,
                        self.brush_opacity * 100.0,
                        pct,
                    ))
                    .size(10.0)
                    .color(widgets::T2),
                );
            },
        );
    }

    fn layer_panel(&mut self, ui: &mut Ui) {
        ui.spacing_mut().item_spacing.y = 6.0;

        ui.horizontal(|ui| {
            let _ = widgets::icon_btn(ui, "⋮");
            if self.layer_name_buf.is_empty() {
                self.layer_name_buf = self.doc.active_layer().name.clone();
            }
            widgets::text_input(ui, &mut self.layer_name_buf, egui::Id::new("lname"));
        });

        ui.horizontal(|ui| {
            let avail = ui.available_width();
            let blend_w = avail * 0.48;
            ui.allocate_ui_with_layout(
                Vec2::new(blend_w, 24.0),
                egui::Layout::left_to_right(egui::Align::Center),
                |ui| {
                    let mode = self.doc.active_layer().blend_mode;
                    widgets::combo(ui, egui::Id::new("blend"), blend_label(mode), |ui| {
                        for m in [
                            BlendMode::Normal,
                            BlendMode::Multiply,
                            BlendMode::Screen,
                            BlendMode::Overlay,
                            BlendMode::SoftLight,
                            BlendMode::HardLight,
                            BlendMode::PassThrough,
                        ] {
                            if ui
                                .selectable_label(false, blend_label(m))
                                .clicked()
                            {
                                self.doc.active_layer_mut().blend_mode = m;
                                self.compositor.invalidate(None);
                                self.needs_upload = true;
                            }
                        }
                    });
                },
            );
            ui.add_space(4.0);
            ui.allocate_ui_with_layout(
                Vec2::new(avail - blend_w - 4.0, 24.0),
                egui::Layout::left_to_right(egui::Align::Center),
                |ui| {
                    let mut op = self.doc.active_layer().opacity as f32;
                    if widgets::slider(ui, &mut op, 0.0..=1.0).changed() {
                        self.doc.active_layer_mut().opacity = op as f64;
                        self.compositor.invalidate(None);
                        self.needs_upload = true;
                    }
                },
            );
        });

        ui.horizontal_wrapped(|ui| {
            let l = self.doc.active_layer_mut();
            let mut locked = l.locked;
            if widgets::toggle(ui, "Lock", &mut locked).clicked() {
                l.locked = locked;
            }
            let mut al = l.is_alpha_locked;
            if widgets::toggle(ui, "Alpha", &mut al).clicked() {
                l.is_alpha_locked = al;
            }
            let mut cl = l.is_clipping;
            if widgets::toggle(ui, "Clip", &mut cl).clicked() {
                l.is_clipping = cl;
                self.compositor.invalidate(None);
                self.needs_upload = true;
            }
            let mut rf = l.is_reference;
            if widgets::toggle(ui, "Ref", &mut rf).clicked() {
                l.is_reference = rf;
            }
        });

        widgets::sep(ui);

        let active = self.doc.active_layer_index();
        let roots: Vec<usize> = (0..self.doc.layer_count())
            .filter(|&i| {
                let l = self.doc.layer(i);
                l.parent_group < 0 && !l.is_paper
            })
            .collect();
        let mut display = Vec::new();
        for &r in &roots {
            self.collect_subtree(r, 0, &mut display);
        }

        struct Snap {
            li: usize,
            d: usize,
            act: bool,
            sel: bool,
            grp: bool,
            clp: bool,
            vis: bool,
            exp: bool,
            nm: String,
            st: String,
        }
        let snaps: Vec<Snap> = display
            .iter()
            .map(|&(li, d)| {
                let l = self.doc.layer(li);
                Snap {
                    li,
                    d,
                    act: active == li,
                    sel: self.selected_layer == Some(li),
                    grp: l.is_group,
                    clp: l.is_clipping,
                    vis: l.visible,
                    exp: self.expanded_groups.contains(&li),
                    nm: l.name.clone(),
                    st: self.status_line(li),
                }
            })
            .collect();

        let row_h = 52.0;
        let total = snaps.len();
        let list_h = ui.available_height().max(80.0);
        ui.allocate_ui_with_layout(
            Vec2::new(ui.available_width(), list_h),
            egui::Layout::top_down(egui::Align::LEFT),
            |ui| {
                egui::ScrollArea::vertical()
                    .id_salt("layers")
                    .auto_shrink([false; 2])
                    .show_rows(ui, row_h, total, |ui, range| {
                        for idx in range {
                            let s = &snaps[idx];
                            let r = widgets::layer_row(
                                ui,
                                s.li,
                                s.d,
                                s.act,
                                s.sel,
                                s.grp,
                                s.clp,
                                s.vis,
                                s.exp,
                                &s.nm,
                                &s.st,
                            );
                            if r.tog_vis {
                                self.doc.layer_mut(s.li).visible = !s.vis;
                                self.compositor.invalidate(None);
                                self.needs_upload = true;
                            }
                            if r.tog_grp {
                                if s.exp {
                                    self.expanded_groups.remove(&s.li);
                                } else {
                                    self.expanded_groups.insert(s.li);
                                }
                            }
                            if r.click {
                                self.selected_layer = Some(s.li);
                                self.doc.set_active_layer(s.li);
                                self.layer_name_buf = self.doc.layer(s.li).name.clone();
                            }
                        }
                    });
            },
        );

        widgets::sep(ui);
        ui.horizontal_wrapped(|ui| {
            let _ = widgets::layer_action_btn(ui, widgets::LayerAction::Add);
            let _ = widgets::layer_action_btn(ui, widgets::LayerAction::Folder);
            let _ = widgets::layer_action_btn(ui, widgets::LayerAction::Duplicate);
            let _ = widgets::layer_action_btn(ui, widgets::LayerAction::Up);
            let _ = widgets::layer_action_btn(ui, widgets::LayerAction::Down);
            let _ = widgets::layer_action_btn(ui, widgets::LayerAction::Delete);
        });
    }

    fn color_panel(&mut self, ui: &mut Ui) {
        ui.horizontal(|ui| {
            ui.add_space(8.0);
            ui.label(
                egui::RichText::new("Color")
                    .size(11.0)
                    .color(widgets::TM),
            );
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                let _ = widgets::icon_btn(ui, "⋮");
            });
        });
        ui.add_space(2.0);

        let picker = widgets::hsv_picker(ui, &mut self.color_h, &mut self.color_s, &mut self.color_v);
        if picker.dragged() || picker.clicked() {
            self.set_brush_from_hsv();
        }

        ui.horizontal(|ui| {
            ui.add_space(8.0);
            if widgets::hex_input(ui, &mut self.hex_buf, egui::Id::new("hex")).lost_focus() {
                self.try_apply_hex();
            }
        });
        ui.add_space(6.0);

        ui.horizontal(|ui| {
            ui.add_space(8.0);
            widgets::swatch_grid(ui, 10, &mut |c| {
                self.set_brush_rgb(c.r(), c.g(), c.b());
            });
        });
    }

    fn properties_panel(&mut self, ui: &mut Ui) {
        ui.label(
            egui::RichText::new("Brush")
                .size(11.0)
                .color(widgets::TM),
        );
        ui.add_space(4.0);
        widgets::brush_size_palette(ui, self.brush_size, &mut |s| self.brush_size = s);
        ui.add_space(8.0);
        widgets::sep(ui);
        ui.add_space(4.0);
        ui.label(
            egui::RichText::new("Flow")
                .size(10.0)
                .color(widgets::TM),
        );
        let _ = widgets::slider(ui, &mut self.brush_flow, 0.0..=1.0);
        ui.label(
            egui::RichText::new("Opacity")
                .size(10.0)
                .color(widgets::TM),
        );
        let _ = widgets::slider(ui, &mut self.brush_opacity, 0.0..=1.0);
    }

    fn canvas_ui(&mut self, ui: &mut Ui) {
        let avail = ui.available_size();
        if avail.x <= 0.0 || avail.y <= 0.0 {
            return;
        }
        let workspace = ui.available_rect_before_wrap();
        ui.painter().rect_filled(workspace, 0.0, widgets::B0);

        let Some(tex) = &self.composite_tex else {
            return;
        };
        let tsz = tex.size_vec2();
        if tsz.x <= 0.0 || tsz.y <= 0.0 {
            return;
        }

        let scale = (avail.x / tsz.x).min(avail.y / tsz.y) * self.canvas_zoom;
        let render = tsz * scale;
        let offset = ((avail - render) * 0.5 + self.canvas_pan).max(Vec2::ZERO);
        let cr = Rect::from_min_size(workspace.min + offset, render);
        let resp = ui.allocate_rect(cr, Sense::click_and_drag());

        if ui.is_rect_visible(cr) {
            let p = ui.painter();
            widgets::paint_checkerboard(p, cr);
            p.image(
                tex.id(),
                cr,
                Rect::from_min_max(Pos2::ZERO, egui::pos2(1.0, 1.0)),
                Color32::WHITE,
            );
            p.rect_stroke(cr, 0.0, egui::Stroke::new(1.0, widgets::STR), egui::StrokeKind::Middle);
        }

        if resp.hovered() {
            let s = ui.ctx().input(|i| i.smooth_scroll_delta.y);
            if s != 0.0 {
                self.canvas_zoom = (self.canvas_zoom * (1.0 + s * 0.001)).clamp(0.1, 64.0);
            }
        }
        if resp.dragged_by(egui::PointerButton::Middle) {
            self.canvas_pan += resp.drag_delta();
        }

        if resp.dragged_by(egui::PointerButton::Primary) {
            if let Some(pos) = ui.ctx().input(|i| i.pointer.hover_pos()) {
                let dx = (pos.x - cr.min.x) / scale;
                let dy = (pos.y - cr.min.y) / scale;
                if dx >= 0.0
                    && dy >= 0.0
                    && (dx as i32) < self.doc.width()
                    && (dy as i32) < self.doc.height()
                {
                    let p = f32::from_bits(self.pressure.load(Ordering::Relaxed)) as f64;
                    if let Some((lx, ly)) = self.last_pos {
                        if (dx as f64 - lx).powi(2) + (dy as f64 - ly).powi(2) < 1.0 {
                            return;
                        }
                    }
                    self.last_pos = Some((dx as f64, dy as f64));
                    let r = (self.brush_size * p.max(0.05)).ceil() as i32;
                    stamp(
                        &mut self.doc,
                        self.brush_color,
                        self.brush_size,
                        dx as f64,
                        dy as f64,
                        p,
                    );
                    self.compositor.invalidate(Some(DocRect::new(
                        dx.round() as i32 - r,
                        dy.round() as i32 - r,
                        r * 2,
                        r * 2,
                    )));
                    self.needs_upload = true;
                    ui.ctx().request_repaint();
                }
            }
        } else {
            self.last_pos = None;
        }
    }
}

fn stamp(doc: &mut DrawingDocument, color: DocColor, size: f64, cx: f64, cy: f64, pressure: f64) {
    let r = (size * pressure.max(0.05)).ceil() as i32;
    let sr = (color.r() * 255.0) as u32;
    let sg = (color.g() * 255.0) as u32;
    let sb = (color.b() * 255.0) as u32;
    for dy in -r..=r {
        for dx in -r..=r {
            let dist = ((dx * dx + dy * dy) as f64).sqrt();
            if dist > r as f64 {
                continue;
            }
            let a = ((r as f64 - dist + 0.5).clamp(0.0, 1.0) * 255.0 * pressure).round() as u8;
            if a == 0 {
                continue;
            }
            let x = dx + cx.round() as i32;
            let y = dy + cy.round() as i32;
            let e = doc.active_layer_mut().pixels.get_pixel(x, y);
            let da = e[3] as u32;
            let sa = a as u32;
            let oa = sa + da * (255 - sa) / 255;
            if oa == 0 {
                continue;
            }
            doc.active_layer_mut().pixels.set_pixel(
                x,
                y,
                ((sb * sa + e[0] as u32 * da * (255 - sa) / 255) / oa) as u8,
                ((sg * sa + e[1] as u32 * da * (255 - sa) / 255) / oa) as u8,
                ((sr * sa + e[2] as u32 * da * (255 - sa) / 255) / oa) as u8,
                oa as u8,
            );
        }
    }
}
