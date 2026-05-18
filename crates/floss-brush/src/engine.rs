//! Brush engine — stamp placement, mask generation, and pixel compositing.
//!
//! Ported from `Floss.App.Brushes.BrushEngine.cs`.
//!
//! Manages active strokes, builds stamp samples from input segments
//! using Catmull-Rom interpolation, evaluates dynamics, and composites
//! stamps onto layer tiles.

use std::collections::HashMap;

use floss_core::{Color, Rect};
use floss_document::DrawingLayer;

use crate::preset::BrushPreset;
use crate::stroke::{StrokePoint, StrokeState};
use crate::tip::StampMask;
use crate::types::AngleSource;

// ── Constants (match C# BrushEngine) ──────────────────────────────────────

const _MIN_STRETCH_CARRY: f32 = 0.02;
const _MAX_STRETCH_CARRY: f32 = 0.88;

/// A single stamp placement ready for rendering.
#[derive(Debug, Clone)]
pub struct StampSample {
    pub x: f32,
    pub y: f32,
    pub size: f32,
    pub opacity: f32,
    pub angle: f32, // degrees
    pub hardness: f32,
    pub spacing_multiplier: f32,
    pub tip_thickness_multiplier: f32,
}

/// Active stroke state tracked by the engine.
struct ActiveStroke {
    state: StrokeState,
    base_color: Color,
    base_mask_size: i32,
    stroke_random: f32,
    brush_name_hash: u64,
}

impl ActiveStroke {
    fn matches(&self, brush: &BrushPreset) -> bool {
        let hash = brush_name_hash(&brush.name);
        self.brush_name_hash == hash
    }
}

fn brush_name_hash(name: &str) -> u64 {
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    name.hash(&mut h);
    h.finish()
}

// ── BrushEngine ──────────────────────────────────────────────────────────

pub struct BrushEngine {
    active_stroke: Option<ActiveStroke>,
    stamps: Vec<StampSample>,
    /// Cache of generated masks per (size, hardness) for the active tip.
    mask_cache: HashMap<(i32, i32), StampMask>,
}

impl BrushEngine {
    pub fn new() -> Self {
        Self {
            active_stroke: None,
            stamps: Vec::new(),
            mask_cache: HashMap::new(),
        }
    }

    // ── Stroke lifecycle ──────────────────────────────────────────────────

    /// Start a new brush stroke. Ends any previous stroke.
    pub fn begin_stroke(&mut self, brush: &BrushPreset, x: f32, y: f32, pressure: f32, tilt_x: f32, tilt_y: f32) {
        self.end_stroke();
        self.active_stroke = Some(ActiveStroke {
            state: StrokeState::new(x, y, pressure, tilt_x, tilt_y),
            base_color: brush.color,
            base_mask_size: brush.base_mask_size,
            stroke_random: pseudo_random_01(x.to_bits(), y.to_bits()),
            brush_name_hash: brush_name_hash(&brush.name),
        });
    }

    /// End the current stroke.
    pub fn end_stroke(&mut self) {
        self.active_stroke = None;
        self.stamps.clear();
    }

    fn ensure_stroke(&mut self, brush: &BrushPreset, x: f32, y: f32, pressure: f32, tilt_x: f32, tilt_y: f32) {
        if self.active_stroke.is_none() || !self.active_stroke.as_ref().unwrap().matches(brush) {
            self.begin_stroke(brush, x, y, pressure, tilt_x, tilt_y);
        }
    }

    // ── Segment rasterization ─────────────────────────────────────────────

    /// Estimate the pixel region affected by a segment (for undo recording).
    pub fn estimate_segment_region(
        &self,
        brush: &BrushPreset,
        from_x: f64,
        from_y: f64,
        to_x: f64,
        to_y: f64,
    ) -> Rect {
        let radius = estimate_brush_radius(brush);
        let min_x = (from_x.min(to_x) - radius) as i32;
        let min_y = (from_y.min(to_y) - radius) as i32;
        let max_x = (from_x.max(to_x) + radius).ceil() as i32;
        let max_y = (from_y.max(to_y) + radius).ceil() as i32;
        Rect::new(min_x, min_y, (max_x - min_x + 1).max(1), (max_y - min_y + 1).max(1))
    }

