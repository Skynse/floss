//! Unpremultiplied BGRA Normal blend — integer math matches
//! `LayerCompositorPixelOps` in the C# compositor.

const OPAQUE: u32 = 0xFF;

#[inline(always)]
fn scale_alpha(raw_a: u32, opacity: u32) -> u32 {
    if opacity >= OPAQUE {
        raw_a
    } else {
        (raw_a * opacity + 127) / 255
    }
}

#[inline(always)]
fn composite_pixel(dst: &mut [u8; 4], sb: u8, sg: u8, sr: u8, src_a: u32) {
    if src_a == 0 {
        return;
    }
    if src_a >= OPAQUE {
        dst[0] = sb;
        dst[1] = sg;
        dst[2] = sr;
        dst[3] = 255;
        return;
    }

    let inv_src_a = 255 - src_a;
    let dst_a = dst[3] as u32;
    let dst_cont = (dst_a * inv_src_a + 127) / 255;
    let out_a = src_a + dst_cont;
    if out_a == 0 {
        return;
    }
    let half = out_a >> 1;
    let sb = sb as u32;
    let sg = sg as u32;
    let sr = sr as u32;
    dst[0] = ((sb * src_a + dst[0] as u32 * dst_cont + half) / out_a) as u8;
    dst[1] = ((sg * src_a + dst[1] as u32 * dst_cont + half) / out_a) as u8;
    dst[2] = ((sr * src_a + dst[2] as u32 * dst_cont + half) / out_a) as u8;
    dst[3] = out_a as u8;
}

/// Composite `width` contiguous BGRA pixels (src → dst, Normal blend).
pub fn composite_normal_row(dst: &mut [u8], src: &[u8], width: usize, opacity: u32) {
    debug_assert!(dst.len() >= width * 4);
    debug_assert!(src.len() >= width * 4);

    let mut i = 0;
    while i < width {
        let off = i * 4;
        let raw_a = src[off + 3] as u32;
        if raw_a != 0 {
            let src_a = scale_alpha(raw_a, opacity);
            let mut px = [dst[off], dst[off + 1], dst[off + 2], dst[off + 3]];
            composite_pixel(
                &mut px,
                src[off],
                src[off + 1],
                src[off + 2],
                src_a,
            );
            dst[off..off + 4].copy_from_slice(&px);
        }
        i += 1;
    }
}

/// Composite a rectangular BGRA region with independent row strides (bytes).
pub fn composite_normal_bgra_region(
    dst: &mut [u8],
    dst_stride: usize,
    src: &[u8],
    src_stride: usize,
    width: usize,
    height: usize,
    opacity: u32,
) {
    for y in 0..height {
        let dst_row = y * dst_stride;
        let src_row = y * src_stride;
        composite_normal_row(
            &mut dst[dst_row..dst_row + width * 4],
            &src[src_row..src_row + width * 4],
            width,
            opacity,
        );
    }
}

/// Fill `width * height` BGRA pixels with a single premultiplied-clear value (u32 LE).
pub fn clear_bgra_region(dst: &mut [u8], dst_stride: usize, width: usize, height: usize, value: u32) {
    let fill = value.to_le_bytes();
    for y in 0..height {
        let row = y * dst_stride;
        for x in 0..width {
            let off = row + x * 4;
            dst[off..off + 4].copy_from_slice(&fill);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn opaque_src_replaces_dst() {
        let mut dst = [0u8, 0, 0, 0, 255, 0, 0, 128];
        let src = [10u8, 20, 30, 255, 40, 50, 60, 255];
        composite_normal_row(&mut dst, &src, 2, 255);
        assert_eq!(&dst[..4], &[10, 20, 30, 255]);
        assert_eq!(&dst[4..], &[40, 50, 60, 255]);
    }

    #[test]
    fn half_alpha_blends() {
        let mut dst = [100u8, 100, 100, 255];
        let src = [200u8, 0, 0, 128];
        composite_normal_row(&mut dst, &src, 1, 255);
        // srcA=128, dstCont=(255*127+127)/255=127, outA=255
        // outB = (0*128 + 100*127 + 127)/255 ≈ 150
        assert_eq!(dst[3], 255);
        assert!(dst[0] > 100 && dst[0] < 200);
    }

    #[test]
    fn csharp_parity_fixture() {
        let mut dst = [0u8; 16];
        let src = [10u8, 20, 30, 255, 40, 50, 60, 128, 0, 0, 0, 0, 200, 0, 0, 255];
        composite_normal_row(&mut dst, &src, 4, 255);
        assert_eq!(&dst[..4], &[10, 20, 30, 255]);
        assert_eq!(dst[4], 40);
        assert_eq!(dst[5], 50);
        assert_eq!(dst[6], 60);
        assert_eq!(dst[7], 128);
        assert_eq!(&dst[8..12], &[0, 0, 0, 0]);
        assert_eq!(&dst[12..], &[200, 0, 0, 255]);
    }

    #[test]
    fn opacity_scales_source() {
        let mut dst = [0u8; 4];
        let src = [255u8, 255, 255, 255];
        composite_normal_row(&mut dst, &src, 1, 128);
        assert_eq!(dst[3], 128);
    }
}
