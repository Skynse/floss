use glam::Vec4;
use serde::{Deserialize, Serialize};

/// Premultiplied RGBA color (straight alpha, 0–1).
///
/// Stored as premultiplied values internally for correct compositing,
/// but constructors and accessors work with straight values.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct Color {
    /// Premultiplied R, G, B, A
    inner: Vec4,
}

impl Color {
    /// Opaque black.
    pub const BLACK: Self = Self::from_rgba(0.0, 0.0, 0.0, 1.0);
    /// Opaque white.
    pub const WHITE: Self = Self::from_rgba(1.0, 1.0, 1.0, 1.0);
    /// Fully transparent black.
    pub const TRANSPARENT: Self = Self::from_rgba(0.0, 0.0, 0.0, 0.0);

    /// Create from straight (non-premultiplied) RGBA values in 0–1.
    #[inline]
    pub const fn from_rgba(r: f32, g: f32, b: f32, a: f32) -> Self {
        Self {
            inner: Vec4::new(r * a, g * a, b * a, a),
        }
    }

    /// Create from premultiplied RGBA values in 0–1.
    #[inline]
    pub const fn from_rgba_premul(r: f32, g: f32, b: f32, a: f32) -> Self {
        Self {
            inner: Vec4::new(r, g, b, a),
        }
    }

    /// Create from 8-bit sRGB bytes (straight alpha).
    #[inline]
    pub fn from_bytes(r: u8, g: u8, b: u8, a: u8) -> Self {
        Self::from_rgba(
            r as f32 / 255.0,
            g as f32 / 255.0,
            b as f32 / 255.0,
            a as f32 / 255.0,
        )
    }

    /// Premultiplied red channel.
    #[inline]
    pub fn r_premul(&self) -> f32 {
        self.inner.x
    }
    /// Premultiplied green channel.
    #[inline]
    pub fn g_premul(&self) -> f32 {
        self.inner.y
    }
    /// Premultiplied blue channel.
    #[inline]
    pub fn b_premul(&self) -> f32 {
        self.inner.z
    }
    /// Alpha channel (0 = transparent, 1 = opaque).
    #[inline]
    pub fn a(&self) -> f32 {
        self.inner.w
    }

    /// Straight (un-premultiplied) red channel.
    #[inline]
    pub fn r(&self) -> f32 {
        if self.inner.w < 0.001 {
            0.0
        } else {
            self.inner.x / self.inner.w
        }
    }
    /// Straight green channel.
    #[inline]
    pub fn g(&self) -> f32 {
        if self.inner.w < 0.001 {
            0.0
        } else {
            self.inner.y / self.inner.w
        }
    }
    /// Straight blue channel.
    #[inline]
    pub fn b(&self) -> f32 {
        if self.inner.w < 0.001 {
            0.0
        } else {
            self.inner.z / self.inner.w
        }
    }

    /// Premultiplied RGBA as [f32; 4].
    #[inline]
    pub fn as_premul_array(&self) -> [f32; 4] {
        self.inner.to_array()
    }

    /// 8-bit sRGB bytes [R, G, B, A] (straight alpha).
    #[inline]
    pub fn as_bytes(&self) -> [u8; 4] {
        [
            (Self::linear_to_srgb(self.r()) * 255.0 + 0.5) as u8,
            (Self::linear_to_srgb(self.g()) * 255.0 + 0.5) as u8,
            (Self::linear_to_srgb(self.b()) * 255.0 + 0.5) as u8,
            (self.a() * 255.0 + 0.5) as u8,
        ]
    }

    /// Composite `src` over `self` using standard over operator
    /// (both colors are premultiplied internally).
    #[inline]
    pub fn blend_over(&self, src: Color) -> Color {
        let inv_a = 1.0 - src.a();
        Color {
            inner: Vec4::new(
                src.inner.x + self.inner.x * inv_a,
                src.inner.y + self.inner.y * inv_a,
                src.inner.z + self.inner.z * inv_a,
                src.a() + self.a() * inv_a,
            ),
        }
    }

    /// Lerp between two colors (straight-alpha interpolation).
    #[inline]
    pub fn lerp(&self, other: Color, t: f32) -> Color {
        let t = t.clamp(0.0, 1.0);
        Color::from_rgba(
            self.r() + (other.r() - self.r()) * t,
            self.g() + (other.g() - self.g()) * t,
            self.b() + (other.b() - self.b()) * t,
            self.a() + (other.a() - self.a()) * t,
        )
    }

    fn linear_to_srgb(c: f32) -> f32 {
        if c <= 0.0031308 {
            12.92 * c
        } else {
            1.055 * c.powf(1.0 / 2.4) - 0.055
        }
    }
}

impl Default for Color {
    fn default() -> Self {
        Self::TRANSPARENT
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn black_is_opaque() {
        assert_eq!(Color::BLACK.a(), 1.0);
        assert_eq!(Color::BLACK.as_bytes(), [0, 0, 0, 255]);
    }

    #[test]
    fn transparent_blend_over_returns_src() {
        let src = Color::from_rgba(0.5, 0.25, 0.125, 0.5);
        let result = Color::TRANSPARENT.blend_over(src);
        assert!((result.r() - 0.5).abs() < 0.01);
        assert!((result.a() - 0.5).abs() < 0.01);
    }

    #[test]
    fn opaque_blend_over_hides_background() {
        let bg = Color::from_rgba(0.0, 1.0, 0.0, 1.0);
        let fg = Color::from_rgba(1.0, 0.0, 0.0, 1.0);
        let result = bg.blend_over(fg);
        assert!((result.r() - 1.0).abs() < 0.01);
    }

    #[test]
    fn lerp_midpoint() {
        let a = Color::from_rgba(0.0, 0.0, 0.0, 0.0);
        let b = Color::from_rgba(1.0, 1.0, 1.0, 1.0);
        let mid = a.lerp(b, 0.5);
        assert!((mid.r() - 0.5).abs() < 0.01);
        assert!((mid.a() - 0.5).abs() < 0.01);
    }
}
