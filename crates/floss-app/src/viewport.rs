//! Canvas viewport — renders the composited document with zoom/pan/rotate.
//!
//! Handles pointer and keyboard events, routing them to the tool controller
//! and updating the viewport transform.

use floss_core::Transform;

/// The canvas viewport state — owns the tool controller and compositor.
pub struct Viewport {
    /// Viewport transform (zoom, pan, rotate, flip).
    pub transform: Transform,
    /// Viewport size in pixels.
    pub viewport_w: f64,
    pub viewport_h: f64,
}

impl Viewport {
    pub fn new() -> Self {
        Self {
            transform: Transform::default(),
            viewport_w: 1024.0,
            viewport_h: 768.0,
        }
    }

    /// Zoom by a factor around a viewport-center point.
    pub fn zoom(&mut self, factor: f64) {
        let new_scale = (self.transform.scale * factor).clamp(0.01, 256.0);
        self.transform.scale = new_scale;
    }

    /// Pan by a delta in viewport pixels.
    pub fn pan(&mut self, dx: f64, dy: f64) {
        self.transform.pan.x += dx;
        self.transform.pan.y += dy;
    }

    /// Rotate by a delta in radians.
    pub fn rotate(&mut self, delta: f64) {
        self.transform.angle += delta;
    }

    /// Reset viewport to default.
    pub fn reset(&mut self) {
        self.transform = Transform::default();
    }
}
