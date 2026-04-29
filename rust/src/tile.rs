use std::collections::HashSet;

use crate::geometry::CanvasRect;

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct TileCoord {
    pub x: u32,
    pub y: u32,
}

#[derive(Clone, Debug)]
pub struct TileGrid {
    width: u32,
    height: u32,
    tile_size: u32,
    dirty_tiles: HashSet<TileCoord>,
}

impl TileGrid {
    pub fn new(width: u32, height: u32, tile_size: u32) -> Self {
        Self {
            width,
            height,
            tile_size: tile_size.max(1),
            dirty_tiles: HashSet::new(),
        }
    }

    pub fn columns(&self) -> u32 {
        self.width.div_ceil(self.tile_size)
    }

    pub fn rows(&self) -> u32 {
        self.height.div_ceil(self.tile_size)
    }

    pub fn mark_rect_dirty(&mut self, rect: CanvasRect) {
        if rect.is_empty() {
            return;
        }

        let max_x = self.columns().saturating_sub(1);
        let max_y = self.rows().saturating_sub(1);
        let min_tile_x = floor_to_tile(rect.left, self.tile_size).clamp(0, max_x);
        let min_tile_y = floor_to_tile(rect.top, self.tile_size).clamp(0, max_y);
        let max_tile_x = floor_to_tile(rect.right, self.tile_size).clamp(0, max_x);
        let max_tile_y = floor_to_tile(rect.bottom, self.tile_size).clamp(0, max_y);

        for y in min_tile_y..=max_tile_y {
            for x in min_tile_x..=max_tile_x {
                self.dirty_tiles.insert(TileCoord { x, y });
            }
        }
    }

    pub fn mark_all_dirty(&mut self) {
        for y in 0..self.rows() {
            for x in 0..self.columns() {
                self.dirty_tiles.insert(TileCoord { x, y });
            }
        }
    }

    pub fn dirty_count(&self) -> usize {
        self.dirty_tiles.len()
    }

    pub fn drain_dirty_tiles(&mut self) -> Vec<TileCoord> {
        self.dirty_tiles.drain().collect()
    }
}

fn floor_to_tile(value: f32, tile_size: u32) -> u32 {
    if value <= 0.0 {
        0
    } else {
        (value as u32) / tile_size
    }
}
