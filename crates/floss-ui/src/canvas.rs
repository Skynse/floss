use std::sync::Arc;

use peniko::{
    Blob, ImageAlphaType, ImageBrush, ImageData, ImageFormat, ImageQuality,
};
use sha2::{Digest, Sha256};

pub fn make_image_brush(rgba: &[u8], w: u32, h: u32) -> ImageBrush {
    let data = Arc::new(rgba.to_vec());
    let blob = Blob::new(data);
    ImageBrush::new(ImageData {
        data: blob,
        format: ImageFormat::Rgba8,
        alpha_type: ImageAlphaType::Alpha,
        width: w,
        height: h,
    })
    .with_quality(ImageQuality::Low)
}

pub fn hash_pixels(rgba: &[u8]) -> Vec<u8> {
    let mut hasher = Sha256::new();
    hasher.update(rgba);
    hasher.finalize().to_vec()
}
