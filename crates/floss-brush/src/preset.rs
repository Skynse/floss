//! Brush preset — the complete configuration for a brush.
//!
//! Includes tip, size, opacity, dynamics, blend mode, color mixing, etc.

use floss_core::{BlendMode, Color};
use serde::{Deserialize, Serialize};

use crate::dynamics::BrushDynamics;
use crate::tip::{BrushTip, ProceduralBrushTip};
use crate::types::{AngleSource, BrushTipDirection, BrushTipShape};

/// How the brush engine mixes colors.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MixingMode {
    Standard,
    Perceptual,
}

/// Smudge/blend mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SmudgeMode {
    Blend,
    Smear,
    Smudge,
}

/// Brush quality level (affects stamp fidelity vs performance).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BrushQuality {
    Low,
    High,
}

/// Complete brush preset — equivalent to C# `BrushPreset`.
///
/// Note: This struct CANNOT derive Clone because it owns a `Box<dyn BrushTip>`.
/// For cloning, use `BrushPreset::clone_preset()` which clones the tip via
/// the tip's mask generation (lossy but functional for stroke continuation).
pub struct BrushPreset {
    pub name: String,
    /// Base brush size in document pixels.
    pub size: f64,
    /// Master opacity 0–1.
    pub opacity: f64,
    /// Hardness 0.001–1.0 (1 = hard edge).
    pub hardness: f64,
    /// Stamp spacing as fraction of stamp size.
    pub spacing: f64,
    /// Brush color.
    pub color: Color,
    /// Rotation angle in degrees.
    pub angle: f64,
    /// Sensor-driven dynamics.
    pub dynamics: BrushDynamics,
    /// Ink flow rate 0.01–1.0.
    pub flow: f64,
    /// Whether to mix with underlying color.
    pub color_mix: bool,
    /// Paint load 0–1 (how much color the brush carries).
    pub color_load: f64,
    /// Stretch of color load along stroke.
    pub color_stretch: f64,
    /// Blur amount 0–1.
    pub blur_amount: f64,
    /// Smudge mode.
    pub smudge_mode: SmudgeMode,
    /// Mixing mode.
    pub mixing_mode: MixingMode,
    /// Amount of paint deposited per stamp.
    pub amount_of_paint: f64,
    /// Density of paint (affects opacity buildup).
    pub density_of_paint: f64,
    /// Tip density 0–1 (scales stamp alpha globally).
    pub tip_density: f64,
    /// Tip thickness stretch 0.01–4.0.
    pub tip_thickness: f64,
    /// Direction of asymmetry.
    pub tip_direction: BrushTipDirection,
    /// Grain/texture amount 0–1.
    pub grain: f64,
    /// Optional texture image path.
    pub texture: Option<String>,
    /// Stabilization/smoothing amount.
    pub stabilization: f64,
    /// Quality level.
    pub quality: BrushQuality,
    /// The brush tip (procedural or image).
    pub tip: Box<dyn BrushTip>,
    /// Blend mode.
    pub blend_mode: BlendMode,
    /// Optional shape overlay for CSP-style tip masking.
    pub shape: Option<ProceduralBrushTip>,
    /// Angle source for rotation.
    pub base_angle_source: AngleSource,
    /// Random angle jitter in degrees.
    pub angle_jitter: f64,
    /// Base mask size (derived from tip + size).
    pub base_mask_size: i32,
}

impl BrushPreset {
    /// Create a simple brush preset with sensible defaults.
    pub fn simple(name: impl Into<String>, size: f64, color: Color) -> Self {
        let tip = ProceduralBrushTip::new(BrushTipShape::Circle, 1.0);
        let base_mask_size = base_size_for_tip(size);
        Self {
            name: name.into(),
            size,
            opacity: 1.0,
            hardness: 0.8,
            spacing: 0.15,
            color,
            angle: 0.0,
            dynamics: BrushDynamics::default(),
            flow: 1.0,
            color_mix: false,
            color_load: 1.0,
            color_stretch: 0.5,
            blur_amount: 0.0,
            smudge_mode: SmudgeMode::Blend,
            mixing_mode: MixingMode::Standard,
            amount_of_paint: 1.0,
            density_of_paint: 1.0,
            tip_density: 1.0,
            tip_thickness: 1.0,
            tip_direction: BrushTipDirection::Horizontal,
            grain: 0.0,
            texture: None,
            stabilization: 0.3,
            quality: BrushQuality::High,
            tip: Box::new(tip),
            blend_mode: BlendMode::Normal,
            shape: None,
            base_angle_source: AngleSource::None,
            angle_jitter: 0.0,
            base_mask_size,
        }
    }

    pub fn clone_preset(&self) -> Self {
        let mut cloned = Self::simple(self.name.clone(), self.size, self.color);
        cloned.opacity = self.opacity;
        cloned.hardness = self.hardness;
        cloned.spacing = self.spacing;
        cloned.angle = self.angle;
        cloned.dynamics = self.dynamics.clone();
        cloned.flow = self.flow;
        cloned.color_mix = self.color_mix;
        cloned.color_load = self.color_load;
        cloned.color_stretch = self.color_stretch;
        cloned.blur_amount = self.blur_amount;
        cloned.smudge_mode = self.smudge_mode;
        cloned.mixing_mode = self.mixing_mode;
        cloned.amount_of_paint = self.amount_of_paint;
        cloned.density_of_paint = self.density_of_paint;
        cloned.tip_density = self.tip_density;
        cloned.tip_thickness = self.tip_thickness;
        cloned.tip_direction = self.tip_direction;
        cloned.grain = self.grain;
        cloned.texture = self.texture.clone();
        cloned.stabilization = self.stabilization;
        cloned.quality = self.quality;
        cloned.blend_mode = self.blend_mode;
        cloned.shape = self.shape.as_ref().map(|shape| ProceduralBrushTip::new(shape.shape, shape.aspect_ratio));
        cloned.base_angle_source = self.base_angle_source;
        cloned.angle_jitter = self.angle_jitter;
        cloned.base_mask_size = self.base_mask_size;
        cloned
    }
}

/// Compute the base mask size from the brush size.
/// Larger brushes get larger masks for quality; clamped to reasonable bounds.
fn base_size_for_tip(brush_size: f64) -> i32 {
    let s = brush_size as i32;
    if s <= 16 {
        s.max(4)
    } else if s <= 128 {
        s
    } else if s <= 512 {
        s.min(256)
    } else {
        256
    }
}
