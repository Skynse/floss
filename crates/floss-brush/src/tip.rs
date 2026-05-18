//! Brush tip trait and implementations.
//!
//! A brush tip generates an alpha8 mask at a given size and hardness.
//! Implementations: `ProceduralBrushTip` (math-based shapes),
//! `ImageBrushTip` (PNG-based tips).

use crate::types::BrushTipShape;

/// A generated alpha8 stamp mask.
#[derive(Clone)]
pub struct StampMask {
    /// Width in pixels.
    pub width: usize,
    /// Height in pixels.
    pub height: usize,
    /// Alpha8 pixel data (0 = transparent, 255 = opaque).
    /// Row-major: pixel (x, y) = data[y * width + x].
    pub data: Vec<u8>,
}

/// Trait for objects that can generate brush tip masks.
pub trait BrushTip: Send + Sync {
    /// Generate an alpha8 mask at the given base size and hardness [0.001, 1.0].
    ///
    /// Takes `&self` — caching is handled by the caller (BrushEngine).
    fn generate_mask(&self, base_size: i32, hardness: f32) -> StampMask;
}

// ── ProceduralBrushTip ────────────────────────────────────────────────────

/// A procedurally-generated brush tip (circle, soft round, ellipse, chalk, bristle, etc.).
pub struct ProceduralBrushTip {
    pub shape: BrushTipShape,
    /// Aspect ratio (width/height for Ellipse; ignored for Circle).
    pub aspect_ratio: f32,
}

impl ProceduralBrushTip {
    pub fn new(shape: BrushTipShape, aspect_ratio: f32) -> Self {
        Self {
            shape,
            aspect_ratio,
        }
    }
}

impl BrushTip for ProceduralBrushTip {
    fn generate_mask(&self, base_size: i32, hardness: f32) -> StampMask {
        let size = base_size.max(1);
        let h = hardness.clamp(0.001, 1.0);

        match self.shape {
            BrushTipShape::Chalk => generate_chalk(size, h),
            BrushTipShape::Bristle => generate_bristle(size, h),
            BrushTipShape::Scatter => generate_scatter(size, h),
            _ => generate_smooth(size, h, self.shape, self.aspect_ratio),
        }
    }
}

// ── ImageBrushTip ─────────────────────────────────────────────────────────

/// A brush tip loaded from a PNG image.
pub struct ImageBrushTip {
    /// Raw PNG bytes.
    png_bytes: Vec<u8>,
    /// Decoded RGBA source pixels.
    source_rgba: Vec<u8>,
    source_w: usize,
    source_h: usize,
    /// Whether the source has useful alpha variation.
    source_has_useful_alpha: bool,
}

impl ImageBrushTip {
    pub fn from_png_bytes(png_bytes: Vec<u8>) -> Result<Self, String> {
        let img = image::load_from_memory(&png_bytes)
            .map_err(|e| format!("Failed to decode PNG: {}", e))?;
        let rgba = img.to_rgba8();
        let (w, h) = rgba.dimensions();
        let source_rgba = rgba.into_raw();
        let has_alpha = source_rgba.chunks(4).any(|px| px[3] > 0 && px[3] < 255);

        Ok(Self {
            png_bytes,
            source_rgba,
            source_w: w as usize,
            source_h: h as usize,
            source_has_useful_alpha: has_alpha,
        })
    }

    pub fn png_bytes(&self) -> &[u8] {
        &self.png_bytes
    }

}

impl BrushTip for ImageBrushTip {
    fn generate_mask(&self, base_size: i32, hardness: f32) -> StampMask {
        let size = base_size.max(1);
        let h = hardness.clamp(0.001, 1.0);

        // Scale source preserving aspect ratio
        let src_aspect = self.source_w as f32 / self.source_h as f32;
        let (mask_w, mask_h) = if src_aspect >= 1.0 {
            (size as usize, (size as f32 / src_aspect).max(1.0) as usize)
        } else {
            ((size as f32 * src_aspect).max(1.0) as usize, size as usize)
        };

        let mut data = vec![0u8; mask_w * mask_h];

        for y in 0..mask_h {
            let src_y = (y as f32 / mask_h as f32 * self.source_h as f32) as usize;
            let src_y = src_y.min(self.source_h - 1);
            for x in 0..mask_w {
                let src_x = (x as f32 / mask_w as f32 * self.source_w as f32) as usize;
                let src_x = src_x.min(self.source_w - 1);
                let src_off = (src_y * self.source_w + src_x) * 4;
                let src_a = self.source_rgba[src_off + 3];
                // Apply hardness as a contrast adjustment to alpha
                let a: u8 = if self.source_has_useful_alpha {
                    apply_hardness_to_alpha(src_a as f32, h)
                } else {
                    let luma = (self.source_rgba[src_off] as f32 * 0.299
                        + self.source_rgba[src_off + 1] as f32 * 0.587
                        + self.source_rgba[src_off + 2] as f32 * 0.114)
                        / 255.0;
                    (luma * 255.0).clamp(0.0, 255.0) as u8
                };
                data[y * mask_w + x] = a;
            }
        }

        StampMask {
            width: mask_w,
            height: mask_h,
            data,
        }
    }
}

// ── Mask generation helpers ───────────────────────────────────────────────

fn apply_hardness_to_alpha(alpha: f32, hardness: f32) -> u8 {
    // Hardness sharpens the alpha gradient: alpha^hardness
    let a = alpha / 255.0;
    let sharp = a.powf(hardness);
    (sharp * 255.0).round().clamp(0.0, 255.0) as u8
}

