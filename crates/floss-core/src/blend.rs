use serde::{Deserialize, Serialize};

/// Porter-Duff / Photoshop blend mode for layer compositing.
///
/// Maps to the modes in LayerCompositorPixelOps (Drawpile-based).
/// Some modes use precomputed 256×256 LUTs for fast byte-level blending;
/// HSL modes use double-precision math.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum BlendMode {
    Normal,
    PassThrough,
    Dissolve,
    Multiply,
    Screen,
    Overlay,
    SoftLight,
    HardLight,
    ColorDodge,
    ColorBurn,
    EasyDodge,
    Darken,
    Lighten,
    Difference,
    Exclusion,
    LinearBurn,
    LinearDodge,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,
    Subtract,
    Divide,
    DarkerColor,
    LighterColor,
    Hue,
    Saturation,
    Color,
    Luminosity,
    NormalAlphaPreserving,
    Erase,
    SrcOver,
    DstOut,
    Clear,
    Pigment,
    PigmentAlpha,
    PigmentAndEraser,
    OklabNormal,
    OklabNormalAndEraser,
    OklabRecolor,
}

impl Default for BlendMode {
    fn default() -> Self {
        Self::Normal
    }
}

impl BlendMode {
    /// Drawpile DP_blend_mode_preserves_alpha: true for alpha-preserving modes.
    pub fn preserves_alpha(&self) -> bool {
        matches!(
            self,
            BlendMode::NormalAlphaPreserving
                | BlendMode::Multiply
                | BlendMode::Screen
                | BlendMode::Overlay
                | BlendMode::SoftLight
                | BlendMode::HardLight
                | BlendMode::ColorDodge
                | BlendMode::ColorBurn
                | BlendMode::EasyDodge
                | BlendMode::Darken
                | BlendMode::Lighten
                | BlendMode::Difference
                | BlendMode::Exclusion
                | BlendMode::LinearBurn
                | BlendMode::LinearDodge
                | BlendMode::VividLight
                | BlendMode::LinearLight
                | BlendMode::PinLight
                | BlendMode::HardMix
                | BlendMode::Subtract
                | BlendMode::Divide
                | BlendMode::DarkerColor
                | BlendMode::LighterColor
                | BlendMode::Hue
                | BlendMode::Saturation
                | BlendMode::Color
                | BlendMode::Luminosity
        )
    }

    pub fn clip(&self, clip: bool) -> BlendMode {
        if clip && *self == BlendMode::Normal {
            BlendMode::NormalAlphaPreserving
        } else {
            *self
        }
    }

    /// Returns true if this mode samples pixels below the stroke.
    pub fn reads_destination(&self) -> bool {
        !matches!(self, BlendMode::Normal | BlendMode::Erase)
    }

    pub fn is_erase(&self) -> bool {
        matches!(self, BlendMode::Erase)
    }

    /// Check if mode has a precomputed LUT.
    pub fn has_lut(&self) -> bool {
        matches!(
            self,
            BlendMode::Multiply
                | BlendMode::Screen
                | BlendMode::Overlay
                | BlendMode::SoftLight
                | BlendMode::HardLight
                | BlendMode::ColorDodge
                | BlendMode::ColorBurn
                | BlendMode::LinearBurn
                | BlendMode::LinearDodge
                | BlendMode::VividLight
                | BlendMode::LinearLight
                | BlendMode::PinLight
                | BlendMode::HardMix
                | BlendMode::Subtract
                | BlendMode::Divide
        )
    }

    /// Check if mode requires HSL/double-precision math.
    pub fn is_hsl_mode(&self) -> bool {
        matches!(
            self,
            BlendMode::DarkerColor
                | BlendMode::LighterColor
                | BlendMode::Hue
                | BlendMode::Saturation
                | BlendMode::Color
                | BlendMode::Luminosity
        )
    }

    pub fn can_decrease_opacity(&self) -> bool {
        matches!(
            self,
            BlendMode::Erase | BlendMode::DstOut | BlendMode::Clear | BlendMode::Normal
        )
    }
}

// ── LUT construction ───────────────────────────────────────────────────────

/// A 256×256 lookup table: `lut[(src_byte << 8) | dst_byte]` yields blended byte.
pub struct BlendLut {
    pub data: Box<[u8; 65536]>,
}

