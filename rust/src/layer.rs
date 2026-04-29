use std::collections::HashMap;

use crate::brush::{BrushBlendMode, BrushPreset};
use crate::geometry::CanvasRect;
use crate::stroke::{Stroke, StrokeSample};
use crate::tile::TileCoord;

const PAPER_RGBA: [u8; 4] = [0xf7, 0xf4, 0xed, 0xff];
const TRANSPARENT_RGBA: [u8; 4] = [0x00, 0x00, 0x00, 0x00];

#[derive(Clone, Debug, PartialEq)]
pub struct Layer {
    pub id: u64,
    pub name: String,
    pub visible: bool,
    pub opacity: f32,
    pub blend_mode: BrushBlendMode,
    width: u32,
    height: u32,
    tile_size: u32,
    tiles: HashMap<TileCoord, Vec<u8>>,
    pub committed_strokes: Vec<Stroke>,
    active_raw_samples: Vec<StrokeSample>,
    pub active_stroke: Option<Stroke>,
    active_rendered_sample_count: usize,
    next_stroke_id: u64,
    dirty_tiles: Vec<TileCoord>,
}

impl Layer {
    pub fn active_raw_sample_count(&self) -> usize {
        self.active_raw_samples.len()
    }
    pub fn new(id: u64, name: impl Into<String>, width: u32, height: u32, tile_size: u32) -> Self {
        Self {
            id,
            name: name.into(),
            visible: true,
            opacity: 1.0,
            blend_mode: BrushBlendMode::Normal,
            width,
            height,
            tile_size: tile_size.max(1),
            tiles: HashMap::new(),
            committed_strokes: Vec::new(),
            active_raw_samples: Vec::with_capacity(512),
            active_stroke: None,
            active_rendered_sample_count: 0,
            next_stroke_id: 1,
            dirty_tiles: Vec::new(),
        }
    }

    pub fn begin_stroke(&mut self, brush: &BrushPreset, sample: StrokeSample) -> Vec<TileCoord> {
        self.active_raw_samples.clear();
        self.active_raw_samples.push(sample);
        self.active_rendered_sample_count = 0;
        self.rebuild_active_stroke(brush);
        std::mem::take(&mut self.dirty_tiles)
    }

    pub fn append_stroke_samples(
        &mut self,
        brush: &BrushPreset,
        samples: &[StrokeSample],
    ) -> Vec<TileCoord> {
        self.active_raw_samples.extend_from_slice(samples);
        self.rebuild_active_stroke(brush);
        std::mem::take(&mut self.dirty_tiles)
    }

    pub fn end_stroke(
        &mut self,
        brush: &BrushPreset,
        sample: StrokeSample,
    ) -> Vec<TileCoord> {
        self.active_raw_samples.push(sample);
        self.rebuild_active_stroke(brush);
        if let Some(stroke) = self.active_stroke.take() {
            self.committed_strokes.push(stroke);
            self.next_stroke_id += 1;
        }
        self.active_raw_samples.clear();
        self.active_rendered_sample_count = 0;
        std::mem::take(&mut self.dirty_tiles)
    }

    pub fn cancel_stroke(&mut self) -> Vec<TileCoord> {
        self.active_raw_samples.clear();
        self.active_stroke = None;
        self.active_rendered_sample_count = 0;
        std::mem::take(&mut self.dirty_tiles)
    }

    pub fn clear(&mut self) -> Vec<TileCoord> {
        self.committed_strokes.clear();
        self.active_raw_samples.clear();
        self.active_stroke = None;
        self.active_rendered_sample_count = 0;
        self.tiles.clear();
        self.mark_all_dirty();
        std::mem::take(&mut self.dirty_tiles)
    }

    pub fn snapshot_tile(&self, coord: TileCoord) -> Option<&[u8]> {
        self.tiles.get(&coord).map(|v| v.as_slice())
    }

    pub fn has_tile(&self, coord: TileCoord) -> bool {
        self.tiles.contains_key(&coord)
    }

    fn mark_all_dirty(&mut self) {
        let cols = self.width.div_ceil(self.tile_size);
        let rows = self.height.div_ceil(self.tile_size);
        for y in 0..rows {
            for x in 0..cols {
                self.dirty_tiles.push(TileCoord { x, y });
            }
        }
    }

    fn rebuild_active_stroke(&mut self, brush: &BrushPreset) {
        let stroke = Stroke::new(self.next_stroke_id, brush.clone(), &self.active_raw_samples);
        self.mark_rect_dirty(stroke.bounds);
        self.rasterize_new_active_samples(&stroke, brush);
        self.active_stroke = Some(stroke);
    }

    fn rasterize_new_active_samples(&mut self, stroke: &Stroke, brush: &BrushPreset) {
        let start = self.active_rendered_sample_count.saturating_sub(1);
        for index in start..stroke.render_samples.len() {
            let previous = if index == 0 {
                stroke.render_samples[index]
            } else {
                stroke.render_samples[index - 1]
            };
            let current = stroke.render_samples[index];
            self.rasterize_segment(previous, current, brush);
        }
        self.active_rendered_sample_count = stroke.render_samples.len();
    }

