//! Layer compositor — orchestrates GPU compositing of all layers.
//!
//! Tracks per-layer tile caches, composite tile dirty state, and
//! determines which tiles need re-rendering each frame.

use std::collections::HashMap;

use floss_core::{Rect, Transform};
use floss_document::DrawingDocument;

use crate::cache::{CompTileCoord, CompositeTileTracker, GpuTileCache, COMP_TILE_SIZE};

/// Blend mode for compositing (maps to shader blend mode enum).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompositeBlendMode {
    Normal = 0,
    Multiply = 1,
    Screen = 2,
    Overlay = 3,
    ColorDodge = 4,
    ColorBurn = 5,
    Darken = 6,
    Lighten = 7,
    HardLight = 8,
    SoftLight = 9,
    Difference = 10,
    Erase = 11,
}

impl From<floss_core::BlendMode> for CompositeBlendMode {
    fn from(bm: floss_core::BlendMode) -> Self {
        match bm {
            floss_core::BlendMode::Normal => Self::Normal,
            floss_core::BlendMode::Multiply => Self::Multiply,
            floss_core::BlendMode::Screen => Self::Screen,
            floss_core::BlendMode::Overlay => Self::Overlay,
            floss_core::BlendMode::ColorDodge => Self::ColorDodge,
            floss_core::BlendMode::ColorBurn => Self::ColorBurn,
            floss_core::BlendMode::Darken => Self::Darken,
            floss_core::BlendMode::Lighten => Self::Lighten,
            floss_core::BlendMode::HardLight => Self::HardLight,
            floss_core::BlendMode::SoftLight => Self::SoftLight,
            floss_core::BlendMode::Difference => Self::Difference,
            floss_core::BlendMode::Erase => Self::Erase,
        }
    }
}

/// Metadata for compositing a single layer.
#[derive(Debug, Clone)]
pub struct LayerCompositeInfo {
    pub visible: bool,
    pub opacity: f32,
    pub blend_mode: CompositeBlendMode,
    /// Layer is a clipping mask for the layer below.
    pub is_clipping_mask: bool,
}

/// The compositor manages GPU tiles for all layers and determines
/// what needs to be redrawn each frame.
pub struct Compositor {
    /// Per-layer GPU tile caches.
    layer_caches: HashMap<usize, GpuTileCache>,
    /// Composite tile dirty tracking.
    comp_tracker: CompositeTileTracker,
    /// Canvas dimensions.
    width: i32,
    height: i32,
    /// Full recomposite needed next frame.
    full_dirty: bool,
}

impl Compositor {
    pub fn new(width: i32, height: i32) -> Self {
        Self {
            layer_caches: HashMap::new(),
            comp_tracker: CompositeTileTracker::new(width, height),
            width,
            height,
            full_dirty: true,
        }
    }

    /// Resize the canvas. Clears all GPU tiles.
    pub fn resize(&mut self, width: i32, height: i32) {
        self.width = width;
        self.height = height;
        self.layer_caches.clear();
        self.comp_tracker.resize(width, height);
        self.full_dirty = true;
    }

    /// Invalidate a document-space region. Marks layer tiles and
    /// composite tiles as dirty.
    pub fn invalidate_region(&mut self, region: Rect, layer_index: usize) {
        if region.is_empty() {
            return;
        }
        // Mark layer tiles dirty
        if let Some(cache) = self.layer_caches.get_mut(&layer_index) {
            cache.mark_region_dirty(region);
        }
        // Mark composite tiles dirty
        self.comp_tracker.invalidate_region(region);
    }

    /// Invalidate all layers — full re-render.
    pub fn invalidate_all(&mut self) {
        self.full_dirty = true;
        self.comp_tracker.all_dirty();
        for cache in self.layer_caches.values_mut() {
            cache.clear();
        }
    }

    /// Begin a new frame. Bumps generation counters.
    pub fn begin_frame(&mut self, _transform: &Transform) {
        for cache in self.layer_caches.values_mut() {
            cache.begin_frame();
        }
    }