impl BlendLut {
    pub fn new(f: fn(f64, f64) -> f64) -> Self {
        let mut data = Box::new([0u8; 65536]);
        for s in 0u16..256 {
            for d in 0u16..256 {
                let sv = s as f64 / 255.0;
                let dv = d as f64 / 255.0;
                let result = f(sv, dv).clamp(0.0, 1.0);
                data[((s as usize) << 8) | (d as usize)] =
                    (result * 255.0 + 0.5) as u8;
            }
        }
        Self { data }
    }

    pub fn get_lut(mode: BlendMode) -> Option<&'static BlendLut> {
        LUTS.lut(mode)
    }
}

struct AllLuts {
    multiply: BlendLut,
    screen: BlendLut,
    overlay: BlendLut,
    soft_light: BlendLut,
    hard_light: BlendLut,
    color_dodge: BlendLut,
    color_burn: BlendLut,
    linear_burn: BlendLut,
    linear_dodge: BlendLut,
    vivid_light: BlendLut,
    linear_light: BlendLut,
    pin_light: BlendLut,
    hard_mix: BlendLut,
    subtract: BlendLut,
    divide: BlendLut,
}

static LUTS: std::sync::LazyLock<AllLuts> = std::sync::LazyLock::new(|| AllLuts {
    multiply: BlendLut::new(|s, d| s * d),
    screen: BlendLut::new(|s, d| 1.0 - (1.0 - s) * (1.0 - d)),
    overlay: BlendLut::new(|s, d| blend_overlay(s, d)),
    soft_light: BlendLut::new(|s, d| blend_soft_light(s, d)),
    hard_light: BlendLut::new(|s, d| blend_hard_light(s, d)),
    color_dodge: BlendLut::new(|s, d| blend_color_dodge(s, d)),
    color_burn: BlendLut::new(|s, d| blend_color_burn(s, d)),
    linear_burn: BlendLut::new(|s, d| (d + s - 1.0).max(0.0)),
    linear_dodge: BlendLut::new(|s, d| (d + s).min(1.0)),
    vivid_light: BlendLut::new(|s, d| blend_vivid_light(s, d)),
    linear_light: BlendLut::new(|s, d| blend_linear_light(s, d)),
    pin_light: BlendLut::new(|s, d| blend_pin_light(s, d)),
    hard_mix: BlendLut::new(|s, d| blend_hard_mix(s, d)),
    subtract: BlendLut::new(|s, d| (d - s).max(0.0)),
    divide: BlendLut::new(|s, d| if s <= 0.0 { 1.0 } else { (d / s).min(1.0) }),
});

impl AllLuts {
    fn lut(&self, mode: BlendMode) -> Option<&'static BlendLut> {
        // Safety: LazyLock ensures the static lives forever once initialized
        let ptr: &'static AllLuts = unsafe { &*(self as *const AllLuts) };
        match mode {
            BlendMode::Multiply => Some(&ptr.multiply),
            BlendMode::Screen => Some(&ptr.screen),
            BlendMode::Overlay => Some(&ptr.overlay),
            BlendMode::SoftLight => Some(&ptr.soft_light),
            BlendMode::HardLight => Some(&ptr.hard_light),
            BlendMode::ColorDodge => Some(&ptr.color_dodge),
            BlendMode::ColorBurn => Some(&ptr.color_burn),
            BlendMode::LinearBurn => Some(&ptr.linear_burn),
            BlendMode::LinearDodge => Some(&ptr.linear_dodge),
            BlendMode::VividLight => Some(&ptr.vivid_light),
            BlendMode::LinearLight => Some(&ptr.linear_light),
            BlendMode::PinLight => Some(&ptr.pin_light),
            BlendMode::HardMix => Some(&ptr.hard_mix),
            BlendMode::Subtract => Some(&ptr.subtract),
            BlendMode::Divide => Some(&ptr.divide),
            _ => None,
        }
    }
}

// ── Blend functions (double precision) ─────────────────────────────────────

#[inline]
fn blend_overlay(dst: f64, src: f64) -> f64 {
    if dst < 0.5 {
        2.0 * dst * src
    } else {
        1.0 - 2.0 * (1.0 - dst) * (1.0 - src)
    }
}

#[inline]
fn blend_soft_light(dst: f64, src: f64) -> f64 {
    if src < 0.5 {
        dst - (1.0 - 2.0 * src) * dst * (1.0 - dst)
    } else {
        let d = if dst < 0.25 {
            ((16.0 * dst - 12.0) * dst + 4.0) * dst
        } else {
            dst.sqrt()
        };
        dst + (2.0 * src - 1.0) * (d - dst)
    }
}

