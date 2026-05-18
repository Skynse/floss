//! Selection mask — a sparse bitmap that restricts painting to a region.
//!
//! A selection mask is stored as a tiled bitmap (same tile size as pixel data).
//! Each tile is a `[u8; TILE_BYTES]` where each pixel's alpha channel indicates
//! selection strength (0 = not selected, 255 = fully selected).

use std::collections::HashMap;

use floss_core::Rect;



const TILE_SIZE: i32 = 64;
const TILE_BYTES: usize = (TILE_SIZE as usize) * (TILE_SIZE as usize) * 4;

/// A selection mask using the same sparse tiled storage as pixel layers.
pub struct SelectionMask {
    tiles: HashMap<(i32, i32), Box<[u8; TILE_BYTES]>>,
    /// Tight bounding box of the selection.
    bounds: Rect,
}

impl SelectionMask {
    /// Create an empty selection.
    pub fn empty() -> Self {
        Self {
            tiles: HashMap::new(),
            bounds: Rect::ZERO,
        }
    }

    /// Create a rectangular selection.
    pub fn from_rect(rect: Rect) -> Self {
        let mut mask = Self::empty();
        mask.add_rect(rect);
        mask
    }

    /// Add a filled rectangle to the selection (union).
    pub fn add_rect(&mut self, rect: Rect) {
        if rect.is_empty() {
            return;
        }
        let first_tx = tile_coord(rect.x);
        let first_ty = tile_coord(rect.y);
        let last_tx = tile_coord(rect.right() - 1);
        let last_ty = tile_coord(rect.bottom() - 1);

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                let tile_rect = Rect::new(
                    tx * TILE_SIZE,
                    ty * TILE_SIZE,
                    TILE_SIZE,
                    TILE_SIZE,
                );
                let clipped = tile_rect.intersect(rect);
                if clipped.is_empty() {
                    continue;
                }

                let tile = self.tiles.entry((tx, ty)).or_insert_with(|| {
                    Box::new([0u8; TILE_BYTES])
                });

                for py in clipped.y..clipped.bottom() {
                    let ty_off = py.rem_euclid(TILE_SIZE) as usize;
                    let tx_start = clipped.x.rem_euclid(TILE_SIZE) as usize;
                    let row_off = (ty_off * TILE_SIZE as usize + tx_start) * 4;
                    for px in 0..clipped.w as usize {
                        tile[row_off + px * 4 + 3] = 255;
                    }
                }
            }
        }

        self.bounds = if self.bounds.is_empty() {
            rect
        } else {
            self.bounds.union(rect)
        };
    }

    /// Remove a rectangle from the selection (subtract).
    pub fn subtract_rect(&mut self, rect: Rect) {
        if rect.is_empty() || self.is_empty() {
            return;
        }
        let first_tx = tile_coord(rect.x);
        let first_ty = tile_coord(rect.y);
        let last_tx = tile_coord(rect.right() - 1);
        let last_ty = tile_coord(rect.bottom() - 1);

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                let tile_rect = Rect::new(
                    tx * TILE_SIZE,
                    ty * TILE_SIZE,
                    TILE_SIZE,
                    TILE_SIZE,
                );
                let clipped = tile_rect.intersect(rect);
                if clipped.is_empty() {
                    continue;
                }
                if let Some(tile) = self.tiles.get_mut(&(tx, ty)) {
                    for py in clipped.y..clipped.bottom() {
                        let ty_off = py.rem_euclid(TILE_SIZE) as usize;
                        let tx_start = clipped.x.rem_euclid(TILE_SIZE) as usize;
                        let row_off = (ty_off * TILE_SIZE as usize + tx_start) * 4;
                        for px in 0..clipped.w as usize {
                            tile[row_off + px * 4 + 3] = 0;
                        }
                    }
                }
            }
        }

        // Prune empty tiles and recompute bounds
        self.tiles.retain(|_, tile| {
            tile.chunks(4).any(|px| px[3] != 0)
        });
        self.recompute_bounds();
    }

    /// Invert the selection within the document bounds.
    pub fn invert(&mut self, doc_bounds: Rect) {
        if doc_bounds.is_empty() {
            return;
        }

        let first_tx = tile_coord(doc_bounds.x);
        let first_ty = tile_coord(doc_bounds.y);
        let last_tx = tile_coord(doc_bounds.right() - 1);
        let last_ty = tile_coord(doc_bounds.bottom() - 1);

        let mut new_tiles: HashMap<(i32, i32), Box<[u8; TILE_BYTES]>> = HashMap::new();

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                let tile_rect = Rect::new(
                    tx * TILE_SIZE,
                    ty * TILE_SIZE,
                    TILE_SIZE,
                    TILE_SIZE,
                );
                let clipped = tile_rect.intersect(doc_bounds);
                if clipped.is_empty() {
                    continue;
                }

                let old_tile = self.tiles.remove(&(tx, ty));
                let mut new_tile = [0u8; TILE_BYTES];

                for py in clipped.y..clipped.bottom() {
                    let ty_off = py.rem_euclid(TILE_SIZE) as usize;
                    let tx_off = clipped.x.rem_euclid(TILE_SIZE) as usize;
                    let row_off = (ty_off * TILE_SIZE as usize + tx_off) * 4;
                    for px in 0..clipped.w as usize {
                        let old_a = old_tile.as_ref()
                            .map(|t| t[row_off + px * 4 + 3])
                            .unwrap_or(0);
                        new_tile[row_off + px * 4 + 3] = 255 - old_a;
                    }
                }

                if new_tile.chunks(4).any(|px| px[3] != 0) {
                    new_tiles.insert((tx, ty), Box::new(new_tile));
                }
            }
        }

        self.tiles = new_tiles;
        self.bounds = doc_bounds;
    }

    /// Check whether a point is selected.
    pub fn contains_point(&self, x: i32, y: i32) -> bool {
        let key = (tile_coord(x), tile_coord(y));
        if let Some(tile) = self.tiles.get(&key) {
            let tx = x.rem_euclid(TILE_SIZE) as usize;
            let ty = y.rem_euclid(TILE_SIZE) as usize;
            let off = (ty * TILE_SIZE as usize + tx) * 4;
            tile[off + 3] != 0
        } else {
            false
        }
    }

    pub fn is_empty(&self) -> bool {
        self.tiles.is_empty()
    }

    pub fn bounds(&self) -> Rect {
        self.bounds
    }

    pub fn has_selection(&self) -> bool {
        !self.tiles.is_empty()
    }

    /// Capture a snapshot of the selection for undo.
    pub fn capture_snapshot(&self) -> SelectionSnapshot {
        SelectionSnapshot {
            mask_tiles: self.tiles.iter().map(|(&k, v)| (k, v.clone())).collect(),
            bounds: self.bounds,
        }
    }

    /// Restore from a snapshot (for undo).
    pub fn restore_snapshot(&mut self, snapshot: SelectionSnapshot) {
        self.tiles = snapshot.mask_tiles;
        self.bounds = snapshot.bounds;
    }

    /// Clear the selection.
    pub fn clear(&mut self) {
        self.tiles.clear();
        self.bounds = Rect::ZERO;
    }

    pub fn replace_from_mask(&mut self, bounds: Rect, mask: &[bool]) {
        self.clear();
        self.add_from_mask(bounds, mask);
    }

    pub fn add_from_mask(&mut self, bounds: Rect, mask: &[bool]) {
        self.apply_mask(bounds, mask, MaskOp::Add);
    }

    pub fn subtract_from_mask(&mut self, bounds: Rect, mask: &[bool]) {
        self.apply_mask(bounds, mask, MaskOp::Subtract);
    }

    fn recompute_bounds(&mut self) {
        let mut found = false;
        let mut min_x = i32::MAX;
        let mut min_y = i32::MAX;
        let mut max_x = i32::MIN;
        let mut max_y = i32::MIN;

        for (&(tx, ty), tile) in &self.tiles {
            for py in 0..TILE_SIZE {
                let row_off = py as usize * TILE_SIZE as usize * 4;
                for px in 0..TILE_SIZE {
                    if tile[row_off + px as usize * 4 + 3] != 0 {
                        let doc_x = tx * TILE_SIZE + px;
                        let doc_y = ty * TILE_SIZE + py;
                        found = true;
                        min_x = min_x.min(doc_x);
                        min_y = min_y.min(doc_y);
                        max_x = max_x.max(doc_x);
                        max_y = max_y.max(doc_y);
                    }
                }
            }
        }
        self.bounds = if found {
            Rect::new(min_x, min_y, max_x - min_x + 1, max_y - min_y + 1)
        } else {
            Rect::ZERO
        };
    }

    fn apply_mask(&mut self, bounds: Rect, mask: &[bool], op: MaskOp) {
        if bounds.is_empty() {
            return;
        }
        let expected = (bounds.w.max(0) as usize) * (bounds.h.max(0) as usize);
        if mask.len() != expected {
            return;
        }

        for row in 0..bounds.h {
            for col in 0..bounds.w {
                let idx = row as usize * bounds.w as usize + col as usize;
                if !mask[idx] {
                    continue;
                }
                let x = bounds.x + col;
                let y = bounds.y + row;
                let tx = tile_coord(x);
                let ty = tile_coord(y);
                let tile = self.tiles.entry((tx, ty)).or_insert_with(|| Box::new([0u8; TILE_BYTES]));
                let local_x = x.rem_euclid(TILE_SIZE) as usize;
                let local_y = y.rem_euclid(TILE_SIZE) as usize;
                let off = (local_y * TILE_SIZE as usize + local_x) * 4 + 3;
                match op {
                    MaskOp::Add => tile[off] = 255,
                    MaskOp::Subtract => tile[off] = 0,
                }
            }
        }

        self.tiles.retain(|_, tile| tile.chunks(4).any(|px| px[3] != 0));
        self.recompute_bounds();
    }
}

