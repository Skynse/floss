#[derive(Clone, Debug, PartialEq)]
pub enum BrushBlendMode {
    Normal,
    Multiply,
    Screen,
    Overlay,
}

#[derive(Clone, Debug, PartialEq)]
pub struct BrushPreset {
    pub name: String,
    pub size: f32,
    pub opacity: f32,
    pub hardness: f32,
    pub spacing: f32,
    pub color_argb: u32,
    pub blend_mode: BrushBlendMode,
    /// Exponent for pressure curve. 1.0 = linear, 2.0 = quadratic (requires more pressure for full size).
    pub pressure_curve_exponent: f32,
    /// How much velocity reduces brush size (0.0 = no effect, 1.0 = max effect).
    pub velocity_size_sensitivity: f32,
    /// How much velocity reduces brush opacity (0.0 = no effect, 1.0 = max effect).
    pub velocity_opacity_sensitivity: f32,
}

impl Default for BrushPreset {
    fn default() -> Self {
        Self {
            name: "Round".to_owned(),
            size: 18.0,
            opacity: 0.88,
            hardness: 0.72,
            spacing: 0.18,
            color_argb: 0xfff3f5ff,
            blend_mode: BrushBlendMode::Normal,
            pressure_curve_exponent: 2.0,
            velocity_size_sensitivity: 0.5,
            velocity_opacity_sensitivity: 0.3,
        }
    }
}