    /// Rasterize a segment of the stroke onto the layer.
    ///
    /// Returns the dirty region that was modified (for invalidation and undo).
    pub fn rasterize_segment(
        &mut self,
        layer: &mut DrawingLayer,
        brush: &BrushPreset,
        from_x: f64,
        from_y: f64,
        from_pressure: f64,
        to_x: f64,
        to_y: f64,
        to_pressure: f64,
        from_tilt_x: f32,
        from_tilt_y: f32,
        to_tilt_x: f32,
        to_tilt_y: f32,
        _time_delta_secs: f64,
        _sample_source: Option<&dyn Fn(i32, i32) -> [u8; 4]>,
    ) -> Rect {
        self.ensure_stroke(brush, to_x as f32, to_y as f32, to_pressure as f32, to_tilt_x, to_tilt_y);

        self.stamps.clear();

        let dirty = self.build_stamps(
            brush,
            from_x as f32, from_y as f32, from_pressure as f32, from_tilt_x, from_tilt_y,
            to_x as f32, to_y as f32, to_pressure as f32, to_tilt_x, to_tilt_y,
            false,
        );

        if dirty.is_empty() || self.stamps.is_empty() {
            return Rect::ZERO;
        }

        // Composite stamps directly onto the layer tiles
        self.render_stamps_direct(layer, brush);

        dirty
    }

    /// Rasterize the final segment (ensures endpoint coverage).
    pub fn rasterize_final_segment(
        &mut self,
        layer: &mut DrawingLayer,
        brush: &BrushPreset,
        from_x: f64,
        from_y: f64,
        from_pressure: f64,
        to_x: f64,
        to_y: f64,
        to_pressure: f64,
        from_tilt_x: f32,
        from_tilt_y: f32,
        to_tilt_x: f32,
        to_tilt_y: f32,
        _time_delta_secs: f64,
        _sample_source: Option<&dyn Fn(i32, i32) -> [u8; 4]>,
    ) -> Rect {
        self.ensure_stroke(brush, to_x as f32, to_y as f32, to_pressure as f32, to_tilt_x, to_tilt_y);

        self.stamps.clear();

        let dirty = self.build_stamps(
            brush,
            from_x as f32, from_y as f32, from_pressure as f32, from_tilt_x, from_tilt_y,
            to_x as f32, to_y as f32, to_pressure as f32, to_tilt_x, to_tilt_y,
            true,
        );

        if dirty.is_empty() || self.stamps.is_empty() {
            return Rect::ZERO;
        }

        self.render_stamps_direct(layer, brush);

        dirty
    }

    // ── Stamp building ────────────────────────────────────────────────────

