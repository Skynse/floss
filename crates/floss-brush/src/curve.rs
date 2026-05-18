//! Cubic spline curve with LUT-based evaluation.
//!
//! Ported from `Floss.App.Brushes.CubicCurve`.
//!
//! Maps [0,1] → [0,1] via a 256-entry lookup table precomputed
//! from control points. Thread-safe for reads; call `rebuild_lut()`
//! after editing points.

/// A control point on the curve.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CurvePoint {
    pub x: f32,
    pub y: f32,
}

impl CurvePoint {
    pub fn new(x: f32, y: f32) -> Self {
        Self {
            x: x.clamp(0.0, 1.0),
            y: y.clamp(0.0, 1.0),
        }
    }
}

/// Cubic spline curve mapping [0,1] → [0,1].
///
/// Evaluated via a precomputed 256-entry LUT. Points are sorted by x.
/// The spline uses Catmull-Rom interpolation through the control points.
#[derive(Clone, Debug)]
pub struct CubicCurve {
    points: Vec<CurvePoint>,
    lut: [f32; LUT_SIZE],
}

/// Number of LUT entries.
pub const LUT_SIZE: usize = 256;

impl CubicCurve {
    /// Create the identity curve (linear 0→0, 1→1).
    pub fn identity() -> Self {
        let mut c = Self {
            points: Vec::new(),
            lut: [0.0; LUT_SIZE],
        };
        c.reset();
        c
    }

    /// Create a linear curve from (x0,y0) to (x1,y1).
    pub fn linear(x0: f32, y0: f32, x1: f32, y1: f32) -> Self {
        let mut c = Self {
            points: vec![CurvePoint::new(x0, y0), CurvePoint::new(x1, y1)],
            lut: [0.0; LUT_SIZE],
        };
        c.rebuild_lut();
        c
    }

    /// Reset to identity (0,0)→(1,1).
    pub fn reset(&mut self) {
        self.points.clear();
        self.points.push(CurvePoint::new(0.0, 0.0));
        self.points.push(CurvePoint::new(1.0, 1.0));
        self.rebuild_lut();
    }

    /// Get the control points (sorted by x).
    pub fn points(&self) -> &[CurvePoint] {
        &self.points
    }

    /// Replace all control points.
    pub fn set_points(&mut self, pts: impl IntoIterator<Item = CurvePoint>) {
        self.points.clear();
        self.points.extend(pts);
        self.sort_and_clamp();
        self.rebuild_lut();
    }

    /// Add a control point.
    pub fn add_point(&mut self, x: f32, y: f32) {
        self.points.push(CurvePoint::new(x, y));
        self.sort_and_clamp();
        self.rebuild_lut();
    }

    /// Move a control point by index.
    pub fn move_point(&mut self, index: usize, x: f32, y: f32) {
        if index < self.points.len() {
            self.points[index] = CurvePoint::new(x, y);
            self.sort_and_clamp();
            self.rebuild_lut();
        }
    }

    /// Remove a control point (refuses to remove if ≤2 points).
    pub fn remove_point(&mut self, index: usize) {
        if self.points.len() > 2 && index < self.points.len() {
            self.points.remove(index);
            self.rebuild_lut();
        }
    }

    /// Evaluate the curve at x ∈ [0,1].
    #[inline]
    pub fn evaluate(&self, x: f32) -> f32 {
        let idx = (x.clamp(0.0, 1.0) * (LUT_SIZE - 1) as f32) as usize;
        self.lut[idx.min(LUT_SIZE - 1)]
    }

    /// Get the raw LUT array.
    pub fn lut(&self) -> &[f32; LUT_SIZE] {
        &self.lut
    }

    /// Clone the curve (explicit deep copy).
    pub fn clone_deep(&self) -> Self {
        Self {
            points: self.points.clone(),
            lut: self.lut,
        }
    }

