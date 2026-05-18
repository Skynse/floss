//! Sparse tiled pixel buffer — the core data structure for layer pixel storage.
//!
//! Ported from `Floss.App.Document.TiledPixelBuffer.cs`.
//!
//! Key properties:
//! - **Infinite canvas**: supports positive and negative tile coordinates.
//! - **Sparse**: only tiles with non-transparent content are stored.
//! - **Tile size**: 64×64 pixels, 4 bytes (BGRA) each = 16384 bytes per tile.
//! - **Compression**: tiles can be deflate-compressed to reduce memory.
//! - **Thread-safe**: internal `RwLock` for concurrent read access.

use std::collections::HashMap;
use std::io::{Read, Write};

use floss_core::Rect;

// ── Constants (match C# TiledPixelBuffer) ──────────────────────────────────

/// Tile edge length in pixels.
const TILE_SIZE: i32 = 64;
/// Bytes per pixel (BGRA).
const BYTES_PER_PIXEL: usize = 4;
/// Total bytes in a raw tile.
const TILE_BYTES: usize = (TILE_SIZE as usize) * (TILE_SIZE as usize) * BYTES_PER_PIXEL;

// ── Tile coordinate helpers ────────────────────────────────────────────────

/// Maps a pixel coordinate to the tile coordinate that contains it.
/// Uses arithmetic (floor) division consistent with the C# `FloorDiv`.
#[inline]
fn tile_coord(pixel: i32) -> i32 {
    // C# FloorDiv for negative numbers: rounds toward -inf
    let tile = pixel / TILE_SIZE;
    if pixel < 0 && pixel % TILE_SIZE != 0 {
        tile - 1
    } else {
        tile
    }
}

/// Byte offset within a tile for the given pixel coordinates.
#[inline]
fn offset_in_tile(x: i32, y: i32) -> usize {
    let tx = x.rem_euclid(TILE_SIZE) as usize;
    let ty = y.rem_euclid(TILE_SIZE) as usize;
    (ty * TILE_SIZE as usize + tx) * BYTES_PER_PIXEL
}

// ── TiledPixelBuffer ───────────────────────────────────────────────────────

/// Sparse tiled pixel buffer for layer content storage.
pub struct TiledPixelBuffer {
    /// Hot (uncompressed) tiles currently in active use.
    tiles: HashMap<(i32, i32), Box<[u8; TILE_BYTES]>>,
    /// Cold (deflate-compressed) tiles.
    compressed: HashMap<(i32, i32), Box<[u8]>>,
    /// Bounds tracking — expanded as tiles are written.
    min_x: i32,
    min_y: i32,
    max_x: i32,
    max_y: i32,
}

impl TiledPixelBuffer {
    /// Create a new buffer with the given initial canvas size.
    ///
    /// The buffer starts empty (no tiles); bounds track the requested
    /// canvas size but content is sparse.
    pub fn new(width: i32, height: i32) -> Self {
        Self {
            tiles: HashMap::new(),
            compressed: HashMap::new(),
            min_x: 0,
            min_y: 0,
            max_x: width.max(1),
            max_y: height.max(1),
        }
    }

    // ── Bounds ─────────────────────────────────────────────────────────────

    pub fn width(&self) -> i32 {
        (self.max_x - self.min_x).max(1)
    }
    pub fn height(&self) -> i32 {
        (self.max_y - self.min_y).max(1)
    }
    pub fn bounds(&self) -> Rect {
        Rect::new(self.min_x, self.min_y, self.width(), self.height())
    }
    pub fn tile_count(&self) -> usize {
        self.tiles.len() + self.compressed.len()
    }

    fn extend_bounds(&mut self, left: i32, top: i32, right: i32, bottom: i32) {
        if left < self.min_x {
            self.min_x = left;
        }
        if top < self.min_y {
            self.min_y = top;
        }
        if right > self.max_x {
            self.max_x = right;
        }
        if bottom > self.max_y {
            self.max_y = bottom;
        }
    }

    /// Discard all tiles and reset to the given canvas size.
    pub fn resize(&mut self, width: i32, height: i32) {
        self.tiles.clear();
        self.compressed.clear();
        self.min_x = 0;
        self.min_y = 0;
        self.max_x = width.max(1);
        self.max_y = height.max(1);
    }