#[derive(Clone, Copy)]
enum MaskOp {
    Add,
    Subtract,
}

use crate::history::SelectionSnapshot;

#[inline]
fn tile_coord(pixel: i32) -> i32 {
    let tile = pixel / TILE_SIZE;
    if pixel < 0 && pixel % TILE_SIZE != 0 {
        tile - 1
    } else {
        tile
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_selection_has_no_tiles() {
        let sel = SelectionMask::empty();
        assert!(sel.is_empty());
        assert!(!sel.contains_point(0, 0));
    }

    #[test]
    fn rect_selection_contains_corners() {
        let sel = SelectionMask::from_rect(Rect::new(10, 20, 30, 40));
        assert!(sel.contains_point(10, 20));
        assert!(sel.contains_point(39, 59));
        assert!(!sel.contains_point(40, 60));
        assert!(!sel.contains_point(9, 19));
    }

    #[test]
    fn subtract_removes_region() {
        let mut sel = SelectionMask::from_rect(Rect::new(0, 0, 100, 100));
        sel.subtract_rect(Rect::new(25, 25, 50, 50));
        assert!(sel.contains_point(10, 10));
        assert!(!sel.contains_point(50, 50));
    }

    #[test]
    fn invert_flips_selection() {
        let mut sel = SelectionMask::from_rect(Rect::new(0, 0, 64, 64));
        sel.invert(Rect::new(0, 0, 128, 128));
        // Center (32,32) was selected, now should NOT be
        assert!(!sel.contains_point(32, 32));
        // Corner (96,96) was not selected, now should be
        assert!(sel.contains_point(96, 96));
    }
}
