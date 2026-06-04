//! CurveOption — combines sensors into a single multiplier via multiply or add.



use crate::curve::CubicCurve;
use crate::sensor::{SensorConfig, SensorType};
use crate::stroke::StrokePoint;

/// How multiple sensors are combined.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SensorCombineMode {
    /// Multiply all sensor values together.
    Multiply,
    /// Average all sensor values.
    Add,
}

/// Maps one or more sensors through cubic curves to produce a [min_output, max_output]
/// multiplier. Disabled or empty → returns 1.0 (no effect).
#[derive(Clone, Debug)]
pub struct CurveOption {
    pub enabled: bool,
    /// Lerp factor: 0 = no effect, 1 = full curve effect.
    pub strength: f32,
    /// Minimum output multiplier.
    pub min_output: f32,
    /// Maximum output multiplier.
    pub max_output: f32,
    /// How to combine multiple sensors.
    pub combine_mode: SensorCombineMode,
    /// The sensors driving this option.
    pub sensors: Vec<SensorConfig>,
}

impl CurveOption {
    /// Create a disabled option (returns 1.0).
    pub fn off() -> Self {
        Self {
            enabled: false,
            strength: 1.0,
            min_output: 0.0,
            max_output: 1.0,
            combine_mode: SensorCombineMode::Multiply,
            sensors: Vec::new(),
        }
    }

    /// Create a simple pressure-based option with a power curve.
    pub fn pressure(gamma: f32, min: f32, max: f32) -> Self {
        let mut opt = Self {
            enabled: true,
            strength: 1.0,
            min_output: min,
            max_output: max,
            combine_mode: SensorCombineMode::Multiply,
            sensors: Vec::new(),
        };
        opt.sensors.push(SensorConfig {
            sensor_type: SensorType::Pressure,
            curve: gamma_curve(gamma),
            length: 1000.0,
        });
        opt
    }

    /// Evaluate the combined sensor value.
    ///
    /// Returns a multiplier in approximately [min_output, max_output],
    /// lerped toward 1.0 by (1 - strength). No sensors or disabled → 1.0.
    pub fn compute(&self, sp: &StrokePoint) -> f32 {
        if !self.enabled || self.sensors.is_empty() {
            return 1.0;
        }

        let combined = match self.combine_mode {
            SensorCombineMode::Multiply => {
                let mut v = 1.0f32;
                for s in &self.sensors {
                    v *= s.curved_value(sp);
                }
                v
            }
            SensorCombineMode::Add => {
                let sum: f32 = self.sensors.iter().map(|s| s.curved_value(sp)).sum();
                (sum / self.sensors.len() as f32).clamp(0.0, 1.0)
            }
        };

        let raw = self.min_output + (self.max_output - self.min_output) * combined;
        1.0 - self.strength + self.strength * raw
    }
}

/// Build a power-curve (x^gamma) as a CubicCurve with 9 sample points.
fn gamma_curve(gamma: f32) -> CubicCurve {
    const STEPS: usize = 9;
    let mut c = CubicCurve::identity();
    let pts: Vec<_> = (0..STEPS)
        .map(|i| {
            let x = i as f32 / (STEPS - 1) as f32;
            crate::curve::CurvePoint::new(x, x.powf(gamma).clamp(0.0, 1.0))
        })
        .collect();
    c.set_points(pts);
    c
}