    // ── Tile access ────────────────────────────────────────────────────────

    /// Returns the raw tile data for `(tx, ty)` if it exists (decompressing if needed).
    pub fn get_tile_or_null(&mut self, tx: i32, ty: i32) -> Option<&[u8; TILE_BYTES]> {
        let key = (tx, ty);
        if self.tiles.contains_key(&key) {
            return self.tiles.get(&key).map(|b| b.as_ref());
        }
        if self.compressed.contains_key(&key) {
            return self.ensure_raw(key);
        }
        None
    }

    /// Get or create a mutable tile at `(tx, ty)`. Creates a zero-filled tile if absent.
    pub fn get_or_create_tile(&mut self, tx: i32, ty: i32) -> &mut [u8; TILE_BYTES] {
        let key = (tx, ty);
        self.ensure_raw(key);
        if !self.tiles.contains_key(&key) {
            let tile = Box::new([0u8; TILE_BYTES]);
            self.tiles.insert(key, tile);
            self.compressed.remove(&key);
            self.extend_bounds(
                tx * TILE_SIZE,
                ty * TILE_SIZE,
                (tx + 1) * TILE_SIZE,
                (ty + 1) * TILE_SIZE,
            );
        }
        self.tiles.get_mut(&key).unwrap()
    }

    /// Ensure a tile is in the hot (uncompressed) set, decompressing if necessary.
    fn ensure_raw(&mut self, key: (i32, i32)) -> Option<&[u8; TILE_BYTES]> {
        if self.tiles.contains_key(&key) {
            return self.tiles.get(&key).map(|b| b.as_ref());
        }
        if let Some(compressed) = self.compressed.remove(&key) {
            let raw = inflate(&compressed).unwrap_or_else(|| {
                // If decompression fails, treat as raw (wasn't compressed)
                let mut buf = [0u8; TILE_BYTES];
                let len = compressed.len().min(TILE_BYTES);
                buf[..len].copy_from_slice(&compressed[..len]);
                buf
            });
            let boxed = Box::new(raw);
            self.tiles.insert(key, boxed);
            return self.tiles.get(&key).map(|b| b.as_ref());
        }
        None
    }

    // ── Pixel access ───────────────────────────────────────────────────────

    /// Write a BGRA pixel at document coordinates.
    pub fn set_pixel(&mut self, x: i32, y: i32, b: u8, g: u8, r: u8, a: u8) {
        let tile = self.get_or_create_tile(tile_coord(x), tile_coord(y));
        let off = offset_in_tile(x, y);
        tile[off] = b;
        tile[off + 1] = g;
        tile[off + 2] = r;
        tile[off + 3] = a;
    }

    /// Read a BGRA pixel at document coordinates. Returns zeros if no tile exists.
    /// Decompresses tiles on demand (needs &mut for decompression).
    pub fn get_pixel(&mut self, x: i32, y: i32) -> [u8; 4] {
        let key = (tile_coord(x), tile_coord(y));
        self.ensure_raw(key);
        self.read_pixel_hot(x, y)
    }

    /// Read a BGRA pixel from hot (uncompressed) tiles only.
    /// Returns zeros if the tile is compressed or absent.
    pub fn try_read_pixel(&self, x: i32, y: i32) -> [u8; 4] {
        let key = (tile_coord(x), tile_coord(y));
        if let Some(tile) = self.tiles.get(&key) {
            let off = offset_in_tile(x, y);
            [tile[off], tile[off + 1], tile[off + 2], tile[off + 3]]
        } else {
            [0, 0, 0, 0]
        }
    }

    fn read_pixel_hot(&self, x: i32, y: i32) -> [u8; 4] {
        let key = (tile_coord(x), tile_coord(y));
        if let Some(tile) = self.tiles.get(&key) {
            let off = offset_in_tile(x, y);
            [tile[off], tile[off + 1], tile[off + 2], tile[off + 3]]
        } else {
            [0, 0, 0, 0]
        }
    }

    // ── Clear ──────────────────────────────────────────────────────────────