#[inline]
fn blend_hard_light(dst: f64, src: f64) -> f64 {
    if src < 0.5 {
        2.0 * dst * src
    } else {
        1.0 - 2.0 * (1.0 - dst) * (1.0 - src)
    }
}

#[inline]
fn blend_color_dodge(dst: f64, src: f64) -> f64 {
    if dst == 0.0 {
        return 0.0;
    }
    if src == 1.0 {
        return 1.0;
    }
    (dst / (1.0 - src)).min(1.0)
}

#[inline]
fn blend_easy_dodge(dst: f64, src: f64) -> f64 {
    if dst == 0.0 {
        return 0.0;
    }
    if src >= 1.0 {
        return 1.0;
    }
    dst.powf(1.04 / (1.0 - src))
}

#[inline]
fn blend_color_burn(dst: f64, src: f64) -> f64 {
    if dst == 1.0 {
        return 1.0;
    }
    if src == 0.0 {
        return 0.0;
    }
    1.0 - ((1.0 - dst) / src).min(1.0)
}

#[inline]
fn blend_linear_light(dst: f64, src: f64) -> f64 {
    if src < 0.5 {
        dst + 2.0 * src - 1.0
    } else {
        dst + 2.0 * (src - 0.5)
    }
}

#[inline]
fn blend_vivid_light(dst: f64, src: f64) -> f64 {
    if src < 0.5 {
        blend_color_burn(dst, 2.0 * src)
    } else {
        blend_color_dodge(dst, 2.0 * (src - 0.5))
    }
}

#[inline]
fn blend_pin_light(dst: f64, src: f64) -> f64 {
    if src < 0.5 {
        dst.min(2.0 * src)
    } else {
        dst.max(2.0 * (src - 0.5))
    }
}

#[inline]
fn blend_hard_mix(dst: f64, src: f64) -> f64 {
    if blend_vivid_light(dst, src) < 0.5 {
        0.0
    } else {
        1.0
    }
}

#[inline]
fn safe_divide(dst: f64, src: f64) -> f64 {
    if src == 0.0 {
        0.0
    } else {
        (dst / src).min(1.0)
    }
}

#[inline]
fn rgb_to_luma(r: f64, g: f64, b: f64) -> f64 {
    0.2126 * r + 0.7152 * g + 0.0722 * b
}

#[inline]
fn svg_lum(r: f64, g: f64, b: f64) -> f64 {
    0.3 * r + 0.59 * g + 0.11 * b
}

#[inline]
fn svg_sat(r: f64, g: f64, b: f64) -> f64 {
    r.max(g).max(b) - r.min(g).min(b)
}

fn svg_set_sat(r: f64, g: f64, b: f64, sat: f64) -> (f64, f64, f64) {
    // Find min/mid/max
    let (min, mid, max): (f64, f64, f64);
    let (min_ch, mid_ch, max_ch): (u8, u8, u8);

    if r <= g {
        if r <= b {
            min = r; min_ch = 0;
            if g <= b { mid = g; mid_ch = 1; max = b; max_ch = 2; }
            else { mid = b; mid_ch = 2; max = g; max_ch = 1; }
        } else {
            min = b; min_ch = 2; mid = r; mid_ch = 0; max = g; max_ch = 1;
        }
    } else {
        if g <= b {
            min = g; min_ch = 1;
            if r <= b { mid = r; mid_ch = 0; max = b; max_ch = 2; }
            else { mid = b; mid_ch = 2; max = r; max_ch = 0; }
        } else {
            min = b; min_ch = 2; mid = g; mid_ch = 1; max = r; max_ch = 0;
        }
    }

    let (n_mid, n_max) = if max > min {
        (((mid - min) * sat) / (max - min), sat)
    } else {
        (0.0, 0.0)
    };

    let mut nr = r;
    let mut ng = g;
    let mut nb = b;
    match min_ch { 0 => nr = 0.0, 1 => ng = 0.0, _ => nb = 0.0 }
    match mid_ch { 0 => nr = n_mid, 1 => ng = n_mid, _ => nb = n_mid }
    match max_ch { 0 => nr = n_max, 1 => ng = n_max, _ => nb = n_max }
    (nr, ng, nb)
}