    fn rasterize_segment(&mut self, from: StrokeSample, to: StrokeSample, brush: &BrushPreset) {
        let dx = to.x - from.x;
        let dy = to.y - from.y;
        let distance = (dx * dx + dy * dy).sqrt();
        let spacing = (brush.size * brush.spacing).max(1.0);
        let steps = ((distance / spacing).ceil() as usize).max(1);

        for step in 0..=steps {
            let t = step as f32 / steps as f32;
            let x = from.x + dx * t;
            let y = from.y + dy * t;
            let pressure = from.pressure + (to.pressure - from.pressure) * t;
            let velocity = from.velocity + (to.velocity - from.velocity) * t;
            self.rasterize_stamp(x, y, pressure, velocity, brush);
        }
    }

    fn rasterize_stamp(
        &mut self,
        center_x: f32,
        center_y: f32,
        pressure: f32,
        velocity: f32,
        brush: &BrushPreset,
    ) {
        let normalized_pressure = pressure.clamp(0.0, 1.0).powf(brush.pressure_curve_exponent);
        let normalized_velocity = (velocity / 5000.0).clamp(0.0, 1.0);
        let velocity_size_factor = (1.0
            - (normalized_velocity * brush.velocity_size_sensitivity).powi(2))
        .clamp(0.1, 1.0);
        let velocity_opacity_factor = (1.0
            - (normalized_velocity * brush.velocity_opacity_sensitivity).powi(2))
        .clamp(0.1, 1.0);

        let effective_size = brush.size * normalized_pressure * velocity_size_factor;
        let effective_opacity = brush.opacity * velocity_opacity_factor;
        let radius = (effective_size * 0.5).max(0.5);

        let min_x = (center_x - radius).floor().max(0.0) as u32;
        let min_y = (center_y - radius).floor().max(0.0) as u32;
        let max_x = (center_x + radius)
            .ceil()
            .min(self.width.saturating_sub(1) as f32) as u32;
        let max_y = (center_y + radius)
            .ceil()
            .min(self.height.saturating_sub(1) as f32) as u32;

        let color = argb_to_rgba(brush.color_argb);
        let base_alpha = (color[3] as f32 / 255.0) * effective_opacity.clamp(0.0, 1.0);

        for y in min_y..=max_y {
            for x in min_x..=max_x {
                let pixel_center_x = x as f32 + 0.5;
                let pixel_center_y = y as f32 + 0.5;
                let dist = ((pixel_center_x - center_x).powi(2)
                    + (pixel_center_y - center_y).powi(2))
                .sqrt();
                if dist > radius {
                    continue;
                }

                let falloff = 1.0 - (dist / radius);
                let hardness = brush.hardness.clamp(0.0, 1.0);
                let alpha = base_alpha * (hardness + (1.0 - hardness) * falloff);
                self.blend_pixel(x, y, color, alpha);
            }
        }
    }

    fn blend_pixel(&mut self, x: u32, y: u32, source: [u8; 4], alpha: f32) {
        let tile_x = x / self.tile_size;
        let tile_y = y / self.tile_size;
        let coord = TileCoord { x: tile_x, y: tile_y };

        let tile = self.tiles.entry(coord).or_insert_with(|| {
            let tile_w = self.tile_size.min(self.width - tile_x * self.tile_size);
            let tile_h = self.tile_size.min(self.height - tile_y * self.tile_size);
            vec![0; tile_w as usize * tile_h as usize * 4]
        });

        let tile_w = self.tile_size.min(self.width - tile_x * self.tile_size);
        let local_x = (x - tile_x * self.tile_size) as usize;
        let local_y = (y - tile_y * self.tile_size) as usize;
        let index = (local_y * tile_w as usize + local_x) * 4;

        let inv_alpha = 1.0 - alpha;
        tile[index] = ((source[0] as f32 * alpha) + (tile[index] as f32 * inv_alpha))
            .round()
            .clamp(0.0, 255.0) as u8;
        tile[index + 1] = ((source[1] as f32 * alpha) + (tile[index + 1] as f32 * inv_alpha))
            .round()
            .clamp(0.0, 255.0) as u8;
        tile[index + 2] = ((source[2] as f32 * alpha) + (tile[index + 2] as f32 * inv_alpha))
            .round()
            .clamp(0.0, 255.0) as u8;
        tile[index + 3] = ((source[3] as f32 * alpha) + (tile[index + 3] as f32 * inv_alpha))
            .round()
            .clamp(0.0, 255.0) as u8;
    }

    fn mark_rect_dirty(&mut self, rect: CanvasRect) {
        if rect.is_empty() {
            return;
        }
        let max_tile_x = self.width.div_ceil(self.tile_size).saturating_sub(1);
        let max_tile_y = self.height.div_ceil(self.tile_size).saturating_sub(1);
        let min_tile_x = floor_to_tile(rect.left, self.tile_size).clamp(0, max_tile_x);
        let min_tile_y = floor_to_tile(rect.top, self.tile_size).clamp(0, max_tile_y);
        let max_tile_x = floor_to_tile(rect.right, self.tile_size).clamp(0, max_tile_x);
        let max_tile_y = floor_to_tile(rect.bottom, self.tile_size).clamp(0, max_tile_y);

        for ty in min_tile_y..=max_tile_y {
            for tx in min_tile_x..=max_tile_x {
                self.dirty_tiles.push(TileCoord { x: tx, y: ty });
            }
        }
    }
}

fn floor_to_tile(value: f32, tile_size: u32) -> u32 {
    if value <= 0.0 {
        0
    } else {
        (value as u32) / tile_size
    }
}

fn argb_to_rgba(color: u32) -> [u8; 4] {
    [
        ((color >> 16) & 0xff) as u8,
        ((color >> 8) & 0xff) as u8,
        (color & 0xff) as u8,
        ((color >> 24) & 0xff) as u8,
    ]
}