    fn build_stamps(
        &mut self,
        brush: &BrushPreset,
        from_x: f32, from_y: f32, from_pressure: f32, from_tilt_x: f32, from_tilt_y: f32,
        to_x: f32, to_y: f32, to_pressure: f32, to_tilt_x: f32, to_tilt_y: f32,
        ensure_endpoint: bool,
    ) -> Rect {
        if from_pressure <= 0.0 && to_pressure <= 0.0 {
            return Rect::ZERO;
        }

        let stroke = self.active_stroke.as_mut().unwrap();
        let dx = to_x - from_x;
        let dy = to_y - from_y;
        let distance = (dx * dx + dy * dy).sqrt();

        let elapsed_secs = 0.001f32.max(0.001);
        let velocity01 = (distance / elapsed_secs / 5000.0).clamp(0.0, 1.0);

        if distance > 0.001 {
            let current_angle = dy.atan2(dx);
            stroke.state.drawing_angle = lerp_angle(stroke.state.drawing_angle, current_angle, 0.5);
        }

        let stamp_spacing = stroke.state.next_stamp_distance.max(1.0);
        let estimated_stamps = distance / stamp_spacing;
        let subdivisions = (8usize).max((estimated_stamps * 4.0).ceil() as usize).min(96);

        let mut dirty = Rect::ZERO;
        let mut prev_x = from_x;
        let mut prev_y = from_y;

        for i in 1..=subdivisions {
            let t = i as f32 / subdivisions as f32;
            let cur_x = from_x + dx * t;
            let cur_y = from_y + dy * t;
            let seg_dx = cur_x - prev_x;
            let seg_dy = cur_y - prev_y;
            let seg_len = (seg_dx * seg_dx + seg_dy * seg_dy).sqrt();

            if seg_len > 0.0001 {
                let mut consumed = stroke.state.next_stamp_distance - stroke.state.distance_leftover;
                while consumed <= seg_len {
                    let ratio = consumed / seg_len;
                    let sx = prev_x + seg_dx * ratio;
                    let sy = prev_y + seg_dy * ratio;
                    // Lerp pressure and tilt
                    let sp = lerp_pressure(sx, sy, from_pressure, to_pressure, from_tilt_x, from_tilt_y, to_tilt_x, to_tilt_y, t);
                    let sp_point = build_stroke_point(stroke, sx, sy, sp.0, sp.1, sp.2, velocity01);
                    let stamp = create_stamp(brush, &sp_point);
                    let bounds = stamp_bounds(&stamp);
                    dirty = dirty.union(bounds);
                    self.stamps.push(stamp);
                    stroke.state.total_distance += stroke.state.next_stamp_distance;
                    stroke.state.dab_seq_no += 1;
                    stroke.state.next_stamp_distance = stamp_spacing_hz(brush, &self.stamps.last().unwrap());
                    consumed += stroke.state.next_stamp_distance;
                }
                stroke.state.distance_leftover = seg_len - (consumed - stroke.state.next_stamp_distance);
                if stroke.state.distance_leftover >= stroke.state.next_stamp_distance {
                    stroke.state.distance_leftover = 0.0;
                }
            }

            prev_x = cur_x;
            prev_y = cur_y;
        }

        if ensure_endpoint && !self.stamps.is_empty() {
            let last = self.stamps.last().unwrap();
            dirty = dirty.inflate((last.size * 0.25) as i32 + 1);
        }

        stroke.state.last_x = to_x;
        stroke.state.last_y = to_y;
        stroke.state.last_pressure = to_pressure;
        stroke.state.last_tilt_x = to_tilt_x;
        stroke.state.last_tilt_y = to_tilt_y;

        dirty
    }

    // ── Direct stamp rendering (software) ──────────────────────────────────

