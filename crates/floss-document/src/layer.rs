//! Drawing layer — wraps a `TiledPixelBuffer` with layer metadata.
//!
//! Each layer has a name, visibility, lock state, blend mode, opacity,
//! and an optional clipping mask (linked layer).

use floss_core::{BlendMode, Rect};

use crate::tile::TiledPixelBuffer;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExpressionColorMode {
    Color,
    Gray,
    Monochrome,
}

/// A single layer in the document stack.
pub struct DrawingLayer {
    /// Layer display name.
    pub name: String,
    /// Pixel data (sparse tiled buffer).
    pub pixels: TiledPixelBuffer,
    /// Visible in the composite.
    pub visible: bool,
    /// Prevent painting on this layer.
    pub locked: bool,
    /// Blend mode for compositing.
    pub blend_mode: BlendMode,
    /// Layer opacity (0–1).
    pub opacity: f64,
    /// Optional layer color label.
    pub layer_color: Option<[u8; 4]>,
    /// Expression color mode.
    pub expression_color: ExpressionColorMode,
    /// Pixel-space offset.
    pub offset_x: i32,
    pub offset_y: i32,
    /// This layer is a group folder.
    pub is_group: bool,
    /// Group is expanded in the layer panel.
    pub group_expanded: bool,
    /// Whether the folder is visually open.
    pub is_open: bool,
    /// Whether the layer clips to the layer below.
    pub is_clipping: bool,
    /// Prevent alpha writes while painting.
    pub is_alpha_locked: bool,
    /// This layer is a reference layer (used by eyedropper, fill, etc.).
    pub is_reference: bool,
    /// This layer is the paper/background layer.
    pub is_paper: bool,
    /// Visual indent level in the layer panel.
    pub indent_level: i32,
    /// Index of the parent group, or -1 if top-level.
    pub parent_group: i32,
    /// Layer thumbnail dirty — needs re-render.
    pub thumbnail_dirty: bool,
}

impl DrawingLayer {
    /// Create a new empty layer with the given canvas dimensions.
    pub fn new(name: impl Into<String>, width: i32, height: i32) -> Self {
        Self {
            name: name.into(),
            pixels: TiledPixelBuffer::new(width, height),
            visible: true,
            locked: false,
            blend_mode: BlendMode::Normal,
            opacity: 1.0,
            layer_color: None,
            expression_color: ExpressionColorMode::Color,
            offset_x: 0,
            offset_y: 0,
            is_group: false,
            group_expanded: false,
            is_open: true,
            is_clipping: false,
            is_alpha_locked: false,
            is_reference: false,
            is_paper: false,
            indent_level: 0,
            parent_group: -1,
            thumbnail_dirty: false,
        }
    }

    /// Returns true if the layer can be painted on.
    pub fn can_paint(&self) -> bool {
        self.visible && !self.locked && !self.is_group
    }

    /// Returns true for paint operations that respect visibility.
    pub fn can_paint_active(&self) -> bool {
        self.visible && !self.locked && !self.is_group
    }

    /// The bounding box of the layer's content.
    pub fn content_bounds(&mut self) -> Rect {
        self.pixels.compute_content_bounds()
    }

    /// Check whether any content tiles intersect the given region.
    pub fn has_content_in(&self, region: Rect) -> bool {
        self.pixels.has_content_tiles(region)
    }

    /// Create a deep clone of this layer (with copied pixel data).
    pub fn clone_deep(&mut self) -> Self {
        let pixels_snapshot = self.pixels.capture_tiles();
        let mut cloned = Self {
            name: self.name.clone(),
            pixels: TiledPixelBuffer::new(self.pixels.width(), self.pixels.height()),
            visible: self.visible,
            locked: self.locked,
            blend_mode: self.blend_mode,
            opacity: self.opacity,
            layer_color: self.layer_color,
            expression_color: self.expression_color,
            offset_x: self.offset_x,
            offset_y: self.offset_y,
            is_group: self.is_group,
            group_expanded: self.group_expanded,
            is_open: self.is_open,
            is_clipping: self.is_clipping,
            is_alpha_locked: self.is_alpha_locked,
            is_reference: self.is_reference,
            is_paper: self.is_paper,
            indent_level: self.indent_level,
            parent_group: self.parent_group,
            thumbnail_dirty: true,
        };
        cloned.pixels.restore_tiles(pixels_snapshot);
        cloned
    }
}