fn svg_clip_color(r: f64, g: f64, b: f64) -> (f64, f64, f64) {
    let lum = svg_lum(r, g, b);
    let n = r.min(g).min(b);
    let x = r.max(g).max(b);

    let (mut r, mut g, mut b) = (r, g, b);
    if n < 0.0 {
        let denom = lum - n;
        if denom != 0.0 {
            r = lum + ((r - lum) * lum) / denom;
            g = lum + ((g - lum) * lum) / denom;
            b = lum + ((b - lum) * lum) / denom;
        }
    }
    if x > 1.0 {
        let denom = x - lum;
        if denom != 0.0 {
            r = lum + ((r - lum) * (1.0 - lum)) / denom;
            g = lum + ((g - lum) * (1.0 - lum)) / denom;
            b = lum + ((b - lum) * (1.0 - lum)) / denom;
        }
    }
    (r, g, b)
}

fn svg_set_lum(r: f64, g: f64, b: f64, lum: f64) -> (f64, f64, f64) {
    let d = lum - svg_lum(r, g, b);
    svg_clip_color(r + d, g + d, b + d)
}

fn luminosity_blend(
    dst_r: f64, dst_g: f64, dst_b: f64,
    src_r: f64, src_g: f64, src_b: f64,
    use_darker: bool,
) -> (f64, f64, f64) {
    let dst_lum = rgb_to_luma(dst_r, dst_g, dst_b);
    let src_lum = rgb_to_luma(src_r, src_g, src_b);
    let cmp = if use_darker { src_lum < dst_lum } else { src_lum > dst_lum };
    if cmp { (src_r, src_g, src_b) } else { (dst_r, dst_g, dst_b) }
}

fn hsl_blend(
    dst_r: f64, dst_g: f64, dst_b: f64,
    src_r: f64, src_g: f64, src_b: f64,
    mode: i32,
) -> (f64, f64, f64) {
    match mode {
        0 => {
            // Hue: src hue, dst saturation+luminosity
            let (r, g, b) = svg_set_sat(src_r, src_g, src_b, svg_sat(dst_r, dst_g, dst_b));
            svg_set_lum(r, g, b, svg_lum(dst_r, dst_g, dst_b))
        }
        1 => {
            // Saturation: src saturation, dst hue+luminosity
            let (r, g, b) = svg_set_sat(dst_r, dst_g, dst_b, svg_sat(src_r, src_g, src_b));
            svg_set_lum(r, g, b, svg_lum(dst_r, dst_g, dst_b))
        }
        2 => {
            // Color: src hue+saturation, dst luminosity
            svg_set_lum(src_r, src_g, src_b, svg_lum(dst_r, dst_g, dst_b))
        }
        _ => {
            // Luminosity: src luminosity, dst hue+saturation
            svg_set_lum(dst_r, dst_g, dst_b, svg_lum(src_r, src_g, src_b))
        }
    }
}

// ── Per-pixel blend compositing ────────────────────────────────────────────