    fn render_stamps_direct(&mut self, layer: &mut DrawingLayer, brush: &BrushPreset) {
        for stamp in &self.stamps {
            if stamp.opacity <= 0.0 || stamp.size <= 0.0 {
                continue;
            }

            // Use engine's mask cache to avoid regenerating identical masks
            let cache_key = (brush.base_mask_size, (stamp.hardness * 255.0) as i32);
            if !self.mask_cache.contains_key(&cache_key) {
                let mask = brush.tip.generate_mask(brush.base_mask_size, stamp.hardness);
                self.mask_cache.insert(cache_key, mask);
            }
            let mask = self.mask_cache.get(&cache_key).unwrap();
            let scale = stamp.size / brush.base_mask_size as f32;

            // Bounding box in document pixels
            let mw = mask.width as f32 * scale;
            let mh = mask.height as f32 * scale;
            let half_w = (mw * 0.5).ceil() as i32;
            let half_h = (mh * 0.5).ceil() as i32;

            let cx = stamp.x.round() as i32;
            let cy = stamp.y.round() as i32;

            for dy in -half_h..=half_h {
                let doc_y = cy + dy;
                let src_y = ((dy as f32 + mh * 0.5) / scale) as usize;
                if src_y >= mask.height {
                    continue;
                }

                for dx in -half_w..=half_w {
                    let doc_x = cx + dx;
                    let src_x = ((dx as f32 + mw * 0.5) / scale) as usize;
                    if src_x >= mask.width {
                        continue;
                    }

                    let mask_a = mask.data[src_y * mask.width + src_x] as f32 / 255.0;
                    if mask_a <= 0.0 {
                        continue;
                    }

                    let stamp_a = (mask_a * stamp.opacity * 255.0).round() as u8;
                    if stamp_a == 0 {
                        continue;
                    }

                    // Read existing pixel
                    let existing = layer.pixels.get_pixel(doc_x, doc_y);
                    // Composite brush color over existing with stamp alpha
                    let brush_b = (brush.color.r() * 255.0) as u8;
                    let brush_g = (brush.color.g() * 255.0) as u8;
                    let brush_r = (brush.color.b() * 255.0) as u8;

                    let dst_b = existing[0];
                    let dst_g = existing[1];
                    let dst_r = existing[2];
                    let dst_a = existing[3] as u32;

                    let src_a = stamp_a as u32;
                    let out_a = src_a + dst_a * (255 - src_a) / 255;
                    if out_a == 0 {
                        layer.pixels.set_pixel(doc_x, doc_y, 0, 0, 0, 0);
                        continue;
                    }

                    let out_b = ((brush_b as u32 * src_a + dst_b as u32 * dst_a * (255 - src_a) / 255) / out_a) as u8;
                    let out_g = ((brush_g as u32 * src_a + dst_g as u32 * dst_a * (255 - src_a) / 255) / out_a) as u8;
                    let out_r = ((brush_r as u32 * src_a + dst_r as u32 * dst_a * (255 - src_a) / 255) / out_a) as u8;

                    layer.pixels.set_pixel(doc_x, doc_y, out_b, out_g, out_r, out_a as u8);
                }
            }
        }
    }
}

// ── Helpers ──────────────────────────────────────────────────────────────

fn estimate_brush_radius(brush: &BrushPreset) -> f64 {
    // Conservative estimate: max possible size with max dynamics
    let max_size = brush.size * (brush.dynamics.size.max_output as f64).max(1.0);
    let spacing = max_size * brush.spacing.max(0.01);
    let scatter = if brush.dynamics.scatter.enabled {
        max_size * 1.0 // Assume max scatter could be up to brush size
    } else {
        0.0
    };
    (max_size * 0.75 + spacing + scatter + 3.0).max(1.0)
}

fn stamp_bounds(stamp: &StampSample) -> Rect {
    let r = (stamp.size * 0.75).ceil() as i32 + 1;
    let x = stamp.x.round() as i32;
    let y = stamp.y.round() as i32;
    Rect::new(x - r, y - r, r * 2 + 1, r * 2 + 1)
}

fn stamp_spacing_hz(brush: &BrushPreset, stamp: &StampSample) -> f32 {
    let flow = (brush.flow as f32).clamp(0.01, 1.0);
    let spacing = (brush.spacing as f32 * stamp.spacing_multiplier).clamp(0.005, 4.0);
    (stamp.size * spacing * flow.sqrt()).max(0.5)
}

#[allow(clippy::too_many_arguments)]
fn build_stroke_point(
    stroke: &ActiveStroke,
    x: f32, y: f32, pressure: f32, tilt_x: f32, tilt_y: f32,
    velocity01: f32,
) -> StrokePoint {
    StrokePoint::new(
        x, y, pressure, tilt_x, tilt_y, 0.0, // twist
        stroke.state.drawing_angle,
        velocity01,
        stroke.state.total_distance,
        stroke.state.dab_seq_no,
        pseudo_random_01(x.to_bits() as u32, y.to_bits() as u32),
        stroke.stroke_random,
    )
}

