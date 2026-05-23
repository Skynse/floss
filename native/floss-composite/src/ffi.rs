use crate::blend;

pub const FLOSS_COMPOSITE_VERSION: u32 = 1;

/// Returns ABI version — bump when FFI signatures change.
#[no_mangle]
pub extern "C" fn floss_composite_version() -> u32 {
    FLOSS_COMPOSITE_VERSION
}

/// Normal-blend `width` contiguous BGRA pixels from `src` onto `dst`.
///
/// # Safety
/// `dst` and `src` must point to at least `width * 4` valid bytes.
/// `opacity` is 0–255 (255 = full layer opacity).
#[no_mangle]
pub unsafe extern "C" fn floss_composite_normal_row(
    dst: *mut u8,
    src: *const u8,
    width: i32,
    opacity: u32,
) {
    if dst.is_null() || src.is_null() || width <= 0 {
        return;
    }
    let width = width as usize;
    let dst_slice = std::slice::from_raw_parts_mut(dst, width * 4);
    let src_slice = std::slice::from_raw_parts(src, width * 4);
    blend::composite_normal_row(dst_slice, src_slice, width, opacity);
}

/// Normal-blend a BGRA rectangle with independent row strides (bytes).
///
/// # Safety
/// `dst` must span `height` rows of `dst_stride` bytes; `src` must span
/// `height` rows of `src_stride` bytes; each row holds `width` pixels.
#[no_mangle]
pub unsafe extern "C" fn floss_composite_normal_bgra_region(
    dst: *mut u8,
    dst_stride: i32,
    src: *const u8,
    src_stride: i32,
    width: i32,
    height: i32,
    opacity: u32,
) {
    if dst.is_null()
        || src.is_null()
        || dst_stride <= 0
        || src_stride <= 0
        || width <= 0
        || height <= 0
    {
        return;
    }
    let (width, height) = (width as usize, height as usize);
    let dst_stride = dst_stride as usize;
    let src_stride = src_stride as usize;
    let dst_len = dst_stride * height;
    let src_len = src_stride * height;
    let dst_slice = std::slice::from_raw_parts_mut(dst, dst_len);
    let src_slice = std::slice::from_raw_parts(src, src_len);
    blend::composite_normal_bgra_region(
        dst_slice,
        dst_stride,
        src_slice,
        src_stride,
        width,
        height,
        opacity,
    );
}

/// Fill a BGRA rectangle with `clear_value` (0xAABBGGRR little-endian).
#[no_mangle]
pub unsafe extern "C" fn floss_clear_bgra_region(
    dst: *mut u8,
    dst_stride: i32,
    width: i32,
    height: i32,
    clear_value: u32,
) {
    if dst.is_null() || dst_stride <= 0 || width <= 0 || height <= 0 {
        return;
    }
    let (width, height) = (width as usize, height as usize);
    let dst_stride = dst_stride as usize;
    let dst_slice = std::slice::from_raw_parts_mut(dst, dst_stride * height);
    blend::clear_bgra_region(dst_slice, dst_stride, width, height, clear_value);
}
