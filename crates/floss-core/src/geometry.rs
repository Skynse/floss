use glam::DVec2;
use serde::{Deserialize, Serialize};

/// Axis-aligned integer rectangle in document pixel coordinates.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

impl Rect {
    pub const ZERO: Self = Self {
        x: 0,
        y: 0,
        w: 0,
        h: 0,
    };

    #[inline]
    pub const fn new(x: i32, y: i32, w: i32, h: i32) -> Self {
        Self { x, y, w, h }
    }

    #[inline]
    pub fn left(&self) -> i32 {
        self.x
    }
    #[inline]
    pub fn top(&self) -> i32 {
        self.y
    }
    #[inline]
    pub fn right(&self) -> i32 {
        self.x + self.w
    }
    #[inline]
    pub fn bottom(&self) -> i32 {
        self.y + self.h
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.w <= 0 || self.h <= 0
    }

    /// Intersection of two rectangles. Returns `Rect::ZERO` if disjoint.
    #[inline]
    pub fn intersect(&self, other: Rect) -> Rect {
        let x = self.x.max(other.x);
        let y = self.y.max(other.y);
        let r = self.right().min(other.right());
        let b = self.bottom().min(other.bottom());
        if x < r && y < b {
            Rect::new(x, y, r - x, b - y)
        } else {
            Rect::ZERO
        }
    }

    /// Smallest rectangle containing both rects.
    #[inline]
    pub fn union(&self, other: Rect) -> Rect {
        if self.is_empty() {
            return other;
        }
        if other.is_empty() {
            return *self;
        }
        let x = self.x.min(other.x);
        let y = self.y.min(other.y);
        let r = self.right().max(other.right());
        let b = self.bottom().max(other.bottom());
        Rect::new(x, y, r - x, b - y)
    }

    /// Expand (or shrink) by `delta` pixels on all sides.
    #[inline]
    pub fn inflate(&self, delta: i32) -> Rect {
        Rect::new(
            self.x - delta,
            self.y - delta,
            (self.w + 2 * delta).max(0),
            (self.h + 2 * delta).max(0),
        )
    }

    /// Translate by `(dx, dy)`.
    #[inline]
    pub fn translate(&self, dx: i32, dy: i32) -> Rect {
        Rect::new(self.x + dx, self.y + dy, self.w, self.h)
    }

    #[inline]
    pub fn contains_point(&self, x: i32, y: i32) -> bool {
        x >= self.x && x < self.right() && y >= self.y && y < self.bottom()
    }
}

/// 2D affine transform for the canvas viewport: scale (zoom), rotation,
/// and pan offset.
#[derive(Debug, Clone, Copy)]
pub struct Transform {
    /// Pixels per document unit (zoom level).
    pub scale: f64,
    /// Rotation angle in radians.
    pub angle: f64,
    /// Pan offset in viewport pixels.
    pub pan: DVec2,
    /// Whether the canvas is flipped horizontally.
    pub flip_h: bool,
    /// Whether the canvas is flipped vertically.
    pub flip_v: bool,
}

impl Default for Transform {
    fn default() -> Self {
        Self {
            scale: 1.0,
            angle: 0.0,
            pan: DVec2::ZERO,
            flip_h: false,
            flip_v: false,
        }
    }
}

impl Transform {
    /// Convert viewport pixel coordinates to document coordinates.
    #[inline]
    pub fn viewport_to_doc(&self, viewport: DVec2) -> DVec2 {
        let mut pt = (viewport - self.pan) / self.scale;
        if self.flip_h {
            pt.x = -pt.x;
        }
        if self.flip_v {
            pt.y = -pt.y;
        }
        if self.angle.abs() > 1e-12 {
            let cos = self.angle.cos();
            let sin = self.angle.sin();
            pt = DVec2::new(
                pt.x * cos + pt.y * sin,
                -pt.x * sin + pt.y * cos,
            );
        }
        pt
    }

    /// Convert document coordinates to viewport pixels.
    #[inline]
    pub fn doc_to_viewport(&self, doc: DVec2) -> DVec2 {
        let mut pt = doc;
        if self.angle.abs() > 1e-12 {
            let cos = (-self.angle).cos();
            let sin = (-self.angle).sin();
            pt = DVec2::new(
                pt.x * cos + pt.y * sin,
                -pt.x * sin + pt.y * cos,
            );
        }
        if self.flip_h {
            pt.x = -pt.x;
        }
        if self.flip_v {
            pt.y = -pt.y;
        }
        pt * self.scale + self.pan
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn intersect_overlapping() {
        let a = Rect::new(0, 0, 10, 10);
        let b = Rect::new(5, 5, 10, 10);
        let i = a.intersect(b);
        assert_eq!(i, Rect::new(5, 5, 5, 5));
    }

    #[test]
    fn intersect_disjoint_returns_zero() {
        let a = Rect::new(0, 0, 10, 10);
        let b = Rect::new(20, 20, 10, 10);
        assert_eq!(a.intersect(b), Rect::ZERO);
    }

    #[test]
    fn union_expands_bounds() {
        let a = Rect::new(0, 0, 10, 10);
        let b = Rect::new(5, 5, 10, 10);
        assert_eq!(a.union(b), Rect::new(0, 0, 15, 15));
    }

    #[test]
    fn transform_viewport_to_doc_roundtrip() {
        let t = Transform {
            scale: 2.0,
            pan: DVec2::new(100.0, 50.0),
            ..Default::default()
        };
        let doc = DVec2::new(256.0, 128.0);
        let vp = t.doc_to_viewport(doc);
        let back = t.viewport_to_doc(vp);
        assert!((back.x - doc.x).abs() < 0.001);
        assert!((back.y - doc.y).abs() < 0.001);
    }
}