fn create_stamp(brush: &BrushPreset, sp: &StrokePoint) -> StampSample {
    let d = &brush.dynamics;
    let size_mul = d.eval_size(sp);
    let opac_mul = d.eval_opacity(sp);
    let flow_mul = d.eval_flow(sp);
    let hardness = if d.hardness.enabled {
        d.eval_hardness(sp)
    } else {
        brush.hardness as f32
    };
    let spacing_mul = d.eval_spacing(sp);
    let tip_density_mul = if d.tip_density.enabled { d.eval_tip_density(sp) } else { 1.0 };
    let tip_thickness_mul = if d.tip_thickness.enabled { d.eval_tip_thickness(sp) } else { 1.0 };
    let scatter = if d.scatter.enabled { d.eval_scatter(sp) } else { 0.0 };
    let rot_deg = d.eval_rotation_deg(sp);

    let size = (brush.size as f32 * size_mul).max(0.5);
    let opacity = ((brush.opacity * brush.flow * brush.tip_density) as f32
        * tip_density_mul * opac_mul * flow_mul)
        .clamp(0.0, 1.0);

    let direction_contrib = match brush.base_angle_source {
        AngleSource::DirectionOfLine => sp.drawing_angle * (180.0 / std::f32::consts::PI),
        AngleSource::PenTilt => sp.tilt_y.atan2(sp.tilt_x) * (180.0 / std::f32::consts::PI),
        AngleSource::PenTwist => sp.twist * (180.0 / std::f32::consts::PI),
        AngleSource::None => 0.0,
    };

    let jitter = if brush.angle_jitter > 0.001 {
        (sp.random * 2.0 - 1.0) * brush.angle_jitter as f32 * 180.0
    } else {
        0.0
    };

    let angle = brush.angle as f32 + direction_contrib + rot_deg + jitter;

    let (mut sx, mut sy) = (sp.x, sp.y);
    if scatter > 0.001 {
        use std::f32::consts::TAU;
        let radians = sp.random * TAU;
        let amount = (pseudo_random_01(sp.dab_seq_no as u32, (sp.stroke_random * 100_000.0) as u32) * 2.0 - 1.0)
            * scatter * size;
        sx += radians.cos() * amount;
        sy += radians.sin() * amount;
    }

    StampSample {
        x: sx,
        y: sy,
        size,
        opacity,
        angle,
        hardness: hardness.clamp(0.001, 1.0),
        spacing_multiplier: spacing_mul.clamp(0.05, 4.0),
        tip_thickness_multiplier: tip_thickness_mul.clamp(0.01, 4.0),
    }
}

#[inline]
fn lerp_angle(a: f32, b: f32, t: f32) -> f32 {
    use std::f32::consts::TAU;
    let mut delta = b - a;
    if delta > std::f32::consts::PI {
        delta -= TAU;
    } else if delta < -std::f32::consts::PI {
        delta += TAU;
    }
    a + delta * t
}

#[inline]
fn lerp_pressure(
    _x: f32, _y: f32,
    from_p: f32, to_p: f32,
    from_tx: f32, from_ty: f32,
    to_tx: f32, to_ty: f32,
    t: f32,
) -> (f32, f32, f32) {
    let p = from_p + (to_p - from_p) * t;
    let tx = from_tx + (to_tx - from_tx) * t;
    let ty = from_ty + (to_ty - from_ty) * t;
    (p, tx, ty)
}

#[inline]
fn pseudo_random_01(a: u32, b: u32) -> f32 {
    let h = a.wrapping_mul(374761393).wrapping_add(b.wrapping_mul(668265263));
    let h = h.wrapping_mul(1274126177) ^ h;
    h as f32 / u32::MAX as f32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stamp_bounds_positive() {
        let s = StampSample {
            x: 50.0, y: 60.0, size: 20.0, opacity: 1.0,
            angle: 0.0, hardness: 0.8,
            spacing_multiplier: 1.0, tip_thickness_multiplier: 1.0,
        };
        let b = stamp_bounds(&s);
        assert!(b.w > 0 && b.h > 0);
        assert!(b.contains_point(50, 60));
    }

    #[test]
    fn lerp_angle_crosses_pi() {
        let a = std::f32::consts::PI * 1.8;
        let b = -std::f32::consts::PI * 1.8;
        let result = lerp_angle(a, b, 0.5);
        // Should take the short path, not the long way around
        assert!(result.abs() > 0.0);
    }
}