/// Apply a blend mode to source and destination colors.
/// Returns the blended color (pre-alpha-composite).
pub fn apply_blend_mode(
    src_r: f64, src_g: f64, src_b: f64, _src_a: f64,
    dst_r: f64, dst_g: f64, dst_b: f64, _dst_a: f64,
    blend_mode: BlendMode,
) -> (f64, f64, f64) {
    match blend_mode {
        BlendMode::Normal | BlendMode::PassThrough | BlendMode::SrcOver => {
            (src_r, src_g, src_b)
        }
        BlendMode::Dissolve => (src_r, src_g, src_b),
        BlendMode::Multiply => (dst_r * src_r, dst_g * src_g, dst_b * src_b),
        BlendMode::Screen => (
            1.0 - (1.0 - dst_r) * (1.0 - src_r),
            1.0 - (1.0 - dst_g) * (1.0 - src_g),
            1.0 - (1.0 - dst_b) * (1.0 - src_b),
        ),
        BlendMode::Overlay => (
            blend_overlay(dst_r, src_r),
            blend_overlay(dst_g, src_g),
            blend_overlay(dst_b, src_b),
        ),
        BlendMode::SoftLight => (
            blend_soft_light(dst_r, src_r),
            blend_soft_light(dst_g, src_g),
            blend_soft_light(dst_b, src_b),
        ),
        BlendMode::HardLight => (
            blend_hard_light(dst_r, src_r),
            blend_hard_light(dst_g, src_g),
            blend_hard_light(dst_b, src_b),
        ),
        BlendMode::ColorDodge => (
            blend_color_dodge(dst_r, src_r),
            blend_color_dodge(dst_g, src_g),
            blend_color_dodge(dst_b, src_b),
        ),
        BlendMode::EasyDodge => (
            blend_easy_dodge(dst_r, src_r),
            blend_easy_dodge(dst_g, src_g),
            blend_easy_dodge(dst_b, src_b),
        ),
        BlendMode::ColorBurn => (
            blend_color_burn(dst_r, src_r),
            blend_color_burn(dst_g, src_g),
            blend_color_burn(dst_b, src_b),
        ),
        BlendMode::Darken => (dst_r.min(src_r), dst_g.min(src_g), dst_b.min(src_b)),
        BlendMode::Lighten => (dst_r.max(src_r), dst_g.max(src_g), dst_b.max(src_b)),
        BlendMode::Difference => (
            (dst_r - src_r).abs(),
            (dst_g - src_g).abs(),
            (dst_b - src_b).abs(),
        ),
        BlendMode::Exclusion => (
            dst_r + src_r - 2.0 * dst_r * src_r,
            dst_g + src_g - 2.0 * dst_g * src_g,
            dst_b + src_b - 2.0 * dst_b * src_b,
        ),
        BlendMode::LinearBurn => (
            dst_r + src_r - 1.0,
            dst_g + src_g - 1.0,
            dst_b + src_b - 1.0,
        ),
        BlendMode::LinearDodge => (dst_r + src_r, dst_g + src_g, dst_b + src_b),
        BlendMode::VividLight => (
            blend_vivid_light(dst_r, src_r),
            blend_vivid_light(dst_g, src_g),
            blend_vivid_light(dst_b, src_b),
        ),
        BlendMode::LinearLight => (
            blend_linear_light(dst_r, src_r),
            blend_linear_light(dst_g, src_g),
            blend_linear_light(dst_b, src_b),
        ),
        BlendMode::PinLight => (
            blend_pin_light(dst_r, src_r),
            blend_pin_light(dst_g, src_g),
            blend_pin_light(dst_b, src_b),
        ),
        BlendMode::HardMix => (
            blend_hard_mix(dst_r, src_r),
            blend_hard_mix(dst_g, src_g),
            blend_hard_mix(dst_b, src_b),
        ),
        BlendMode::DarkerColor => {
            luminosity_blend(dst_r, dst_g, dst_b, src_r, src_g, src_b, true)
        }
        BlendMode::LighterColor => {
            luminosity_blend(dst_r, dst_g, dst_b, src_r, src_g, src_b, false)
        }
        BlendMode::Subtract => (dst_r - src_r, dst_g - src_g, dst_b - src_b),
        BlendMode::Divide => (
            safe_divide(dst_r, src_r),
            safe_divide(dst_g, src_g),
            safe_divide(dst_b, src_b),
        ),
        BlendMode::Hue => hsl_blend(dst_r, dst_g, dst_b, src_r, src_g, src_b, 0),
        BlendMode::Saturation => hsl_blend(dst_r, dst_g, dst_b, src_r, src_g, src_b, 1),
        BlendMode::Color => hsl_blend(dst_r, dst_g, dst_b, src_r, src_g, src_b, 2),
        BlendMode::Luminosity => hsl_blend(dst_r, dst_g, dst_b, src_r, src_g, src_b, 3),
        BlendMode::NormalAlphaPreserving => (src_r, src_g, src_b),
        // Future/unimplemented modes fall back to Normal
        BlendMode::Erase
        | BlendMode::DstOut
        | BlendMode::Clear
        | BlendMode::Pigment
        | BlendMode::PigmentAlpha
        | BlendMode::PigmentAndEraser
        | BlendMode::OklabNormal
        | BlendMode::OklabNormalAndEraser
        | BlendMode::OklabRecolor => (src_r, src_g, src_b),
    }
}

