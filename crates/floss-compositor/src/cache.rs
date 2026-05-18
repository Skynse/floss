//! GPU tile cache — tracks which layer tiles are resident on the GPU.
//!
//! Each layer has its tiles stored as wgpu textures in a texture atlas.
//! The cache tracks dirty tiles that need re-uploading and manages
//! texture atlas allocation/deallocation.

use std::collections::HashMap;

use floss_core::Rect;

/// Tile coordinate (uniquely identifies a tile within a layer).
pub type TileCoord = (i32, i32);

/// 64×64 tile size, matches `TiledPixelBuffer::TILE_SIZE`.
pub const TILE_SIZE: u32 = 64;
/// Composite tile size (for GPU composite pass tiles). Larger = fewer draw calls.
pub const COMP_TILE_SIZE: u32 = 1024;

/// Status of a tile in the GPU cache.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TileStatus {
    /// Tile is not on the GPU.
    Absent,
    /// Tile is on the GPU and up-to-date with CPU data.
    Clean,
    /// Tile is on the GPU but the CPU data has changed — needs re-upload.
    Dirty,
}

/// Entry in the GPU tile cache.
#[derive(Debug, Clone)]
pub struct GpuTileEntry {
    /// Status of this tile.
    pub status: TileStatus,
    /// Texture atlas slot index (if resident).
    pub atlas_slot: Option<usize>,
    /// Generation counter for LRU eviction.
    pub last_used: u64,
}

/// GPU tile cache for a single layer.
pub struct GpuTileCache {
    /// Tiles keyed by coordinate.
    tiles: HashMap<TileCoord, GpuTileEntry>,
    /// Monotonic generation counter.
    generation: u64,
    /// Max texture atlas slots.
    max_slots: usize,
}

impl GpuTileCache {
    pub fn new(max_slots: usize) -> Self {
        Self {
            tiles: HashMap::new(),
            generation: 0,
            max_slots,
        }
    }

    /// Mark a tile as dirty (needs re-upload). Creates the entry if absent.
    pub fn mark_dirty(&mut self, coord: TileCoord) {
        let entry = self.tiles.entry(coord).or_insert(GpuTileEntry {
            status: TileStatus::Absent,
            atlas_slot: None,
            last_used: 0,
        });
        if entry.status == TileStatus::Clean {
            entry.status = TileStatus::Dirty;
        }
    }

    /// Mark a region of tiles as dirty.
    pub fn mark_region_dirty(&mut self, region: Rect) {
        if region.is_empty() {
            return;
        }
        let first_tx = tile_coord(region.x);
        let first_ty = tile_coord(region.y);
        let last_tx = tile_coord(region.right() - 1);
        let last_ty = tile_coord(region.bottom() - 1);
        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                self.mark_dirty((tx, ty));
            }
        }
    }

    /// Mark a tile as clean (uploaded to GPU).
    pub fn mark_clean(&mut self, coord: TileCoord, atlas_slot: usize) {
        let entry = self.tiles.entry(coord).or_insert(GpuTileEntry {
            status: TileStatus::Clean,
            atlas_slot: Some(atlas_slot),
            last_used: self.generation,
        });
        entry.status = TileStatus::Clean;
        entry.atlas_slot = Some(atlas_slot);
        entry.last_used = self.generation;
    }

    /// Get all dirty tiles that need uploading.
    pub fn dirty_tiles(&self) -> Vec<(TileCoord, Option<usize>)> {
        self.tiles
            .iter()
            .filter(|(_, e)| e.status == TileStatus::Dirty)
            .map(|(&coord, e)| (coord, e.atlas_slot))
            .collect()
    }

    /// Get all resident tile coordinates (clean or dirty).
    pub fn resident_tiles(&self) -> Vec<TileCoord> {
        self.tiles
            .iter()
            .filter(|(_, e)| e.status != TileStatus::Absent && e.atlas_slot.is_some())
            .map(|(&coord, _)| coord)
            .collect()
    }

    /// Bump the generation counter (called at the start of each frame).
    pub fn begin_frame(&mut self) {
        self.generation = self.generation.wrapping_add(1);
    }

    /// Evict least-recently-used tiles to free up atlas slots.
    /// Returns the coordinates of evicted tiles.
    pub fn evict_lru(&mut self, target_free: usize) -> Vec<TileCoord> {
        let mut entries: Vec<_> = self
            .tiles
            .iter()
            .filter(|(_, e)| e.atlas_slot.is_some())
            .map(|(&c, e)| (c, e.last_used))
            .collect();
        entries.sort_by_key(|(_, generation)| *generation);

        let mut evicted = Vec::new();
        let current_count = entries.len();
        let to_evict = (current_count + target_free).saturating_sub(self.max_slots);

        for (coord, _) in entries.iter().take(to_evict) {
            if let Some(entry) = self.tiles.get_mut(coord) {
                entry.status = TileStatus::Absent;
                entry.atlas_slot = None;
                evicted.push(*coord);
            }
        }
        evicted
    }

    /// Remove all tiles from the cache.
    pub fn clear(&mut self) {
        self.tiles.clear();
    }
}