    /// Discard all tiles.
    pub fn clear_all(&mut self) {
        self.tiles.clear();
        self.compressed.clear();
    }

    /// Clear pixels within a region. Tiles that become fully transparent are pruned.
    pub fn clear_region(&mut self, region: Rect) {
        if region.is_empty() {
            return;
        }
        self.for_each_tile(region, false, |_tx, _ty, tile, tile_region| {
            let row_bytes = tile_region.w as usize * BYTES_PER_PIXEL;
            for py in tile_region.y..tile_region.bottom() {
                let row_off = (py.rem_euclid(TILE_SIZE) as usize * TILE_SIZE as usize
                    + tile_region.x.rem_euclid(TILE_SIZE) as usize)
                    * BYTES_PER_PIXEL;
                tile[row_off..row_off + row_bytes].fill(0);
            }
        });
        self.prune_transparent_tiles(region);
    }

    /// Fill a region with a solid BGRA color.
    pub fn fill_solid(&mut self, region: Rect, b: u8, g: u8, r: u8, a: u8) {
        if region.is_empty() {
            return;
        }
        if a == 0 {
            self.clear_region(region);
            return;
        }

        let first_tx = tile_coord(region.x);
        let first_ty = tile_coord(region.y);
        let last_tx = tile_coord(region.right() - 1);
        let last_ty = tile_coord(region.bottom() - 1);

        let full_tile = make_solid_tile(b, g, r, a);

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                let tile_rect = Rect::new(
                    tx * TILE_SIZE,
                    ty * TILE_SIZE,
                    TILE_SIZE,
                    TILE_SIZE,
                );
                let clipped = tile_rect.intersect(region);
                if clipped.is_empty() {
                    continue;
                }

                if clipped.w == TILE_SIZE && clipped.h == TILE_SIZE {
                    // Full tile — store compressed
                    let deflated = deflate(&full_tile);
                    self.tiles.remove(&(tx, ty));
                    self.compressed.insert((tx, ty), deflated.into());
                    self.extend_bounds(
                        tx * TILE_SIZE,
                        ty * TILE_SIZE,
                        (tx + 1) * TILE_SIZE,
                        (ty + 1) * TILE_SIZE,
                    );
                } else {
                    // Partial tile — write into raw
                    let tile = self.get_or_create_tile(tx, ty);
                    for py in clipped.y..clipped.bottom() {
                        let ty_off = py.rem_euclid(TILE_SIZE) as usize;
                        let tx_start = clipped.x.rem_euclid(TILE_SIZE) as usize;
                        let row_off = (ty_off * TILE_SIZE as usize + tx_start) * BYTES_PER_PIXEL;
                        let count = clipped.w as usize * BYTES_PER_PIXEL;
                        tile[row_off..row_off + count]
                            .copy_from_slice(&full_tile[row_off..row_off + count]);
                    }
                }
            }
        }
    }

    // ── Bulk copy ──────────────────────────────────────────────────────────

    /// Copy a BGRA bitmap (top-left origin) into this buffer.
    /// Pixels with alpha=0 are skipped.
    pub fn copy_from_bgra(&mut self, src: &[u8], src_width: i32, src_height: i32) {
        let copy_w = src_width.min(self.width());
        let copy_h = src_height.min(self.height());
        if copy_w <= 0 || copy_h <= 0 {
            return;
        }

        let last_tx = (copy_w - 1) / TILE_SIZE;
        let last_ty = (copy_h - 1) / TILE_SIZE;

        for ty in 0..=last_ty {
            let tile_y = ty * TILE_SIZE;
            let tile_h = TILE_SIZE.min(copy_h - tile_y);
            for tx in 0..=last_tx {
                let tile_x = tx * TILE_SIZE;
                let tile_w = TILE_SIZE.min(copy_w - tile_x);

                if !Self::has_any_alpha(src, src_width, tile_x, tile_y, tile_w, tile_h) {
                    continue;
                }

                let mut tile = [0u8; TILE_BYTES];
                for y in 0..tile_h {
                    let src_off =
                        ((tile_y + y) as usize * src_width as usize + tile_x as usize) * BYTES_PER_PIXEL;
                    let dst_off = y as usize * TILE_SIZE as usize * BYTES_PER_PIXEL;
                    let row_bytes = tile_w as usize * BYTES_PER_PIXEL;
                    tile[dst_off..dst_off + row_bytes]
                        .copy_from_slice(&src[src_off..src_off + row_bytes]);
                }
                self.tiles.insert((tx, ty), Box::new(tile));
                self.compressed.remove(&(tx, ty));
            }
        }
    }

    fn has_any_alpha(
        src: &[u8],
        src_width: i32,
        tile_x: i32,
        tile_y: i32,
        tile_w: i32,
        tile_h: i32,
    ) -> bool {
        for y in 0..tile_h {
            let row_off =
                ((tile_y + y) as usize * src_width as usize + tile_x as usize) * BYTES_PER_PIXEL;
            for x in 0..tile_w {
                let a = src[row_off + x as usize * BYTES_PER_PIXEL + 3];
                if a != 0 {
                    return true;
                }
            }
        }
        false
    }

    // ── Prune ──────────────────────────────────────────────────────────────

    /// Remove fully-transparent tiles within a region.
    fn prune_transparent_tiles(&mut self, region: Rect) {
        let first_tx = tile_coord(region.x);
        let first_ty = tile_coord(region.y);
        let last_tx = tile_coord(region.right() - 1);
        let last_ty = tile_coord(region.bottom() - 1);

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                let key = (tx, ty);
                let is_transparent = if let Some(tile) = self.tiles.get(&key) {
                    tile.iter().all(|&b| b == 0)
                } else if let Some(compressed) = self.compressed.get(&key) {
                    // Quick check: the first byte of a deflate stream would be non-zero
                    // for any content. If the compressed data is all zeros, the tile is empty.
                    compressed.iter().all(|&b| b == 0)
                } else {
                    continue;
                };
                if is_transparent {
                    self.tiles.remove(&key);
                    self.compressed.remove(&key);
                }
            }
        }
    }

    // ── Compute content bounds ─────────────────────────────────────────────

    /// Compute the tight bounding box of all non-transparent content.
    pub fn compute_content_bounds(&mut self) -> Rect {
        let mut found = false;
        let mut min_x = i32::MAX;
        let mut min_y = i32::MAX;
        let mut max_x = i32::MIN;
        let mut max_y = i32::MIN;

        // Gather keys to avoid borrow issues
        let tile_keys: Vec<_> = self.tiles.keys().chain(self.compressed.keys()).copied().collect();

        for (tx, ty) in tile_keys {
            let tile_data: Option<Box<[u8; TILE_BYTES]>> = if self.tiles.contains_key(&(tx, ty)) {
                None // already hot
            } else {
                self.ensure_raw((tx, ty));
                self.tiles.remove(&(tx, ty)).map(|b| b)
            };

            let check_tile = |tile: &[u8; TILE_BYTES], found: &mut bool,
                              min_x: &mut i32, min_y: &mut i32,
                              max_x: &mut i32, max_y: &mut i32| {
                for py in 0..TILE_SIZE {
                    let row_off = py as usize * TILE_SIZE as usize * BYTES_PER_PIXEL;
                    for px in 0..TILE_SIZE {
                        let a = tile[row_off + px as usize * BYTES_PER_PIXEL + 3];
                        if a != 0 {
                            let doc_x = tx * TILE_SIZE + px;
                            let doc_y = ty * TILE_SIZE + py;
                            *found = true;
                            *min_x = (*min_x).min(doc_x);
                            *min_y = (*min_y).min(doc_y);
                            *max_x = (*max_x).max(doc_x);
                            *max_y = (*max_y).max(doc_y);
                        }
                    }
                }
            };

            if let Some(ref tile) = self.tiles.get(&(tx, ty)) {
                check_tile(tile, &mut found, &mut min_x, &mut min_y, &mut max_x, &mut max_y);
            } else if let Some(tile) = tile_data {
                check_tile(&tile, &mut found, &mut min_x, &mut min_y, &mut max_x, &mut max_y);
            }
        }

        if found {
            Rect::new(min_x, min_y, max_x - min_x + 1, max_y - min_y + 1)
        } else {
            Rect::ZERO
        }
    }

    /// Check if any tile intersects the given region.
    pub fn has_content_tiles(&self, region: Rect) -> bool {
        if region.is_empty() || self.tile_count() == 0 {
            return false;
        }
        let first_tx = tile_coord(region.x);
        let first_ty = tile_coord(region.y);
        let last_tx = tile_coord(region.right() - 1);
        let last_ty = tile_coord(region.bottom() - 1);
        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                if self.tiles.contains_key(&(tx, ty)) || self.compressed.contains_key(&(tx, ty)) {
                    return true;
                }
            }
        }
        false
    }

    // ── Blend onto flat buffer ─────────────────────────────────────────────

    /// Blend all tiles onto a pre-allocated BGRA flat buffer, respecting opacity.
    pub fn blend_onto(&mut self, dst: &mut [u8], dst_width: i32, dst_height: i32, opacity: f64) {
        if opacity <= 0.0 {
            return;
        }
        let op_int = (opacity * 255.0 + 0.5) as u32;

        // Collect tile keys
        let tile_keys: Vec<(i32, i32)> =
            self.tiles.keys().chain(self.compressed.keys()).copied().collect();

        for (tx, ty) in tile_keys {
            if let Some(tile) = self.ensure_raw((tx, ty)).map(|t| *t) {
                Self::blend_tile_onto(tx, ty, &tile, dst, dst_width, dst_height, op_int);
            } else if let Some(tile) = self.tiles.get(&(tx, ty)) {
                let tile_ref: &[u8; TILE_BYTES] = tile;
                Self::blend_tile_onto(tx, ty, tile_ref, dst, dst_width, dst_height, op_int);
            }
        }
    }

    fn blend_tile_onto(
        tx: i32,
        ty: i32,
        tile: &[u8; TILE_BYTES],
        dst: &mut [u8],
        dst_width: i32,
        dst_height: i32,
        op_int: u32,
    ) {
        let ox = tx * TILE_SIZE;
        let oy = ty * TILE_SIZE;
        for py in 0..TILE_SIZE {
            let doc_y = oy + py;
            if doc_y < 0 || doc_y >= dst_height {
                continue;
            }
            let row_base = doc_y as usize * dst_width as usize;
            let src_row = py as usize * TILE_SIZE as usize;
            for px in 0..TILE_SIZE {
                let doc_x = ox + px;
                if doc_x < 0 || doc_x >= dst_width {
                    continue;
                }
                let src_off = (src_row + px as usize) * BYTES_PER_PIXEL;
                let src_a = (tile[src_off + 3] as u32 * op_int / 255) as u8;
                if src_a == 0 {
                    continue;
                }
                let dst_off = (row_base + doc_x as usize) * BYTES_PER_PIXEL;
                let dst_a = dst[dst_off + 3] as u32;
                let out_a = src_a as u32 + dst_a * (255 - src_a as u32) / 255;
                if out_a == 0 {
                    continue;
                }
                // Standard over composite (src-over-dst, both premultiplied alpha)
                dst[dst_off] =
                    ((tile[src_off] as u32 * src_a as u32
                        + dst[dst_off] as u32 * dst_a * (255 - src_a as u32) / 255)
                        / out_a) as u8;
                dst[dst_off + 1] =
                    ((tile[src_off + 1] as u32 * src_a as u32
                        + dst[dst_off + 1] as u32 * dst_a * (255 - src_a as u32) / 255)
                        / out_a) as u8;
                dst[dst_off + 2] =
                    ((tile[src_off + 2] as u32 * src_a as u32
                        + dst[dst_off + 2] as u32 * dst_a * (255 - src_a as u32) / 255)
                        / out_a) as u8;
                dst[dst_off + 3] = out_a as u8;
            }
        }
    }

    // ── Capture / Restore (for undo) ───────────────────────────────────────

    /// Capture all tiles as defensive copies.
    pub fn capture_tiles(&mut self) -> HashMap<(i32, i32), Box<[u8; TILE_BYTES]>> {
        let mut result = HashMap::new();
        // Ensure all compressed are hot first
        let compressed_keys: Vec<_> = self.compressed.keys().copied().collect();
        for key in compressed_keys {
            self.ensure_raw(key);
        }
        for (&key, tile) in &self.tiles {
            result.insert(key, Box::new(**tile));
        }
        result
    }

    /// Restore tiles from a previously captured snapshot.
    pub fn restore_tiles(&mut self, tiles: HashMap<(i32, i32), Box<[u8; TILE_BYTES]>>) {
        self.tiles.clear();
        self.compressed.clear();
        for (key, tile) in tiles {
            self.tiles.insert(key, tile);
        }
    }

    /// Capture tiles intersecting a region.
    pub fn capture_region(&mut self, region: Rect) -> HashMap<(i32, i32), Box<[u8; TILE_BYTES]>> {
        let mut result = HashMap::new();
        if region.is_empty() {
            return result;
        }
        let first_tx = tile_coord(region.x);
        let first_ty = tile_coord(region.y);
        let last_tx = tile_coord(region.right() - 1);
        let last_ty = tile_coord(region.bottom() - 1);

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                let key = (tx, ty);
                if let Some(tile) = self.ensure_raw(key) {
                    result.insert(key, Box::new(*tile));
                }
            }
        }
        result
    }

    // ── Utility: iterate over tiles in a region ────────────────────────────

    fn for_each_tile<F>(&mut self, region: Rect, create: bool, mut f: F)
    where
        F: FnMut(i32, i32, &mut [u8; TILE_BYTES], Rect),
    {
        if region.is_empty() {
            return;
        }
        let first_tx = tile_coord(region.x);
        let first_ty = tile_coord(region.y);
        let last_tx = tile_coord(region.right() - 1);
        let last_ty = tile_coord(region.bottom() - 1);

        for ty in first_ty..=last_ty {
            for tx in first_tx..=last_tx {
                let tile_rect = Rect::new(
                    tx * TILE_SIZE,
                    ty * TILE_SIZE,
                    TILE_SIZE,
                    TILE_SIZE,
                );
                let clipped = tile_rect.intersect(region);
                if clipped.is_empty() {
                    continue;
                }

                if create {
                    let raw = self.get_or_create_tile(tx, ty);
                    // Need to pass the slice to the closure — use unsafe to extend lifetime
                    // SAFETY: we don't re-enter the borrow during the closure
                    let _raw_ptr = raw as *mut [u8; TILE_BYTES];
                    // Actually, let's restructure: collect the tile, pass it to f
                    // Since we're using &mut self, we can't call get_or_create_tile
                    // and hold the reference across the closure without NLL shenanigans.
                    // Instead, we do a two-pass: ensure raw, then access.
                    let key = (tx, ty);
                    self.ensure_raw(key);
                    if !self.tiles.contains_key(&key) {
                        self.tiles.insert(key, Box::new([0u8; TILE_BYTES]));
                        self.compressed.remove(&key);
                        self.extend_bounds(
                            tx * TILE_SIZE,
                            ty * TILE_SIZE,
                            (tx + 1) * TILE_SIZE,
                            (ty + 1) * TILE_SIZE,
                        );
                    }
                    let tile = self.tiles.get_mut(&key).unwrap();
                    f(tx, ty, tile, clipped);
                } else if let Some(tile) = self.ensure_raw((tx, ty)).map(|t| *t) {
                    // We have a decompressed tile; need to mutate it
                    let key = (tx, ty);
                    self.tiles.entry(key).or_insert_with(|| Box::new(tile));
                    let tile = self.tiles.get_mut(&key).unwrap();
                    f(tx, ty, tile, clipped);
                }
            }
        }
    }

    // ── Compression ────────────────────────────────────────────────────────

    /// Compress all hot tiles and move them to the cold set.
    pub fn compress_tiles(&mut self) {
        if self.tiles.is_empty() {
            return;
        }
        let to_compress: Vec<_> = self.tiles.drain().collect();
        for (key, tile) in to_compress {
            let deflated = deflate(tile.as_ref());
            if deflated.len() < TILE_BYTES {
                self.compressed.insert(key, deflated.into());
            } else {
                // Compressed size is not smaller — keep raw
                self.tiles.insert(key, tile);
            }
        }
    }
}

