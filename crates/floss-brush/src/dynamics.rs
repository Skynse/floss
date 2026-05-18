//! Brush dynamics — the full set of sensor-driven parameter curves.



use crate::curve_option::CurveOption;
use crate::stroke::StrokePoint;

/// All dynamics channels for a brush preset.
///
/// Each channel is a `CurveOption` that maps sensor inputs (pressure, speed, etc.)
/// to a multiplier applied to the corresponding brush parameter.
#[derive(Clone, Debug)]
pub struct BrushDynamics {
    pub size: CurveOption,
    pub opacity: CurveOption,
    pub flow: CurveOption,
    pub hardness: CurveOption,
    pub scatter: CurveOption,
    pub rotation: CurveOption,
    pub spacing: CurveOption,
    pub tip_density: CurveOption,
    pub tip_thickness: CurveOption,
}

impl Default for BrushDynamics {
    fn default() -> Self {
        Self {
            size: CurveOption::off(),
            opacity: CurveOption::off(),
            flow: CurveOption::off(),
            hardness: CurveOption::off(),
            scatter: CurveOption::off(),
            rotation: CurveOption::off(),
            spacing: CurveOption::off(),
            tip_density: CurveOption::off(),
            tip_thickness: CurveOption::off(),
        }
    }
}

impl BrushDynamics {
    /// Evaluate size multiplier.
    #[inline]
    pub fn eval_size(&self, sp: &StrokePoint) -> f32 {
        self.size.compute(sp)
    }

    /// Evaluate opacity multiplier.
    #[inline]
    pub fn eval_opacity(&self, sp: &StrokePoint) -> f32 {
        self.opacity.compute(sp)
    }

    /// Evaluate flow multiplier.
    #[inline]
    pub fn eval_flow(&self, sp: &StrokePoint) -> f32 {
        self.flow.compute(sp)
    }

    /// Evaluate hardness multiplier.
    #[inline]
    pub fn eval_hardness(&self, sp: &StrokePoint) -> f32 {
        self.hardness.compute(sp)
    }

    /// Evaluate scatter amount (0 = none).
    #[inline]
    pub fn eval_scatter(&self, sp: &StrokePoint) -> f32 {
        self.scatter.compute(sp)
    }

    /// Evaluate spacing multiplier.
    #[inline]
    pub fn eval_spacing(&self, sp: &StrokePoint) -> f32 {
        self.spacing.compute(sp)
    }

    /// Evaluate tip density multiplier.
    #[inline]
    pub fn eval_tip_density(&self, sp: &StrokePoint) -> f32 {
        self.tip_density.compute(sp)
    }

    /// Evaluate tip thickness multiplier.
    #[inline]
    pub fn eval_tip_thickness(&self, sp: &StrokePoint) -> f32 {
        self.tip_thickness.compute(sp)
    }

    /// Evaluate rotation angle in degrees offset.
    /// Returns (compute - 0.5) * 360. Disabled → 0.
    #[inline]
    pub fn eval_rotation_deg(&self, sp: &StrokePoint) -> f32 {
        if !self.rotation.enabled || self.rotation.sensors.is_empty() {
            return 0.0;
        }
        (self.rotation.compute(sp) - 0.5) * 360.0
    }
}
