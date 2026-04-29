use crate::brush::BrushPreset;
use crate::layer::Layer;
use crate::stroke::StrokeSample;
use crate::tile::TileCoord;

const PAPER_RGBA: [u8; 4] = [0xf7, 0xf4, 0xed, 0xff];

#[derive(Clone, Debug, PartialEq)]
pub struct FrameDelta {
    pub dirty_tiles: Vec<TileCoord>,
    pub active_render_sample_count: u32,
    pub committed_stroke_count: u32,
}

#[derive(Clone, Debug, PartialEq)]
pub struct EngineStats {
    pub width: u32,
    pub height: u32,
    pub tile_size: u32,
    pub tile_columns: u32,
    pub tile_rows: u32,
    pub dirty_tile_count: u32,
    pub committed_stroke_count: u32,
    pub active_raw_sample_count: u32,
    pub layer_count: u32,
}

#[derive(Clone, Debug)]
pub struct DrawingDocument {
    width: u32,
    height: u32,
    tile_size: u32,
    brush: BrushPreset,
    layers: Vec<Layer>,
    active_layer_id: u64,
    composite_buffer: Vec<u8>,
    next_layer_id: u64,
}

impl DrawingDocument {
    pub fn new(width: u32, height: u32, tile_size: u32) -> Self {
        let mut doc = Self {
            width,
            height,
            tile_size: tile_size.max(1),
            brush: BrushPreset::default(),
            layers: Vec::new(),
            active_layer_id: 1,
            composite_buffer: vec![0; width as usize * height as usize * 4],
            next_layer_id: 2,
        };
        doc.layers.push(Layer::new(1, "Paint 1", width, height, tile_size));
        doc.clear_composite_buffer();
        doc
    }

    pub fn set_brush(&mut self, brush: BrushPreset) {
        self.brush = brush;
    }

    pub fn begin_stroke(&mut self, sample: StrokeSample) -> FrameDelta {
        let brush = self.brush.clone();
        let dirty = self.active_layer_mut().begin_stroke(&brush, sample);
        self.composite_dirty_tiles(&dirty);
        self.build_frame_delta(dirty)
    }

    pub fn append_stroke_samples(&mut self, samples: &[StrokeSample]) -> FrameDelta {
        let brush = self.brush.clone();
        let dirty = self.active_layer_mut().append_stroke_samples(&brush, samples);
        self.composite_dirty_tiles(&dirty);
        self.build_frame_delta(dirty)
    }

    pub fn end_stroke(&mut self, sample: StrokeSample) -> FrameDelta {
        let brush = self.brush.clone();
        let dirty = self.active_layer_mut().end_stroke(&brush, sample);
        self.composite_dirty_tiles(&dirty);
        self.build_frame_delta(dirty)
    }

    pub fn cancel_stroke(&mut self) -> FrameDelta {
        let dirty = self.active_layer_mut().cancel_stroke();
        self.composite_dirty_tiles(&dirty);
        self.build_frame_delta(dirty)
    }

    pub fn clear(&mut self) -> FrameDelta {
        let mut all_dirty = Vec::new();
        for layer in &mut self.layers {
            let dirty = layer.clear();
            all_dirty.extend(dirty);
        }
        self.composite_dirty_tiles(&all_dirty);
        self.build_frame_delta(all_dirty)
    }

    pub fn snapshot_rgba(&self) -> Vec<u8> {
        self.composite_buffer.clone()
    }

    pub fn drain_frame_delta(&mut self) -> FrameDelta {
        FrameDelta {
            dirty_tiles: Vec::new(),
            active_render_sample_count: self
                .active_layer()
                .active_stroke
                .as_ref()
                .map(|stroke| stroke.render_samples.len() as u32)
                .unwrap_or(0),
            committed_stroke_count: self.active_layer().committed_strokes.len() as u32,
        }
    }

