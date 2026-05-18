//! GPU-accelerated compositor for the Floss canvas.
//!
//! Manages GPU tile caches for each layer, determines dirty regions,
//! and provides the rendering recipe for gpui to execute.
//!
//! ## Architecture
//!
//! 1. **Layer tiles** are 64×64 pixel tiles stored as GPU textures.
//!    When the CPU-side `TiledPixelBuffer` changes, corresponding GPU
//!    tiles are marked dirty and re-uploaded.
//!
//! 2. **Composite pass** — larger 1024×1024 "composite tiles" cache
//!    the result of compositing all visible layer tiles. Only dirty
//!    composite tiles are re-rendered each frame.
//!
//! 3. **Brush stamp pass** — a WGSL compute shader stamps brush
//!    masks directly into the active layer's tiles on the GPU.
//!
//! 4. **Background** — a checkerboard pattern rendered behind
//!    transparent regions.
//!
//! The `Compositor` struct is a pure data model — the actual GPU
//! dispatch is performed by the gpui app using the recipes provided here.

pub mod cache;
pub mod compositor;
pub mod shaders;

pub use cache::{CompTileCoord, CompositeTileTracker, GpuTileCache, TileCoord, COMP_TILE_SIZE, TILE_SIZE};
pub use compositor::{CompositeBlendMode, Compositor, LayerCompositeInfo};
pub use shaders::{BRUSH_STAMP_SHADER, CHECKERBOARD_SHADER, COMPOSITE_SHADER};