/// Composite tile coordinate (for the composite pass, uses larger tiles).
pub type CompTileCoord = (i32, i32);

/// Tracks which composite tiles need re-rendering.
pub struct CompositeTileTracker {
    dirty: HashMap<CompTileCoord, ()>,
    width: i32,
    height: i32,
}

impl CompositeTileTracker {
    pub fn new(width: i32, height: i32) -> Self {
        let mut dirty = HashMap::new();
        // Initially all tiles are dirty
        let last_tx = (width - 1) / COMP_TILE_SIZE as i32;
        let last_ty = (height - 1) / COMP_TILE_SIZE as i32;
        for ty in 0..=last_ty {
            for tx in 0..=last_tx {
                dirty.insert((tx, ty), ());
            }
        }
        Self {
            dirty,
            width,
            height,
        }
    }

    pub fn resize(&mut self, width: i32, height: i32) {
        self.width = width;
        self.height = height;
        self.dirty.clear();
        let last_tx = (width - 1) / COMP_TILE_SIZE as i32;
        let last_ty = (height - 1) / COMP_TILE_SIZE as i32;
        for ty in 0..=last_ty {
            for tx in 0..=last_tx {
                self.dirty.insert((tx, ty), ());
            }
        }
    }

    /// Mark a document-space region as needing composite re-render.
    pub fn invalidate_region(&mut self, region: Rect) {
        if region.is_empty() {
            return;
        }
        let first_tx = region.x / COMP_TILE_SIZE as i32;
        let first_ty = region.y / COMP_TILE_SIZE as i32;
        let last_tx = (region.right() - 1) / COMP_TILE_SIZE as i32;
        let last_ty = (region.bottom() - 1) / COMP_TILE_SIZE as i32;

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                if tx >= 0 && ty >= 0 {
                    self.dirty.insert((tx, ty), ());
                }
            }
        }
    }

    pub fn is_dirty(&self, coord: CompTileCoord) -> bool {
        self.dirty.contains_key(&coord)
    }

    pub fn mark_clean(&mut self, coord: CompTileCoord) {
        self.dirty.remove(&coord);
    }

    pub fn dirty_tiles(&self) -> Vec<CompTileCoord> {
        self.dirty.keys().copied().collect()
    }

    pub fn all_dirty(&mut self) {
        self.dirty.clear();
        let last_tx = (self.width - 1) / COMP_TILE_SIZE as i32;
        let last_ty = (self.height - 1) / COMP_TILE_SIZE as i32;
        for ty in 0..=last_ty {
            for tx in 0..=last_tx {
                self.dirty.insert((tx, ty), ());
            }
        }
    }

    pub fn width(&self) -> i32 {
        self.width
    }
    pub fn height(&self) -> i32 {
        self.height
    }
}

#[inline]
fn tile_coord(pixel: i32) -> i32 {
    let tile = pixel / TILE_SIZE as i32;
    if pixel < 0 && pixel % TILE_SIZE as i32 != 0 {
        tile - 1
    } else {
        tile
    }
}