    pub fn stats(&self) -> EngineStats {
        EngineStats {
            width: self.width,
            height: self.height,
            tile_size: self.tile_size,
            tile_columns: self.width.div_ceil(self.tile_size),
            tile_rows: self.height.div_ceil(self.tile_size),
            dirty_tile_count: 0,
            committed_stroke_count: self.active_layer().committed_strokes.len() as u32,
            active_raw_sample_count: self.active_layer().active_raw_sample_count() as u32,
            layer_count: self.layers.len() as u32,
        }
    }

    // Layer management
    pub fn add_layer(&mut self) -> u64 {
        let id = self.next_layer_id;
        self.next_layer_id += 1;
        self.layers.push(Layer::new(id, &format!("Paint {}", id), self.width, self.height, self.tile_size));
        id
    }

    pub fn delete_layer(&mut self, id: u64) {
        if self.layers.len() <= 1 {
            return; // Keep at least one layer
        }
        self.layers.retain(|l| l.id != id);
        if self.active_layer_id == id {
            self.active_layer_id = self.layers.first().map(|l| l.id).unwrap_or(1);
        }
        self.recomposite_all();
    }

    pub fn move_layer(&mut self, id: u64, new_index: usize) {
        let current_index = self.layers.iter().position(|l| l.id == id);
        if let Some(current) = current_index {
            let clamped = new_index.clamp(0, self.layers.len().saturating_sub(1));
            if current != clamped {
                let layer = self.layers.remove(current);
                self.layers.insert(clamped, layer);
                self.recomposite_all();
            }
        }
    }

    pub fn set_layer_visibility(&mut self, id: u64, visible: bool) {
        if let Some(layer) = self.layers.iter_mut().find(|l| l.id == id) {
            layer.visible = visible;
            self.recomposite_all();
        }
    }

    pub fn set_layer_opacity(&mut self, id: u64, opacity: f32) {
        if let Some(layer) = self.layers.iter_mut().find(|l| l.id == id) {
            layer.opacity = opacity.clamp(0.0, 1.0);
            self.recomposite_all();
        }
    }

    pub fn get_active_layer_id(&self) -> u64 {
        self.active_layer_id
    }

    pub fn set_active_layer(&mut self, id: u64) {
        if self.layers.iter().any(|l| l.id == id) {
            self.active_layer_id = id;
        }
    }

    pub fn rename_layer(&mut self, id: u64, name: impl Into<String>) {
        if let Some(layer) = self.layers.iter_mut().find(|l| l.id == id) {
            layer.name = name.into();
        }
    }

    pub fn layer_ids(&self) -> Vec<u64> {
        self.layers.iter().map(|l| l.id).collect()
    }

    pub fn get_layer(&self, id: u64) -> Option<&Layer> {
        self.layers.iter().find(|l| l.id == id)
    }

    fn active_layer(&self) -> &Layer {
        self.layers
            .iter()
            .find(|l| l.id == self.active_layer_id)
            .expect("active layer must exist")
    }

    fn active_layer_mut(&mut self) -> &mut Layer {
        self.layers
            .iter_mut()
            .find(|l| l.id == self.active_layer_id)
            .expect("active layer must exist")
    }

    fn clear_composite_buffer(&mut self) {
        for pixel in self.composite_buffer.chunks_exact_mut(4) {
            pixel.copy_from_slice(&PAPER_RGBA);
        }
    }

    fn recomposite_all(&mut self) {
        self.clear_composite_buffer();
        let cols = self.width.div_ceil(self.tile_size);
        let rows = self.height.div_ceil(self.tile_size);
        for y in 0..rows {
            for x in 0..cols {
                let coord = TileCoord { x, y };
                self.composite_tile(coord);
            }
        }
    }

    fn composite_dirty_tiles(&mut self, dirty: &[TileCoord]) {
        for coord in dirty {
            self.composite_tile(*coord);
        }
    }