    /// Rebuild the LUT from control points using Catmull-Rom interpolation.
    pub fn rebuild_lut(&mut self) {
        self.sort_and_clamp();
        let pts = &self.points;

        if pts.is_empty() {
            self.lut.fill(0.5);
            return;
        }
        if pts.len() == 1 {
            self.lut.fill(pts[0].y);
            return;
        }

        // Ensure endpoints
        let mut work = pts.clone();
        if work[0].x > 0.0 {
            work.insert(0, CurvePoint::new(0.0, work[0].y));
        }
        if work.last().unwrap().x < 1.0 {
            work.push(CurvePoint::new(1.0, work.last().unwrap().y));
        }

        // Generate LUT via Catmull-Rom through the sorted points
        for i in 0..LUT_SIZE {
            let x = i as f32 / (LUT_SIZE - 1) as f32;
            self.lut[i] = eval_catmull(&work, x);
        }
    }

    fn sort_and_clamp(&mut self) {
        self.points.sort_by(|a, b| a.x.partial_cmp(&b.x).unwrap());
        self.points.dedup_by_key(|p| (p.x * 10000.0) as i32);
    }
}

/// Evaluate Catmull-Rom spline through points at x.
fn eval_catmull(pts: &[CurvePoint], x: f32) -> f32 {
    // Find the segment: pts[idx] ≤ x < pts[idx+1]
    if x <= pts[0].x {
        return pts[0].y;
    }
    if x >= pts.last().unwrap().x {
        return pts.last().unwrap().y;
    }

    let mut idx = 0;
    for i in 0..pts.len() - 1 {
        if x >= pts[i].x && x < pts[i + 1].x {
            idx = i;
            break;
        }
    }

    let p0 = if idx > 0 { pts[idx - 1] } else { pts[idx] };
    let p1 = pts[idx];
    let p2 = pts[idx + 1];
    let p3 = if idx + 2 < pts.len() { pts[idx + 2] } else { pts[idx + 1] };

    let dx = p2.x - p1.x;
    if dx < 0.0001 {
        return p1.y;
    }
    let t = (x - p1.x) / dx;

    catmull_rom(p0.y, p1.y, p2.y, p3.y, t)
}

/// Catmull-Rom interpolation of float values.
#[inline]
fn catmull_rom(p0: f32, p1: f32, p2: f32, p3: f32, t: f32) -> f32 {
    let t2 = t * t;
    let t3 = t2 * t;
    0.5 * ((2.0 * p1)
        + (p2 - p0) * t
        + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2
        + (3.0 * p1 - p0 - 3.0 * p2 + p3) * t3)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn identity_is_linear() {
        let c = CubicCurve::identity();
        assert!((c.evaluate(0.0) - 0.0).abs() < 0.01);
        assert!((c.evaluate(0.5) - 0.5).abs() < 0.01);
        assert!((c.evaluate(1.0) - 1.0).abs() < 0.01);
    }

    #[test]
    fn linear_custom() {
        let c = CubicCurve::linear(0.0, 0.0, 1.0, 0.5);
        assert!((c.evaluate(0.0) - 0.0).abs() < 0.01);
        assert!((c.evaluate(1.0) - 0.5).abs() < 0.01);
    }

    #[test]
    fn points_sorted_by_x() {
        let mut c = CubicCurve::identity();
        c.set_points([
            CurvePoint::new(0.8, 0.2),
            CurvePoint::new(0.2, 0.8),
        ]);
        let pts = c.points();
        assert!(pts[0].x <= pts[1].x);
    }

    #[test]
    fn move_point_updates_lut() {
        let mut c = CubicCurve::identity();
        let before = c.evaluate(0.5);
        c.move_point(0, 0.0, 0.8);
        let after = c.evaluate(0.5);
        assert!((before - after).abs() > 0.01);
    }

    #[test]
    fn remove_point_maintains_lut() {
        let mut c = CubicCurve::identity();
        c.add_point(0.5, 0.5);
        assert_eq!(c.points().len(), 3);
        c.remove_point(1);
        assert_eq!(c.points().len(), 2);
        // LUT should still be valid
        assert!(c.evaluate(0.5) > 0.0);
    }
}
