//! Sensor configuration — maps tablet/pointer sensors through curves.



use crate::curve::CubicCurve;
use crate::stroke::StrokePoint;

/// The physical or logical sensor being mapped.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SensorType {
    Pressure,
    Speed,
    Distance,
    Fade,
    Random,
    StrokeRandom,
    DrawingAngle,
    TiltX,
    TiltY,
    Rotation,
}

/// A single sensor with its response curve and length parameter.
#[derive(Clone, Debug)]
pub struct SensorConfig {
    pub sensor_type: SensorType,
    pub curve: CubicCurve,
    /// For Distance: pixels. For Fade: dab count.
    pub length: f32,
}

impl SensorConfig {
    /// Create with an identity curve and default length.
    pub fn new(sensor_type: SensorType) -> Self {
        Self {
            sensor_type,
            curve: CubicCurve::identity(),
            length: 1000.0,
        }
    }

    /// Raw sensor value ∈ approximately [0, 1].
    pub fn raw_value(&self, sp: &StrokePoint) -> f32 {
        match self.sensor_type {
            SensorType::Pressure => sp.pressure,
            SensorType::Speed => sp.speed,
            SensorType::Distance => (sp.total_distance / self.length.max(1.0)).clamp(0.0, 1.0),
            SensorType::Fade => (sp.dab_seq_no as f32 / self.length.max(1.0)).clamp(0.0, 1.0),
            SensorType::Random => sp.random,
            SensorType::StrokeRandom => sp.stroke_random,
            SensorType::DrawingAngle => {
                use std::f32::consts::TAU;
                ((sp.drawing_angle / TAU) % 1.0 + 1.0) % 1.0
            }
            SensorType::TiltX => ((sp.tilt_x + 90.0) / 180.0).clamp(0.0, 1.0),
            SensorType::TiltY => ((sp.tilt_y + 90.0) / 180.0).clamp(0.0, 1.0),
            SensorType::Rotation => ((sp.twist + 180.0) / 360.0).clamp(0.0, 1.0),
        }
    }

    /// Curve-mapped value ∈ [0, 1].
    pub fn curved_value(&self, sp: &StrokePoint) -> f32 {
        self.curve.evaluate(self.raw_value(sp))
    }
}