fn generate_smooth(size: i32, hardness: f32, shape: BrushTipShape, aspect_ratio: f32) -> StampMask {
    let s = size as usize;
    let cx = s as f32 * 0.5;
    let cy = s as f32 * 0.5;
    let max_r = s as f32 * 0.5 - 0.5;

    let rx = if aspect_ratio >= 1.0 { max_r } else { max_r * aspect_ratio };
    let ry = if aspect_ratio >= 1.0 { max_r / aspect_ratio } else { max_r };

    let mut data = vec![0u8; s * s];

    match shape {
        BrushTipShape::Circle | BrushTipShape::SoftRound | BrushTipShape::Ellipse => {
            for y in 0..s {
                for x in 0..s {
                    let dx = (x as f32 - cx) / rx;
                    let dy = (y as f32 - cy) / ry;
                    let dist = (dx * dx + dy * dy).sqrt();
                    let a = smoothstep(1.0, hardness, dist);
                    data[y * s + x] = (a * 255.0).round() as u8;
                }
            }
        }
        BrushTipShape::Flat | BrushTipShape::Rectangle => {
            // Rectangle with rounded corners — treat as radial falloff from center
            for y in 0..s {
                for x in 0..s {
                    let dx = ((x as f32 - cx) / rx).abs();
                    let dy = ((y as f32 - cy) / ry).abs();
                    let dist = dx.max(dy);
                    let a = smoothstep(1.0, hardness, dist);
                    data[y * s + x] = (a * 255.0).round() as u8;
                }
            }
        }
        _ => {
            // Default: circle
            for y in 0..s {
                for x in 0..s {
                    let dx = (x as f32 - cx) / max_r;
                    let dy = (y as f32 - cy) / max_r;
                    let dist = (dx * dx + dy * dy).sqrt();
                    let a = smoothstep(1.0, hardness, dist);
                    data[y * s + x] = (a * 255.0).round() as u8;
                }
            }
        }
    }

    StampMask {
        width: s,
        height: s,
        data,
    }
}

fn generate_chalk(size: i32, _hardness: f32) -> StampMask {
    let s = size as usize;
    let cx = s as f32 * 0.5;
    let max_r = s as f32 * 0.5 - 0.5;
    let mut data = vec![0u8; s * s];

    // Chalk: noisy alpha with hard cutoff
    for y in 0..s {
        for x in 0..s {
            let dx = (x as f32 - cx) / max_r;
            let dy = (y as f32 - cx) / max_r;
            let dist = (dx * dx + dy * dy).sqrt();
            let noise = pseudo_random(x as u32, y as u32) * 0.4;
            let a = if dist + noise < 1.0 { 255u8 } else { 0u8 };
            data[y * s + x] = a;
        }
    }

    StampMask {
        width: s,
        height: s,
        data,
    }
}

fn generate_bristle(size: i32, _hardness: f32) -> StampMask {
    let s = size as usize;
    let cx = s as f32 * 0.5;
    let max_r = s as f32 * 0.5 - 0.5;
    let mut data = vec![0u8; s * s];

    // Bristle: lines radiating from center
    let bristle_count = 12;
    for y in 0..s {
        for x in 0..s {
            let dx = x as f32 - cx;
            let dy = y as f32 - cx;
            let dist = (dx * dx + dy * dy).sqrt();
            if dist > max_r {
                continue;
            }
            let angle = dy.atan2(dx);
            let bristle_idx = ((angle / std::f32::consts::TAU * bristle_count as f32).round() as i32)
                .rem_euclid(bristle_count);
            let bristle_angle = bristle_idx as f32 / bristle_count as f32 * std::f32::consts::TAU;
            let angle_diff = (angle - bristle_angle).abs();
            let a = if angle_diff < 0.3 && dist < max_r * 0.8 {
                255u8
            } else {
                0u8
            };
            data[y * s + x] = a;
        }
    }

    StampMask {
        width: s,
        height: s,
        data,
    }
}

fn generate_scatter(size: i32, _hardness: f32) -> StampMask {
    let s = size as usize;
    let cx = s as f32 * 0.5;
    let max_r = s as f32 * 0.5 - 0.5;
    let mut data = vec![0u8; s * s];

    // Scatter: random dots within the brush circle
    for y in 0..s {
        for x in 0..s {
            let dx = (x as f32 - cx) / max_r;
            let dy = (y as f32 - cx) / max_r;
            let dist = (dx * dx + dy * dy).sqrt();
            let noise = pseudo_random(x as u32 * 31337 + y as u32, 0);
            let a = if dist < 1.0 && noise > 0.6 { 255u8 } else { 0u8 };
            data[y * s + x] = a;
        }
    }

    StampMask {
        width: s,
        height: s,
        data,
    }
}

// ── Utilities ─────────────────────────────────────────────────────────────

/// Smoothstep-like falloff: 1.0 at center, 0.0 at edge, with hardness controlling
/// the transition width.
#[inline]
fn smoothstep(edge0: f32, edge1: f32, x: f32) -> f32 {
    let t = ((x - edge0) / (edge1 - edge0 + 0.0001)).clamp(0.0, 1.0);
    1.0 - t * t * (3.0 - 2.0 * t)
}

/// Simple pseudorandom in [0, 1].
#[inline]
fn pseudo_random(x: u32, y: u32) -> f32 {
    let h = x.wrapping_mul(374761393).wrapping_add(y.wrapping_mul(668265263));
    let h = h.wrapping_mul(1274126177) ^ h;
    h as f32 / u32::MAX as f32
}
