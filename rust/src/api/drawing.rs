use crate::brush::{BrushBlendMode, BrushPreset};
use crate::document::{DrawingDocument, EngineStats, FrameDelta};
use crate::geometry::{CanvasPoint, CanvasRect};
use crate::stroke::StrokeSample;
use flutter_rust_bridge::frb;

pub use crate::brush::BrushBlendMode as ApiBrushBlendMode;
pub use crate::brush::BrushPreset as ApiBrushPreset;
pub use crate::document::EngineStats as ApiEngineStats;
pub use crate::document::FrameDelta as ApiFrameDelta;
pub use crate::geometry::CanvasPoint as ApiCanvasPoint;
pub use crate::geometry::CanvasRect as ApiCanvasRect;
pub use crate::stroke::StrokeSample as ApiStrokeSample;
pub use crate::tile::TileCoord as ApiTileCoord;

#[derive(Clone, Debug)]
pub struct LayerInfo {
    pub id: u64,
    pub name: String,
    pub visible: bool,
    pub opacity: f32,
}

pub struct EngineOptions {
    pub width: u32,
    pub height: u32,
    pub tile_size: u32,
}

#[frb(opaque)]
pub struct FlossEngine {
    document: DrawingDocument,
}

impl FlossEngine {
    pub fn create(options: EngineOptions) -> Self {
        Self {
            document: DrawingDocument::new(options.width, options.height, options.tile_size),
        }
    }

    pub fn set_brush(&mut self, brush: BrushPreset) {
        self.document.set_brush(brush);
    }

    pub fn begin_stroke(&mut self, sample: StrokeSample) -> FrameDelta {
        self.document.begin_stroke(sample)
    }

    pub fn append_stroke_samples(&mut self, samples: Vec<StrokeSample>) -> FrameDelta {
        self.document.append_stroke_samples(&samples)
    }

    pub fn end_stroke(&mut self, sample: StrokeSample) -> FrameDelta {
        self.document.end_stroke(sample)
    }

    pub fn cancel_stroke(&mut self) -> FrameDelta {
        self.document.cancel_stroke()
    }

    pub fn clear(&mut self) -> FrameDelta {
        self.document.clear()
    }

    pub fn drain_frame_delta(&mut self) -> FrameDelta {
        self.document.drain_frame_delta()
    }

    pub fn stats(&self) -> EngineStats {
        self.document.stats()
    }

    pub fn snapshot_rgba(&self) -> Vec<u8> {
        self.document.snapshot_rgba()
    }

    pub fn add_layer(&mut self) -> u64 {
        self.document.add_layer()
    }

    pub fn delete_layer(&mut self, id: u64) {
        self.document.delete_layer(id);
    }

    pub fn move_layer(&mut self, id: u64, new_index: u64) {
        self.document.move_layer(id, new_index as usize);
    }

    pub fn set_layer_visibility(&mut self, id: u64, visible: bool) {
        self.document.set_layer_visibility(id, visible);
    }

    pub fn set_layer_opacity(&mut self, id: u64, opacity: f32) {
        self.document.set_layer_opacity(id, opacity);
    }

    pub fn set_active_layer(&mut self, id: u64) {
        self.document.set_active_layer(id);
    }

    pub fn rename_layer(&mut self, id: u64, name: String) {
        self.document.rename_layer(id, name);
    }

    pub fn list_layers(&self) -> Vec<LayerInfo> {
        self.document
            .layer_ids()
            .into_iter()
            .filter_map(|id| {
                self.document.get_layer(id).map(|layer| LayerInfo {
                    id: layer.id,
                    name: layer.name.clone(),
                    visible: layer.visible,
                    opacity: layer.opacity,
                })
            })
            .collect()
    }

    pub fn active_layer_id(&self) -> u64 {
        self.document.get_active_layer_id()
    }
}

pub fn default_brush() -> BrushPreset {
    BrushPreset {
        name: "Round".to_owned(),
        size: 18.0,
        opacity: 0.88,
        hardness: 0.72,
        spacing: 0.18,
        color_argb: 0xfff3f5ff,
        blend_mode: BrushBlendMode::Normal,
        pressure_curve_exponent: 2.0,
        velocity_size_sensitivity: 0.5,
        velocity_opacity_sensitivity: 0.3,
    }
}

pub fn inflated_stroke_bounds(samples: Vec<StrokeSample>, brush_size: f32) -> CanvasRect {
    crate::stroke::bounds_for_samples(&samples, brush_size)
}

pub fn resample_preview(samples: Vec<StrokeSample>, spacing: f32) -> Vec<CanvasPoint> {
    crate::stroke::resample_catmull_rom(&samples, spacing)
        .into_iter()
        .map(|sample| sample.position())
        .collect()
}