    /// Get the layer cache for a given layer index (creates if absent).
    pub fn layer_cache_mut(&mut self, layer_index: usize) -> &mut GpuTileCache {
        self.layer_caches
            .entry(layer_index)
            .or_insert_with(|| GpuTileCache::new(2048))
    }

    /// Get the list of composite tiles that need re-rendering this frame.
    pub fn dirty_comp_tiles(&self) -> Vec<CompTileCoord> {
        if self.full_dirty {
            // Return all tiles
            self.comp_tracker.dirty_tiles()
        } else {
            self.comp_tracker.dirty_tiles()
        }
    }

    /// Mark a composite tile as rendered clean.
    pub fn mark_comp_clean(&mut self, coord: CompTileCoord) {
        self.comp_tracker.mark_clean(coord);
    }

    /// Mark the entire composite as clean.
    pub fn mark_all_clean(&mut self) {
        self.full_dirty = false;
    }

    /// Build layer composite info for rendering.
    pub fn build_layer_infos(&self, doc: &DrawingDocument) -> Vec<LayerCompositeInfo> {
        (0..doc.layer_count())
            .map(|i| {
                let layer = doc.layer(i);
                LayerCompositeInfo {
                    visible: layer.visible,
                    opacity: layer.opacity as f32,
                    blend_mode: layer.blend_mode.into(),
                    is_clipping_mask: false,
                }
            })
            .collect()
    }

    pub fn width(&self) -> i32 {
        self.width
    }
    pub fn height(&self) -> i32 {
        self.height
    }

    /// Compute the composite tile coordinate for a document pixel.
    pub fn comp_tile_coord(pixel_x: i32, pixel_y: i32) -> CompTileCoord {
        (
            pixel_x / COMP_TILE_SIZE as i32,
            pixel_y / COMP_TILE_SIZE as i32,
        )
    }

    /// Get the pixel rect for a composite tile coordinate.
    pub fn comp_tile_rect(coord: CompTileCoord) -> Rect {
        Rect::new(
            coord.0 * COMP_TILE_SIZE as i32,
            coord.1 * COMP_TILE_SIZE as i32,
            COMP_TILE_SIZE as i32,
            COMP_TILE_SIZE as i32,
        )
    }

    /// Compute which composite tiles are visible in the viewport.
    pub fn visible_comp_tiles(
        &self,
        viewport: &Transform,
        viewport_w: f64,
        viewport_h: f64,
    ) -> Vec<CompTileCoord> {
        // Convert viewport corners to document space
        let corners = [
            viewport.viewport_to_doc(glam::DVec2::new(0.0, 0.0)),
            viewport.viewport_to_doc(glam::DVec2::new(viewport_w, 0.0)),
            viewport.viewport_to_doc(glam::DVec2::new(0.0, viewport_h)),
            viewport.viewport_to_doc(glam::DVec2::new(viewport_w, viewport_h)),
        ];

        let min_x = corners.iter().map(|c| c.x).fold(f64::INFINITY, f64::min) as i32;
        let min_y = corners.iter().map(|c| c.y).fold(f64::INFINITY, f64::min) as i32;
        let max_x = corners.iter().map(|c| c.x).fold(f64::NEG_INFINITY, f64::max) as i32;
        let max_y = corners.iter().map(|c| c.y).fold(f64::NEG_INFINITY, f64::max) as i32;

        let mut tiles = Vec::new();
        let first_tx = min_x / COMP_TILE_SIZE as i32;
        let first_ty = min_y / COMP_TILE_SIZE as i32;
        let last_tx = max_x / COMP_TILE_SIZE as i32;
        let last_ty = max_y / COMP_TILE_SIZE as i32;

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                if tx >= 0
                    && ty >= 0
                    && tx * (COMP_TILE_SIZE as i32) < self.width
                    && ty * (COMP_TILE_SIZE as i32) < self.height
                {
                    tiles.push((tx, ty));
                }
            }
        }
        tiles
    }
}