/// Blend a src pixel over a dst pixel (src-over with premultiplied alpha in BGRA layout).
/// `dst` is `&mut [u8; 4]` in [B, G, R, A] order.
/// `src` is `[B, G, R, A]` in straight alpha.
/// `opacity` is layer opacity 0-255.
pub fn blend_pixel_over(
    dst: &mut [u8],
    src: &[u8],
    opacity: u8,
) {
    let raw_a = src[3] as u32;
    if raw_a == 0 {
        return;
    }
    let src_a = (raw_a * opacity as u32 + 127) / 255;
    if src_a == 0 {
        return;
    }

    let dst_a = dst[3] as u32;

    if dst_a == 0 {
        dst[0] = ((src[0] as u32 * src_a + 127) / 255) as u8;
        dst[1] = ((src[1] as u32 * src_a + 127) / 255) as u8;
        dst[2] = ((src[2] as u32 * src_a + 127) / 255) as u8;
        dst[3] = src_a as u8;
        return;
    }

    let dst_cont = (dst_a * (255 - src_a) + 127) / 255;
    let out_a = src_a + dst_cont;
    if out_a == 0 {
        return;
    }
    let half = out_a >> 1;
    dst[0] = ((src[0] as u32 * src_a + dst[0] as u32 * dst_cont + half) / out_a) as u8;
    dst[1] = ((src[1] as u32 * src_a + dst[1] as u32 * dst_cont + half) / out_a) as u8;
    dst[2] = ((src[2] as u32 * src_a + dst[2] as u32 * dst_cont + half) / out_a) as u8;
    dst[3] = out_a as u8;
}

/// Blend a src color into dst color (alpha-preserving, no alpha change).
/// Used for clipping layers.
pub fn blend_color_only(dst: &mut [u8], src_b: u8, src_g: u8, src_r: u8, src_a: u8) {
    let sa = src_a as u32;
    if sa >= 255 {
        dst[0] = src_b;
        dst[1] = src_g;
        dst[2] = src_r;
        return;
    }
    let inv = 255 - sa;
    dst[0] = ((src_b as u32 * sa + dst[0] as u32 * inv + 127) / 255) as u8;
    dst[1] = ((src_g as u32 * sa + dst[1] as u32 * inv + 127) / 255) as u8;
    dst[2] = ((src_r as u32 * sa + dst[2] as u32 * inv + 127) / 255) as u8;
}

/// Float-based blend + composite: `src` (0-1) blended over `dst` (0-1).
/// Returns the new dst color and alpha.
pub fn blend_pixel_float(
    src_r: f64, src_g: f64, src_b: f64, src_a: f64,
    dst_r: f64, dst_g: f64, dst_b: f64, dst_a: f64,
    blend_mode: BlendMode,
) -> (f64, f64, f64, f64) {
    if src_a <= 0.0 {
        return (dst_r, dst_g, dst_b, dst_a);
    }
    let (blend_r, blend_g, blend_b) =
        apply_blend_mode(src_r, src_g, src_b, src_a, dst_r, dst_g, dst_b, dst_a, blend_mode);

    if dst_a <= 0.0 {
        return (
            src_r.clamp(0.0, 1.0),
            src_g.clamp(0.0, 1.0),
            src_b.clamp(0.0, 1.0),
            (src_a).min(1.0),
        );
    }

    let out_a = src_a + dst_a * (1.0 - src_a);
    if out_a <= 0.0 {
        return (0.0, 0.0, 0.0, 0.0);
    }

    let out_r = ((blend_r * src_a + dst_r * dst_a * (1.0 - src_a)) / out_a).clamp(0.0, 1.0);
    let out_g = ((blend_g * src_a + dst_g * dst_a * (1.0 - src_a)) / out_a).clamp(0.0, 1.0);
    let out_b = ((blend_b * src_a + dst_b * dst_a * (1.0 - src_a)) / out_a).clamp(0.0, 1.0);

    (out_r, out_g, out_b, out_a)
}

/// Apply layer color tint: lum-based ink blending.
pub fn apply_layer_color(b: &mut u8, g: &mut u8, r: &mut u8, layer_b: u8, layer_g: u8, layer_r: u8) {
    let lum = (*r as u32 * 299 + *g as u32 * 587 + *b as u32 * 114) / 1000;
    let ink = 255 - lum;
    *b = (lum + (layer_b as u32 * ink) / 255) as u8;
    *g = (lum + (layer_g as u32 * ink) / 255) as u8;
    *r = (lum + (layer_r as u32 * ink) / 255) as u8;
}

/// Expression color modes matching C# ExpressionColorMode.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ExpressionColorMode {
    Color,
    Gray,
    Monochrome,
}