// ── Compression helpers ───────────────────────────────────────────────────

fn deflate(data: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(data.len());
    // Use a simple deflate encoder
    let mut encoder = flate2::write::DeflateEncoder::new(&mut out, flate2::Compression::default());
    encoder.write_all(data).ok();
    encoder.finish().ok();
    out
}

fn inflate(data: &[u8]) -> Option<[u8; TILE_BYTES]> {
    if data.len() < 2 {
        return None;
    }
    let mut decoder = flate2::read::DeflateDecoder::new(data);
    let mut buf = [0u8; TILE_BYTES];
    match decoder.read_exact(&mut buf) {
        Ok(()) => Some(buf),
        Err(_) => None,
    }
}

/// Create a solid-filled tile template from BGRA bytes.
fn make_solid_tile(b: u8, g: u8, r: u8, a: u8) -> [u8; TILE_BYTES] {
    let mut tile = [0u8; TILE_BYTES];
    for px in tile.chunks_exact_mut(BYTES_PER_PIXEL) {
        px[0] = b;
        px[1] = g;
        px[2] = r;
        px[3] = a;
    }
    tile
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn starts_with_positive_bounds() {
        let buf = TiledPixelBuffer::new(256, 128);
        assert_eq!(buf.width(), 256);
        assert_eq!(buf.height(), 128);
        assert_eq!(buf.tile_count(), 0);
    }

    #[test]
    fn set_pixel_creates_tile() {
        let mut buf = TiledPixelBuffer::new(512, 512);
        buf.set_pixel(100, 50, 0, 255, 0, 255); // green
        assert!(buf.tile_count() > 0);
        let px = buf.get_pixel(100, 50);
        assert_eq!(px, [0, 255, 0, 255]);
    }

    #[test]
    fn set_pixel_negative_coords() {
        let mut buf = TiledPixelBuffer::new(512, 512);
        buf.set_pixel(-64, -32, 255, 0, 0, 128);
        let px = buf.get_pixel(-64, -32);
        assert_eq!(px, [255, 0, 0, 128]);
    }

    #[test]
    fn clear_region_removes_pixels() {
        let mut buf = TiledPixelBuffer::new(256, 256);
        buf.set_pixel(10, 10, 0, 0, 255, 255);
        buf.clear_region(Rect::new(0, 0, 64, 64));
        let px = buf.get_pixel(10, 10);
        assert_eq!(px[3], 0); // alpha zero
    }

    #[test]
    fn fill_solid_partial_tile() {
        let mut buf = TiledPixelBuffer::new(256, 256);
        buf.fill_solid(Rect::new(32, 32, 16, 16), 255, 0, 0, 128);
        let px = buf.get_pixel(40, 40);
        assert_eq!(px[0], 255); // B
        assert_eq!(px[2], 0); // R
        assert_eq!(px[3], 128); // A
    }

    #[test]
    fn capture_roundtrip() {
        let mut buf = TiledPixelBuffer::new(256, 256);
        buf.set_pixel(10, 20, 1, 2, 3, 255);
        let snapshot = buf.capture_tiles();
        buf.clear_all();
        assert_eq!(buf.tile_count(), 0);
        buf.restore_tiles(snapshot);
        let px = buf.get_pixel(10, 20);
        assert_eq!(px, [1, 2, 3, 255]);
    }

    #[test]
    fn compute_content_bounds() {
        let mut buf = TiledPixelBuffer::new(512, 512);
        buf.set_pixel(50, 60, 0, 0, 0, 255);
        buf.set_pixel(200, 150, 0, 0, 0, 255);
        let bounds = buf.compute_content_bounds();
        assert!(bounds.contains_point(50, 60));
        assert!(bounds.contains_point(200, 150));
    }

    #[test]
    fn tile_coord_negative() {
        assert_eq!(tile_coord(-1), -1);
        assert_eq!(tile_coord(-64), -1);
        assert_eq!(tile_coord(-65), -2);
        assert_eq!(tile_coord(0), 0);
        assert_eq!(tile_coord(63), 0);
        assert_eq!(tile_coord(64), 1);
    }
}
