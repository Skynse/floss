mod blend;
mod ffi;

pub use blend::{clear_bgra_region, composite_normal_bgra_region, composite_normal_row};
pub use ffi::FLOSS_COMPOSITE_VERSION;