    fn composite_tile(&mut self, coord: TileCoord) {
        let tile_w = self.tile_size.min(self.width - coord.x * self.tile_size);
        let tile_h = self.tile_size.min(self.height - coord.y * self.tile_size);
        if tile_w == 0 || tile_h == 0 {
            return;
        }

        let global_x = coord.x * self.tile_size;
        let global_y = coord.y * self.tile_size;

        for local_y in 0..tile_h {
            for local_x in 0..tile_w {
                let gx = global_x + local_x;
                let gy = global_y + local_y;
                let buf_idx = ((gy * self.width + gx) * 4) as usize;

                // Start with paper color (background)
                let mut r = PAPER_RGBA[0] as f32;
                let mut g = PAPER_RGBA[1] as f32;
                let mut b = PAPER_RGBA[2] as f32;
                let mut a = PAPER_RGBA[3] as f32 / 255.0;

                // Composite each visible layer from bottom to top
                for layer in &self.layers {
                    if !layer.visible {
                        continue;
                    }
                    let layer_a = layer.opacity;
                    if let Some(tile) = layer.snapshot_tile(coord) {
                        let tile_idx = ((local_y * tile_w + local_x) * 4) as usize;
                        let src_r = tile[tile_idx] as f32;
                        let src_g = tile[tile_idx + 1] as f32;
                        let src_b = tile[tile_idx + 2] as f32;
                        let src_a = tile[tile_idx + 3] as f32 / 255.0;

                        // Alpha-over blending ( respecting layer opacity )
                        let effective_a = src_a * layer_a;
                        let inv = 1.0 - effective_a;
                        r = src_r * effective_a + r * inv;
                        g = src_g * effective_a + g * inv;
                        b = src_b * effective_a + b * inv;
                        a = effective_a + a * inv;
                    }
                }

                self.composite_buffer[buf_idx] = r.clamp(0.0, 255.0) as u8;
                self.composite_buffer[buf_idx + 1] = g.clamp(0.0, 255.0) as u8;
                self.composite_buffer[buf_idx + 2] = b.clamp(0.0, 255.0) as u8;
                self.composite_buffer[buf_idx + 3] = (a * 255.0).clamp(0.0, 255.0) as u8;
            }
        }
    }

    fn build_frame_delta(&self, dirty_tiles: Vec<TileCoord>) -> FrameDelta {
        FrameDelta {
            dirty_tiles,
            active_render_sample_count: self
                .active_layer()
                .active_stroke
                .as_ref()
                .map(|stroke| stroke.render_samples.len() as u32)
                .unwrap_or(0),
            committed_stroke_count: self.active_layer().committed_strokes.len() as u32,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tracks_dirty_tiles_for_stroke() {
        let mut document = DrawingDocument::new(1024, 1024, 256);
        document.begin_stroke(sample(40.0, 40.0));
        let delta = document.append_stroke_samples(&[
            sample(180.0, 45.0),
            sample(260.0, 80.0),
            sample(340.0, 120.0),
        ]);

        assert!(!delta.dirty_tiles.is_empty());
        assert!(delta.active_render_sample_count > 0);
    }

    #[test]
    fn layer_management() {
        let mut doc = DrawingDocument::new(1024, 1024, 256);
        assert_eq!(doc.layer_ids().len(), 1);

        let layer2 = doc.add_layer();
        assert_eq!(doc.layer_ids().len(), 2);

        doc.set_active_layer(layer2);
        doc.begin_stroke(sample(100.0, 100.0));
        doc.end_stroke(sample(110.0, 110.0));

        doc.delete_layer(layer2);
        assert_eq!(doc.layer_ids().len(), 1);
    }

    fn sample(x: f32, y: f32) -> StrokeSample {
        StrokeSample {
            x,
            y,
            pressure: 1.0,
            velocity: 0.0,
            time_micros: 0,
            pointer: 1,
        }
    }
}
