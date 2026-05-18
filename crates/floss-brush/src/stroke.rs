//! Stroke sampling types — input data and per-stroke state.

/// A single sample point from a tablet/pointer input, enriched with
/// stroke-level metrics for dynamics evaluation.
#[derive(Debug, Clone, Copy)]
pub struct StrokePoint {
    pub x: f32,
    pub y: f32,
    pub pressure: f32,
    pub tilt_x: f32,
    pub tilt_y: f32,
    pub twist: f32,
    /// Radians — direction of stroke motion at this point.
    pub drawing_angle: f32,
    /// Normalized 0–1 speed.
    pub speed: f32,
    /// Cumulative pixels painted since stroke start.
    pub total_distance: f32,
    /// Dab index since stroke start.
    pub dab_seq_no: i32,
    /// Per-dab pseudorandom [0, 1].
    pub random: f32,
    /// Per-stroke pseudorandom [0, 1] (constant for entire stroke).
    pub stroke_random: f32,
}

impl StrokePoint {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        x: f32, y: f32, pressure: f32,
        tilt_x: f32, tilt_y: f32, twist: f32,
        drawing_angle: f32, speed: f32,
        total_distance: f32, dab_seq_no: i32,
        random: f32, stroke_random: f32,
    ) -> Self {
        Self {
            x, y, pressure, tilt_x, tilt_y, twist,
            drawing_angle, speed, total_distance,
            dab_seq_no, random, stroke_random,
        }
    }
}

/// Mutable state tracked across a single brush stroke.
#[derive(Debug, Clone)]
pub struct StrokeState {
    pub last_x: f32,
    pub last_y: f32,
    pub last_pressure: f32,
    pub last_tilt_x: f32,
    pub last_tilt_y: f32,
    pub distance_leftover: f32,
    pub next_stamp_distance: f32,
    pub total_distance: f32,
    pub dab_seq_no: i32,
    /// Radians — direction of last stroke segment.
    pub drawing_angle: f32,
}

impl StrokeState {
    pub fn new(x: f32, y: f32, pressure: f32, tilt_x: f32, tilt_y: f32) -> Self {
        Self {
            last_x: x,
            last_y: y,
            last_pressure: pressure,
            last_tilt_x: tilt_x,
            last_tilt_y: tilt_y,
            distance_leftover: 0.0,
            next_stamp_distance: 0.0,
            total_distance: 0.0,
            dab_seq_no: 0,
            drawing_angle: 0.0,
        }
    }
}