impl ExpressionColorMode {
    /// Returns false if the pixel should be skipped (monochrome threshold).
    pub fn apply_to_source(&self, b: &mut u8, g: &mut u8, r: &mut u8, a: &mut u8) -> bool {
        match self {
            ExpressionColorMode::Color => *a != 0,
            ExpressionColorMode::Gray => {
                let lum = (*r as u32 * 299 + *g as u32 * 587 + *b as u32 * 114) / 1000;
                *b = lum as u8;
                *g = lum as u8;
                *r = lum as u8;
                *a != 0
            }
            ExpressionColorMode::Monochrome => {
                let threshold: u8 = 128;
                if *a < threshold {
                    *a = 0;
                    return false;
                }
                *a = 255;
                let lum = (*r as u32 * 299 + *g as u32 * 587 + *b as u32 * 114) / 1000;
                if lum >= threshold as u32 {
                    *b = 255;
                    *g = 255;
                    *r = 255;
                } else {
                    *b = 0;
                    *g = 0;
                    *r = 0;
                }
                true
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_normal_blend_over_transparent() {
        let mut dst = [0u8, 0, 0, 0]; // BGRA transparent
        let src = [255u8, 0, 0, 255]; // B=255, G=0, R=0, A=255 (blue, opaque)
        blend_pixel_over(&mut dst, &src, 255);
        assert_eq!(dst, [255, 0, 0, 255]); // Fully opaque blue
    }

    #[test]
    fn test_normal_blend_half_opacity() {
        let mut dst = [0u8, 0, 0, 255]; // Black opaque
        let src = [0u8, 255, 0, 255]; // Green opaque, but layer opacity=128
        blend_pixel_over(&mut dst, &src, 128);
        // Half green, half black → (0, 128, 0, 255)
        assert_eq!(dst[1], 128);
        assert_eq!(dst[0], 0);
        assert_eq!(dst[2], 0);
        assert_eq!(dst[3], 255);
    }

    #[test]
    fn test_preserves_alpha() {
        assert!(!BlendMode::Normal.preserves_alpha());
        assert!(BlendMode::Multiply.preserves_alpha());
        assert!(BlendMode::NormalAlphaPreserving.preserves_alpha());
    }

    #[test]
    fn test_has_lut() {
        assert!(BlendMode::Multiply.has_lut());
        assert!(BlendMode::Screen.has_lut());
        assert!(!BlendMode::Normal.has_lut());
        assert!(!BlendMode::DarkerColor.has_lut());
    }

    #[test]
    fn test_is_hsl_mode() {
        assert!(BlendMode::Hue.is_hsl_mode());
        assert!(BlendMode::Saturation.is_hsl_mode());
        assert!(!BlendMode::Normal.is_hsl_mode());
    }

    #[test]
    fn test_lut_multiply() {
        let lut = BlendLut::get_lut(BlendMode::Multiply).unwrap();
        // White * Black = Black
        assert_eq!(lut.data[((255usize) << 8) | 0], 0);
        // White * White = White
        assert_eq!(lut.data[((255usize) << 8) | 255], 255);
    }

    #[test]
    fn test_lut_screen() {
        let lut = BlendLut::get_lut(BlendMode::Screen).unwrap();
        // Screen with white = white
        assert_eq!(lut.data[((255usize) << 8) | 128], 255);
    }

    #[test]
    fn test_expression_color_gray() {
        let mut b = 100u8;
        let mut g = 150u8;
        let mut r = 200u8;
        let mut a = 255u8;
        let ok = ExpressionColorMode::Gray.apply_to_source(&mut b, &mut g, &mut r, &mut a);
        assert!(ok);
        // Should all be the same gray value
        assert_eq!(b, g);
        assert_eq!(g, r);
    }

    #[test]
    fn test_expression_color_monochrome() {
        let mut b = 200u8;
        let mut g = 200u8;
        let mut r = 200u8;
        let mut a = 255u8;
        let ok = ExpressionColorMode::Monochrome.apply_to_source(&mut b, &mut g, &mut r, &mut a);
        assert!(ok);
        assert_eq!(a, 255);
        assert_eq!(b, 255); // White since lum >= 128
    }

    #[test]
    fn test_expression_color_monochrome_below_threshold() {
        let mut b = 50u8;
        let mut g = 50u8;
        let mut r = 50u8;
        let mut a = 50u8;
        let ok = ExpressionColorMode::Monochrome.apply_to_source(&mut b, &mut g, &mut r, &mut a);
        assert!(!ok); // Returned false (skip)
    }
}
