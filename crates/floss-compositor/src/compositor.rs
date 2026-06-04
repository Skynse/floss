//! CPU layer compositor — composites all visible layers into a flat BGRA buffer.
//!
//! Architecture: iterate document-space tiles (64×64), composite layers bottom-to-top
//! onto each tile scratch buffer, then assemble all tiles into a flat output.

use std::collections::HashSet;

use floss_core::{BlendMode, Color, Rect};
use floss_document::DrawingDocument;

pub const TILE_SIZE: i32 = 64;
const TILE_BYTES: usize = (TILE_SIZE as usize) * (TILE_SIZE as usize) * 4;

// ── Compositor ──────────────────────────────────────────────────────────────

pub struct LayerCompositor {
    width: i32,
    height: i32,
    xtiles: i32,
    ytiles: i32,
    tile_total: usize,
    /// Per-tile scratch buffers.
    tiles: Vec<Option<Box<[u8; TILE_BYTES]>>>,
    /// Cached output buffer.
    output: Vec<u8>,
    full_dirty: bool,
    pending: HashSet<usize>,
}

impl LayerCompositor {
    pub fn new(width: i32, height: i32) -> Self {
        let xtiles = (width + TILE_SIZE - 1) / TILE_SIZE;
        let ytiles = (height + TILE_SIZE - 1) / TILE_SIZE;
        let tile_total = (xtiles * ytiles) as usize;
        let output = vec![0u8; (width * height * 4) as usize];

        let mut pending = HashSet::new();
        for ti in 0..tile_total {
            pending.insert(ti);
        }

        Self {
            width,
            height,
            xtiles,
            ytiles,
            tile_total,
            tiles: vec![None; tile_total],
            output,
            full_dirty: true,
            pending,
        }
    }

    pub fn width(&self) -> i32 { self.width }
    pub fn height(&self) -> i32 { self.height }

    /// Mark a region as needing recomposite. `None` = full invalidate.
    pub fn invalidate(&mut self, rect: Option<Rect>) {
        if let Some(r) = rect {
            let r = rect_clip(r, self.width, self.height);
            if r.is_empty() { return; }

            let txa = floor_div(r.x, TILE_SIZE);
            let tya = floor_div(r.y, TILE_SIZE);
            let txb = floor_div(r.right() - 1, TILE_SIZE);
            let tyb = floor_div(r.bottom() - 1, TILE_SIZE);
            for ty in tya..=tyb {
                for tx in txa..=txb {
                    if let Some(idx) = tile_index(tx, ty, self.xtiles, self.ytiles) {
                        self.pending.insert(idx);
                    }
                }
            }
        } else {
            self.full_dirty = true;
            for ti in 0..self.tile_total {
                self.pending.insert(ti);
            }
        }
    }

    /// Composite all layers into a flat BGRA buffer. Returns reference to internal buffer.
    pub fn composite(&mut self, doc: &mut DrawingDocument) -> &[u8] {
        if self.width != doc.width() || self.height != doc.height() {
            *self = Self::new(doc.width(), doc.height());
        }

        // Collect all dirty tiles
        let pending: Vec<usize> = self.pending.drain().collect();
        if pending.is_empty() && !self.full_dirty {
            return &self.output;
        }

        let paper_bytes = doc.paper_color().as_bytes();
        let paper: [u8; 4] = [paper_bytes[2], paper_bytes[1], paper_bytes[0], paper_bytes[3]];

        // Composite each tile
        let xtiles = self.xtiles;
        let width = self.width;
        let height = self.height;
        for &ti in &pending {
            let tile = self.get_tile(ti);
            let ty = (ti / xtiles as usize) as i32;
            let tx = (ti % xtiles as usize) as i32;

            // Fill with paper
            for chunk in tile.chunks_exact_mut(4) {
                chunk.copy_from_slice(&paper);
            }

            let ox = tx * TILE_SIZE;
            let oy = ty * TILE_SIZE;
            let tw = TILE_SIZE.min(width - ox);
            let th = TILE_SIZE.min(height - oy);

            composite_region(tile, ox, oy, tw, th, doc);
        }

        self.full_dirty = false;

        // Assemble flat output — copy from tiles array into output
        let output = &mut self.output;
        let stride = width as usize;
        for ti in 0..self.tile_total {
            let tile = match &self.tiles[ti] {
                Some(t) => t,
                None => continue,
            };
            let ty = (ti / xtiles as usize) as i32;
            let tx = (ti % xtiles as usize) as i32;
            let ox = (tx * TILE_SIZE) as usize;
            let oy = (ty * TILE_SIZE) as usize;
            let tw = (TILE_SIZE as usize).min(width as usize - ox);
            let th = (TILE_SIZE as usize).min(height as usize - oy);

            for y in 0..th {
                let src_off = y * TILE_SIZE as usize * 4;
                let dst_off = ((oy + y) * stride + ox) * 4;
                let row_bytes = tw * 4;
                output[dst_off..dst_off + row_bytes]
                    .copy_from_slice(&tile[src_off..src_off + row_bytes]);
            }
        }

        &self.output
    }

    fn get_tile(&mut self, ti: usize) -> &mut [u8; TILE_BYTES] {
        if self.tiles[ti].is_none() {
            self.tiles[ti] = Some(Box::new([0u8; TILE_BYTES]));
        }
        self.tiles[ti].as_mut().unwrap()
    }
}

// ── Helpers ─────────────────────────────────────────────────────────────────

fn floor_div(a: i32, b: i32) -> i32 {
    let q = a / b;
    if (a ^ b) < 0 && a % b != 0 { q - 1 } else { q }
}

fn rect_clip(r: Rect, max_w: i32, max_h: i32) -> Rect {
    if r.w <= 0 || r.h <= 0 { return Rect::ZERO; }
    let x = r.x.max(0);
    let y = r.y.max(0);
    let r2 = (r.x + r.w).min(max_w);
    let b2 = (r.y + r.h).min(max_h);
    if x >= r2 || y >= b2 { Rect::ZERO }
    else { Rect::new(x, y, r2 - x, b2 - y) }
}

fn tile_index(tx: i32, ty: i32, xtiles: i32, ytiles: i32) -> Option<usize> {
    if tx < 0 || ty < 0 || tx >= xtiles || ty >= ytiles { None }
    else { Some((ty * xtiles + tx) as usize) }
}

// ── Layer metadata (collected once, used by all compositing functions) ──────

struct LayerMeta {
    visible: bool,
    is_group: bool,
    is_clipping: bool,
    opacity: f64,
    blend: BlendMode,
    off_x: i32,
    off_y: i32,
    parent_group: i32,
}

// ── Per-tile compositing ────────────────────────────────────────────────────

fn composite_region(
    dst: &mut [u8; TILE_BYTES],
    tile_x: i32,
    tile_y: i32,
    tile_w: i32,
    tile_h: i32,
    doc: &mut DrawingDocument,
) {
    let metas: Vec<LayerMeta> = collect_metas(doc);
    let roots: Vec<usize> = (0..doc.layer_count())
        .filter(|&i| metas[i].parent_group < 0 && !doc.layer(i).is_paper)
        .collect();

    let stack = build_sibling_stack(&roots, &metas);
    composite_sibling_stack(dst, TILE_SIZE, tile_x, tile_y, tile_w, tile_h, &stack, &metas, doc, 1.0);
}

fn collect_metas(doc: &DrawingDocument) -> Vec<LayerMeta> {
    (0..doc.layer_count()).map(|li| {
        let l = doc.layer(li);
        LayerMeta {
            visible: l.visible, is_group: l.is_group, is_clipping: l.is_clipping,
            opacity: l.opacity, blend: l.blend_mode,
            off_x: l.offset_x, off_y: l.offset_y,
            parent_group: l.parent_group,
        }
    }).collect::<Vec<_>>()
}

// ── Sibling stack ────────────────────────────────────────────────────────────

struct StackItem {
    layer_index: usize,
    /// This item is clipped to the base item at `base_stack_index`.
    is_clipped: bool,
    /// Index of the base layer in the stack (only valid if is_clipped).
    base_stack_index: usize,
    /// This item has clipping children (they're processed together).
    has_clipping_children: bool,
}

fn build_sibling_stack(indices: &[usize], metas: &[LayerMeta]) -> Vec<StackItem> {
    let mut stack: Vec<StackItem> = Vec::with_capacity(indices.len());
    let mut last_non_clip: Option<usize> = None;

    for &li in indices {
        if metas[li].is_clipping {
            if let Some(base_idx) = last_non_clip {
                stack[base_idx].has_clipping_children = true;
                stack.push(StackItem { layer_index: li, is_clipped: true, base_stack_index: base_idx, has_clipping_children: false });
            } else {
                // Clipping without base — treat as normal
                stack.push(StackItem { layer_index: li, is_clipped: false, base_stack_index: 0, has_clipping_children: false });
                last_non_clip = Some(stack.len() - 1);
            }
        } else {
            stack.push(StackItem { layer_index: li, is_clipped: false, base_stack_index: 0, has_clipping_children: false });
            if !metas[li].is_clipping {
                last_non_clip = Some(stack.len() - 1);
            }
        }
    }
    stack
}

// ── Stack compositing ────────────────────────────────────────────────────────

fn composite_sibling_stack(
    dst: &mut [u8],
    dst_stride: i32,
    tile_x: i32,
    tile_y: i32,
    tile_w: i32,
    tile_h: i32,
    stack: &[StackItem],
    metas: &[LayerMeta],
    doc: &mut DrawingDocument,
    opacity_scale: f64,
) {
    let mut i = 0;
    while i < stack.len() {
        let item = &stack[i];
        let li = item.layer_index;
        if !metas[li].visible { i += 1; continue; }

        if item.has_clipping_children { i += 1; continue; }

        if item.is_clipped && item.base_stack_index < stack.len() {
            let base_li = stack[item.base_stack_index].layer_index;
            if !metas[base_li].visible { i += 1; continue; }

            let mut end = i;
            while end + 1 < stack.len()
                && stack[end + 1].is_clipped
                && stack[end + 1].base_stack_index == item.base_stack_index
            {
                end += 1;
            }

            let len = (tile_w * tile_h * 4) as usize;
            let mut temp = vec![0u8; len];

            composite_layer_tiles(&mut temp, tile_x, tile_y, tile_w, tile_h, base_li, metas, doc, 1.0);

            for j in i..=end {
                let clip_li = stack[j].layer_index;
                if !metas[clip_li].visible { continue; }
                if metas[clip_li].is_group {
                    composite_group_into_alpha_preserving(&mut temp, tile_x, tile_y, tile_w, tile_h, clip_li, metas, doc);
                } else {
                    composite_layer_alpha_preserving(&mut temp, tile_x, tile_y, tile_w, tile_h, clip_li, metas, doc);
                }
            }

            merge_buffer_onto(dst, dst_stride, tile_x, tile_y, tile_w, tile_h, &temp, metas[base_li].blend, metas[base_li].opacity * opacity_scale);
            i = end + 1;
        } else if metas[li].is_group {
            composite_group_onto(dst, dst_stride, tile_x, tile_y, tile_w, tile_h, li, metas, doc, opacity_scale);
            i += 1;
        } else {
            composite_layer_tiles(dst, tile_x, tile_y, tile_w, tile_h, li, metas, doc, opacity_scale);
            i += 1;
        }
    }
}

// ── Normal layer compositing ─────────────────────────────────────────────────

fn composite_layer_tiles(
    buf: &mut [u8],
    tile_x: i32,
    tile_y: i32,
    tile_w: i32,
    tile_h: i32,
    li: usize,
    metas: &[LayerMeta],
    doc: &mut DrawingDocument,
    opacity_scale: f64,
) {
    let m = &metas[li];
    let opacity = m.opacity * opacity_scale;
    if opacity <= 0.0 { return; }
    let opacity_byte = (opacity * 255.0 + 0.5).clamp(0.0, 255.0) as u8;

    let sx0 = tile_x - m.off_x;
    let sy0 = tile_y - m.off_y;
    let sx1 = sx0 + tile_w;
    let sy1 = sy0 + tile_h;

    let first_tx = floor_div(sx0, TILE_SIZE);
    let first_ty = floor_div(sy0, TILE_SIZE);
    let last_tx = floor_div(sx1 - 1, TILE_SIZE);
    let last_ty = floor_div(sy1 - 1, TILE_SIZE);

    let buf_stride = tile_w as usize;
    let blend_is_normal = m.blend == BlendMode::Normal;

    for sty in first_ty..=last_ty {
        for stx in first_tx..=last_tx {
            let src_tile = match doc.layer_mut(li).pixels.get_tile_or_null(stx, sty) {
                Some(t) => t,
                None => continue,
            };

            let src_left = sx0.max(stx * TILE_SIZE);
            let src_top = sy0.max(sty * TILE_SIZE);
            let src_right = sx1.min(stx * TILE_SIZE + TILE_SIZE);
            let src_bottom = sy1.min(sty * TILE_SIZE + TILE_SIZE);

            for sy in src_top..src_bottom {
                let ty_off = (sy - sty * TILE_SIZE) as usize;
                let sx_start = (src_left - stx * TILE_SIZE) as usize;
                let src_row = (ty_off * TILE_SIZE as usize + sx_start) * 4;

                let dst_y = sy + m.off_y - tile_y;
                if dst_y < 0 || dst_y >= tile_h { continue; }
                let dst_row = (dst_y as usize * buf_stride) * 4;

                for j in 0..(src_right - src_left) as usize {
                    let si = src_row + j * 4;
                    let sa_raw = src_tile[si + 3];
                    if sa_raw == 0 { continue; }

                    let dst_x = src_left + j as i32 + m.off_x - tile_x;
                    if dst_x < 0 || dst_x >= tile_w { continue; }
                    let di = dst_row + dst_x as usize * 4;

                    if blend_is_normal {
                        let sa = (sa_raw as u32 * opacity_byte as u32 + 127) / 255;
                        if sa == 0 { continue; }
                        blit_pixel(&mut buf[di..di + 4], &src_tile[si..si + 4], sa);
                    } else {
                        let sa_f = sa_raw as f64 / 255.0 * opacity;
                        if sa_f <= 0.0 { continue; }
                        let (out_r, out_g, out_b, out_a) = floss_core::blend::blend_pixel_float(
                            src_tile[si + 2] as f64 / 255.0,
                            src_tile[si + 1] as f64 / 255.0,
                            src_tile[si] as f64 / 255.0,
                            sa_f,
                            buf[di + 2] as f64 / 255.0,
                            buf[di + 1] as f64 / 255.0,
                            buf[di] as f64 / 255.0,
                            buf[di + 3] as f64 / 255.0,
                            m.blend,
                        );
                        buf[di]       = (out_b * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                        buf[di + 1]   = (out_g * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                        buf[di + 2]   = (out_r * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                        buf[di + 3]   = (out_a * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                    }
                }
            }
        }
    }
}

// ── Alpha-preserving compositing (clipping layers) ───────────────────────────

fn composite_layer_alpha_preserving(
    buf: &mut [u8],
    tile_x: i32,
    tile_y: i32,
    tile_w: i32,
    tile_h: i32,
    li: usize,
    metas: &[LayerMeta],
    doc: &mut DrawingDocument,
) {
    let m = &metas[li];
    let opacity_byte = (m.opacity * 255.0 + 0.5).clamp(0.0, 255.0) as u8;

    let sx0 = tile_x - m.off_x;
    let sy0 = tile_y - m.off_y;
    let sx1 = sx0 + tile_w;
    let sy1 = sy0 + tile_h;

    let first_tx = floor_div(sx0, TILE_SIZE);
    let first_ty = floor_div(sy0, TILE_SIZE);
    let last_tx = floor_div(sx1 - 1, TILE_SIZE);
    let last_ty = floor_div(sy1 - 1, TILE_SIZE);

    let buf_stride = tile_w as usize;
    let blend_is_normal = m.blend == BlendMode::Normal;

    for sty in first_ty..=last_ty {
        for stx in first_tx..=last_tx {
            let src_tile = match doc.layer_mut(li).pixels.get_tile_or_null(stx, sty) {
                Some(t) => t,
                None => continue,
            };

            let src_left = sx0.max(stx * TILE_SIZE);
            let src_top = sy0.max(sty * TILE_SIZE);
            let src_right = sx1.min(stx * TILE_SIZE + TILE_SIZE);
            let src_bottom = sy1.min(sty * TILE_SIZE + TILE_SIZE);

            for sy in src_top..src_bottom {
                let ty_off = (sy - sty * TILE_SIZE) as usize;
                let sx_start = (src_left - stx * TILE_SIZE) as usize;
                let src_row = (ty_off * TILE_SIZE as usize + sx_start) * 4;

                let dst_y = sy + m.off_y - tile_y;
                if dst_y < 0 || dst_y >= tile_h { continue; }
                let dst_row = (dst_y as usize * buf_stride) * 4;

                for j in 0..(src_right - src_left) as usize {
                    let si = src_row + j * 4;
                    let raw_a = src_tile[si + 3];
                    if raw_a == 0 { continue; }

                    let dst_x = src_left + j as i32 + m.off_x - tile_x;
                    if dst_x < 0 || dst_x >= tile_w { continue; }
                    let di = dst_row + dst_x as usize * 4;

                    let sa = (raw_a as u32 * opacity_byte as u32 + 127) / 255;
                    if sa == 0 { continue; }

                    if blend_is_normal {
                        blend_color_only(&mut buf[di..di + 4], src_tile[si], src_tile[si + 1], src_tile[si + 2], sa as u8);
                    } else {
                        // Double path for non-normal blend, alpha-preserving
                        let s_r = src_tile[si + 2] as f64 / 255.0;
                        let s_g = src_tile[si + 1] as f64 / 255.0;
                        let s_b = src_tile[si] as f64 / 255.0;
                        let s_a = sa as f64 / 255.0;
                        let d_r = buf[di + 2] as f64 / 255.0;
                        let d_g = buf[di + 1] as f64 / 255.0;
                        let d_b = buf[di] as f64 / 255.0;
                        let d_a = buf[di + 3] as f64 / 255.0;

                        let (blend_r, blend_g, blend_b) = floss_core::blend::apply_blend_mode(
                            s_r, s_g, s_b, s_a, d_r, d_g, d_b, d_a, m.blend,
                        );
                        let out_r = blend_r * s_a + d_r * (1.0 - s_a);
                        let out_g = blend_g * s_a + d_g * (1.0 - s_a);
                        let out_b = blend_b * s_a + d_b * (1.0 - s_a);
                        buf[di]       = (out_b * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                        buf[di + 1]   = (out_g * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                        buf[di + 2]   = (out_r * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                        // dst alpha preserved
                    }
                }
            }
        }
    }
}

#[inline]
fn blend_color_only(dst: &mut [u8], sb: u8, sg: u8, sr: u8, sa: u8) {
    let sa = sa as u32;
    if sa >= 255 { dst[0] = sb; dst[1] = sg; dst[2] = sr; return; }
    let inv = 255 - sa;
    dst[0] = ((sb as u32 * sa + dst[0] as u32 * inv + 127) / 255) as u8;
    dst[1] = ((sg as u32 * sa + dst[1] as u32 * inv + 127) / 255) as u8;
    dst[2] = ((sr as u32 * sa + dst[2] as u32 * inv + 127) / 255) as u8;
}

// ── Group compositing ────────────────────────────────────────────────────────

fn composite_group_onto(
    dst: &mut [u8],
    dst_stride: i32,
    tile_x: i32,
    tile_y: i32,
    tile_w: i32,
    tile_h: i32,
    group_li: usize,
    metas: &[LayerMeta],
    doc: &mut DrawingDocument,
    opacity_scale: f64,
) {
    let group_opacity = metas[group_li].opacity * opacity_scale;
    if group_opacity <= 0.0 { return; }

    let children: Vec<usize> = (0..metas.len())
        .filter(|&i| metas[i].parent_group == group_li as i32)
        .collect();
    if children.is_empty() { return; }

    let child_stack = build_sibling_stack(&children, metas);

    if metas[group_li].blend == BlendMode::PassThrough {
        composite_sibling_stack(dst, dst_stride, tile_x, tile_y, tile_w, tile_h, &child_stack, metas, doc, group_opacity);
    } else {
        // Isolated group: flatten children into temp, merge with group blend+opacity
        let len = (tile_w * tile_h * 4) as usize;
        let mut temp = vec![0u8; len];
        composite_sibling_stack(&mut temp, tile_w, tile_x, tile_y, tile_w, tile_h, &child_stack, metas, doc, 1.0);
        merge_buffer_onto(dst, dst_stride, tile_x, tile_y, tile_w, tile_h, &temp, metas[group_li].blend, group_opacity);
    }
}

fn composite_group_into_alpha_preserving(
    buf: &mut [u8],
    tile_x: i32,
    tile_y: i32,
    tile_w: i32,
    tile_h: i32,
    group_li: usize,
    metas: &[LayerMeta],
    doc: &mut DrawingDocument,
) {
    if !metas[group_li].is_group { return; }

    let children: Vec<usize> = (0..metas.len())
        .filter(|&i| metas[i].parent_group == group_li as i32)
        .collect();
    if children.is_empty() { return; }

    let child_stack = build_sibling_stack(&children, metas);

    if metas[group_li].blend == BlendMode::PassThrough {
        for item in &child_stack {
            let clip_li = item.layer_index;
            if !metas[clip_li].visible { continue; }
            if metas[clip_li].is_group {
                composite_group_into_alpha_preserving(buf, tile_x, tile_y, tile_w, tile_h, clip_li, metas, doc);
            } else {
                composite_layer_alpha_preserving(buf, tile_x, tile_y, tile_w, tile_h, clip_li, metas, doc);
            }
        }
    } else {
        // Isolated clip group: flatten children into temp, then alpha-preserving merge
        let len = (tile_w * tile_h * 4) as usize;
        let mut temp = vec![0u8; len];
        composite_sibling_stack(&mut temp, tile_w, tile_x, tile_y, tile_w, tile_h, &child_stack, metas, doc, 1.0);

        // Alpha-preserving merge of temp onto buf
        let blend = metas[group_li].blend;
        let opacity_byte = (metas[group_li].opacity * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
        for y in 0..tile_h as usize {
            for x in 0..tile_w as usize {
                let i = (y * tile_w as usize + x) * 4;
                let raw_a = temp[i + 3];
                if raw_a == 0 { continue; }
                let sa = (raw_a as u32 * opacity_byte as u32 + 127) / 255;
                if sa == 0 { continue; }

                if blend == BlendMode::Normal {
                    blend_color_only(&mut buf[i..i + 4], temp[i], temp[i + 1], temp[i + 2], sa as u8);
                } else {
                    let s_r = temp[i + 2] as f64 / 255.0;
                    let s_g = temp[i + 1] as f64 / 255.0;
                    let s_b = temp[i] as f64 / 255.0;
                    let s_a = sa as f64 / 255.0;
                    let d_r = buf[i + 2] as f64 / 255.0;
                    let d_g = buf[i + 1] as f64 / 255.0;
                    let d_b = buf[i] as f64 / 255.0;
                    let d_a = buf[i + 3] as f64 / 255.0;

                    let (blend_r, blend_g, blend_b) = floss_core::blend::apply_blend_mode(
                        s_r, s_g, s_b, s_a, d_r, d_g, d_b, d_a, blend,
                    );
                    let out_r = blend_r * s_a + d_r * (1.0 - s_a);
                    let out_g = blend_g * s_a + d_g * (1.0 - s_a);
                    let out_b = blend_b * s_a + d_b * (1.0 - s_a);
                    buf[i]       = (out_b * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                    buf[i + 1]   = (out_g * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                    buf[i + 2]   = (out_r * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                }
            }
        }
    }
}

// ── Buffer merge ─────────────────────────────────────────────────────────────

fn merge_buffer_onto(
    dst: &mut [u8],
    dst_stride: i32,
    _bx: i32,
    _by: i32,
    bw: i32,
    bh: i32,
    src: &[u8],
    blend: BlendMode,
    opacity: f64,
) {
    let opacity_byte = (opacity * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
    let stride = dst_stride as usize;

    if blend == BlendMode::Normal {
        for y in 0..bh as usize {
            for x in 0..bw as usize {
                let si = (y * bw as usize + x) * 4;
                let sa_raw = src[si + 3];
                if sa_raw == 0 { continue; }
                let sa = (sa_raw as u32 * opacity_byte as u32 + 127) / 255;
                if sa == 0 { continue; }
                let di = (y * stride + x) * 4;
                blit_pixel(&mut dst[di..di + 4], &src[si..si + 4], sa);
            }
        }
    } else {
        for y in 0..bh as usize {
            for x in 0..bw as usize {
                let si = (y * bw as usize + x) * 4;
                let sa_raw = src[si + 3];
                if sa_raw == 0 { continue; }
                let sa_f = sa_raw as f64 / 255.0 * opacity;
                if sa_f <= 0.0 { continue; }

                let di = (y * stride + x) * 4;
                let (out_r, out_g, out_b, out_a) = floss_core::blend::blend_pixel_float(
                    src[si + 2] as f64 / 255.0,
                    src[si + 1] as f64 / 255.0,
                    src[si] as f64 / 255.0,
                    sa_f,
                    dst[di + 2] as f64 / 255.0,
                    dst[di + 1] as f64 / 255.0,
                    dst[di] as f64 / 255.0,
                    dst[di + 3] as f64 / 255.0,
                    blend,
                );
                dst[di]       = (out_b * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                dst[di + 1]   = (out_g * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                dst[di + 2]   = (out_r * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
                dst[di + 3]   = (out_a * 255.0 + 0.5).clamp(0.0, 255.0) as u8;
            }
        }
    }
}

#[inline]
fn blit_pixel(dst: &mut [u8], src: &[u8], sa: u32) {
    let da = dst[3] as u32;
    if da == 0 {
        dst[0] = ((src[0] as u32 * sa + 127) / 255) as u8;
        dst[1] = ((src[1] as u32 * sa + 127) / 255) as u8;
        dst[2] = ((src[2] as u32 * sa + 127) / 255) as u8;
        dst[3] = sa as u8;
        return;
    }
    let dc = (da * (255 - sa) + 127) / 255;
    let oa = sa + dc;
    if oa == 0 { return; }
    let h = oa >> 1;
    dst[0] = ((src[0] as u32 * sa + dst[0] as u32 * dc + h) / oa) as u8;
    dst[1] = ((src[1] as u32 * sa + dst[1] as u32 * dc + h) / oa) as u8;
    dst[2] = ((src[2] as u32 * sa + dst[2] as u32 * dc + h) / oa) as u8;
    dst[3] = oa as u8;
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use floss_core::{BlendMode, Color};
    use floss_document::DrawingDocument;
    use floss_core::Rect;

    /// Fill a document layer with constant color.
    fn fill_layer(doc: &mut DrawingDocument, li: usize, r: u8, g: u8, b: u8, a: u8) {
        let w = doc.width();
        let h = doc.height();
        doc.layer_mut(li).pixels.fill_solid(Rect::new(0, 0, w, h), b, g, r, a);
    }

    /// Get a pixel from the compositor output (BGRA).
    fn get_pixel(rgba: &[u8], w: usize, x: usize, y: usize) -> (u8, u8, u8, u8) {
        let i = (y * w + x) * 4;
        (rgba[i+2], rgba[i+1], rgba[i], rgba[i+3]) // RGBA order
    }

    fn make_doc(w: i32, h: i32) -> DrawingDocument {
        DrawingDocument::new(w, h)
    }

    // ── Basic compositing ─────────────────────────────────────────────────

    #[test]
    fn test_single_opaque_layer() {
        let mut doc = make_doc(128, 128);
        fill_layer(&mut doc, 0, 255, 0, 0, 255); // Red
        let mut comp = LayerCompositor::new(128, 128);
        let out = comp.composite(&mut doc).to_vec();
        let (r, g, b, a) = get_pixel(&out, 128, 64, 64);
        assert_eq!((r, g, b, a), (255, 0, 0, 255));
    }

    #[test]
    fn test_layer_opacity() {
        let mut doc = make_doc(64, 64);
        fill_layer(&mut doc, 0, 255, 0, 0, 128); // Half-alpha red
        doc.layer_mut(0).opacity = 0.5;
        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        let (r, _, _, a) = get_pixel(&out, 64, 32, 32);
        // 50% opacity of 50% red over white paper → ~63 alpha red
        assert!(r > 100 && a > 60, "got r={r} a={a}");
    }

    #[test]
    fn test_two_layers_over() {
        let mut doc = make_doc(64, 64);
        fill_layer(&mut doc, 0, 255, 0, 0, 255); // Bottom: red
        doc.add_layer();
        fill_layer(&mut doc, 1, 0, 0, 255, 128); // Top: half-blue
        doc.layer_mut(1).opacity = 0.5;
        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        let (r, _g, b, _a) = get_pixel(&out, 64, 32, 32);
        assert!(r > 100, "Red should show through, got {r}");
        assert!(b > 50, "Blue on top, got {b}");
    }

    #[test]
    fn test_layer_visibility() {
        let mut doc = make_doc(64, 64);
        fill_layer(&mut doc, 0, 255, 0, 0, 255);
        doc.add_layer();
        fill_layer(&mut doc, 1, 0, 255, 0, 255); // Green
        doc.layer_mut(1).visible = false;
        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        let (r, g, _b, _a) = get_pixel(&out, 64, 32, 32);
        assert_eq!((r, g), (255, 0), "Hidden green layer should not affect red");
    }

    #[test]
    fn test_layer_offset() {
        let mut doc = make_doc(128, 128);
        fill_layer(&mut doc, 0, 0, 255, 0, 255); // Green bg
        doc.add_layer();
        // Small blue square at offset (32, 32), 64x64
        let w = 64; let h = 64;
        for y in 0..h { for x in 0..w {
            doc.layer_mut(1).pixels.set_pixel(x, y, 255, 0, 0, 255);
        }}
        doc.layer_mut(1).offset_x = 32;
        doc.layer_mut(1).offset_y = 32;

        let mut comp = LayerCompositor::new(128, 128);
        let out = comp.composite(&mut doc).to_vec();

        // Center of blue square
        let (r, g, b, _) = get_pixel(&out, 128, 64, 64);
        assert_eq!((r, g, b), (0, 0, 255), "Center should be blue");
        // Top-left corner is just green bg
        let (r, g, b, _) = get_pixel(&out, 128, 16, 16);
        assert_eq!((r, g, b), (0, 255, 0), "Corner should be green bg");
    }

    // ── Clipping masks ────────────────────────────────────────────────────

    #[test]
    fn test_clipping_mask() {
        let mut base = floss_document::DrawingLayer::new("Base", 64, 64);
        let cx = 32; let cy = 32; let r = 20;
        for y in (cy-r)..(cy+r) { for x in (cx-r)..(cx+r) {
            let dist = ((cx - x) * (cx - x) + (cy - y) * (cy - y)) as f64;
            if dist.sqrt() < r as f64 {
                base.pixels.set_pixel(x, y, 0, 0, 255, 255);
            }
        }}

        let mut clip = floss_document::DrawingLayer::new("Clip", 64, 64);
        clip.pixels.fill_solid(Rect::new(0, 0, 64, 64), 0, 255, 0, 255);
        clip.is_clipping = true;

        let mut doc = DrawingDocument::new(64, 64);
        doc.replace_for_import(64, 64, Color::from_bytes(255, 255, 255, 255), vec![base, clip], 0);

        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();

        // Inside circle: green on red
        let (_, g, _, _) = get_pixel(&out, 64, cx as usize, cy as usize);
        assert!(g > 200, "Inside circle should be green, got g={g}");

        // Outside: white paper
        let (r2, g2, _, a2) = get_pixel(&out, 64, 5, 5);
        assert!(r2 > 250 && g2 > 250 && a2 > 250, "Outside circle should be white paper, got r={r2} g={g2} a={a2}");
    }

    // ── Groups ────────────────────────────────────────────────────────────

    #[test]
    fn test_isolated_group() {
        let mut doc = DrawingDocument::new(64, 64);
        // Build layers manually then replace_for_import
        let mut layers = Vec::new();

        // Group (index 0)
        let mut group = floss_document::DrawingLayer::new("Group", 64, 64);
        group.is_group = true;
        group.blend_mode = BlendMode::Normal;
        layers.push(group);

        // Child 1 (index 1): red
        let mut child1 = floss_document::DrawingLayer::new("Child 1", 64, 64);
        child1.parent_group = 0;
        child1.indent_level = 1;
        child1.pixels.fill_solid(Rect::new(0, 0, 64, 64), 0, 0, 255, 128);
        layers.push(child1);

        // Child 2 (index 2): blue
        let mut child2 = floss_document::DrawingLayer::new("Child 2", 64, 64);
        child2.parent_group = 0;
        child2.indent_level = 1;
        child2.pixels.fill_solid(Rect::new(0, 0, 64, 64), 255, 0, 0, 128);
        layers.push(child2);

        doc.replace_for_import(64, 64, Color::from_bytes(255, 255, 255, 255), layers, 0);

        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        let (r, g, b, a) = get_pixel(&out, 64, 32, 32);
        assert!(a > 0, "Group should composite, got a={a} r={r} g={g} b={b}");
        assert!(r > 60 && r < 200, "Red+blue = magenta, got r={r} b={b}");
        assert!(b > 60 && b < 200, "Red+blue = magenta, got r={r} b={b}");
    }

    #[test]
    fn test_pass_through_group() {
        let mut layers = Vec::new();

        // Bottom layer (root, not in group)
        let mut bg = floss_document::DrawingLayer::new("BG", 64, 64);
        bg.pixels.fill_solid(Rect::new(0, 0, 64, 64), 0, 0, 255, 255);
        layers.push(bg);

        // Group (pass-through)
        let mut group = floss_document::DrawingLayer::new("PT Group", 64, 64);
        group.is_group = true;
        group.blend_mode = BlendMode::PassThrough;
        group.opacity = 0.5;
        layers.push(group);

        // Child in group
        let mut child = floss_document::DrawingLayer::new("Child", 64, 64);
        child.parent_group = 1;
        child.indent_level = 1;
        child.pixels.fill_solid(Rect::new(0, 0, 64, 64), 255, 0, 0, 255);
        layers.push(child);

        let mut doc = DrawingDocument::new(64, 64);
        doc.replace_for_import(64, 64, Color::from_bytes(255, 255, 255, 255), layers, 0);
        doc.active_layer_index(); // build

        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        let (r, g, b, a) = get_pixel(&out, 64, 32, 32);
        // BG=red (255,0,0), child=blue (0,0,255) at 50% pass-through
        // Result: ~127 red + ~128 blue = magenta-ish
        assert!(a > 0, "Pass-through group should composite, got r={r} g={g} b={b} a={a}");
        assert!(r > 60 && b > 60, "Red+blue pass-through, got r={r} g={g} b={b} a={a}");
    }

    #[test]
    fn test_nested_groups() {
        let mut layers = Vec::new();

        // Outer group (0)
        let mut outer = floss_document::DrawingLayer::new("Outer", 64, 64);
        outer.is_group = true;
        layers.push(outer);

        // Child in outer: red (1)
        let mut red = floss_document::DrawingLayer::new("Red", 64, 64);
        red.parent_group = 0;
        red.indent_level = 1;
        red.pixels.fill_solid(Rect::new(0, 0, 64, 64), 0, 0, 255, 255);
        layers.push(red);

        // Inner group (2)
        let mut inner = floss_document::DrawingLayer::new("Inner", 64, 64);
        inner.is_group = true;
        inner.parent_group = 0;
        inner.indent_level = 1;
        layers.push(inner);

        // Child in inner: blue (3)
        let mut blue = floss_document::DrawingLayer::new("Blue", 64, 64);
        blue.parent_group = 2;
        blue.indent_level = 2;
        blue.pixels.fill_solid(Rect::new(0, 0, 64, 64), 255, 0, 0, 128);
        layers.push(blue);

        let mut doc = DrawingDocument::new(64, 64);
        doc.replace_for_import(64, 64, Color::from_bytes(255, 255, 255, 255), layers, 0);

        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        let (r, _g, b, a) = get_pixel(&out, 64, 32, 32);
        assert!(a > 0, "Nested group should composite, got a={a} r={r} b={b}");
        assert!(r > 80, "Red from outer child visible, got r={r}");
        assert!(b > 30, "Blue from inner child visible, got b={b}");
    }

    // ── Blend modes ───────────────────────────────────────────────────────

    #[test]
    fn test_multiply_blend() {
        let mut doc = make_doc(64, 64);
        fill_layer(&mut doc, 0, 128, 128, 128, 255);
        doc.add_layer();
        fill_layer(&mut doc, 1, 255, 0, 0, 128);
        doc.layer_mut(1).blend_mode = BlendMode::Multiply;
        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        // Multiply: gray * red = darker with red tinge
        let (r, g, _, _) = get_pixel(&out, 64, 32, 32);
        assert!(r < 200 && g < 200, "Multiply should darken, got r={r} g={g}");
    }

    #[test]
    fn test_screen_blend() {
        let mut doc = make_doc(64, 64);
        fill_layer(&mut doc, 0, 64, 64, 64, 255);
        doc.add_layer();
        fill_layer(&mut doc, 1, 255, 0, 0, 128);
        doc.layer_mut(1).blend_mode = BlendMode::Screen;
        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        // Screen should lighten
        let (r, _, _, _) = get_pixel(&out, 64, 32, 32);
        assert!(r > 64, "Screen should lighten, got r={r}");
    }

    // ── Incremental compositing ───────────────────────────────────────────

    #[test]
    fn test_invalidate_region() {
        let mut doc = make_doc(128, 128);
        fill_layer(&mut doc, 0, 255, 255, 255, 255);
        let mut comp = LayerCompositor::new(128, 128);
        let _ = comp.composite(&mut doc);

        // Change only top-left 32x32
        fill_layer(&mut doc, 0, 255, 0, 0, 255); // Full red
        comp.invalidate(Some(Rect::new(0, 0, 64, 64)));
        let out = comp.composite(&mut doc).to_vec();

        // Top-left should be red now
        let (r, _, _, _) = get_pixel(&out, 128, 16, 16);
        assert_eq!(r, 255);
    }

    #[test]
    fn test_paper_color() {
        let mut doc = make_doc(64, 64);
        doc.set_paper_color(Color::from_bytes(0, 255, 255, 255)); // Cyan paper
        let mut comp = LayerCompositor::new(64, 64);
        let out = comp.composite(&mut doc).to_vec();
        let (r, g, b, a) = get_pixel(&out, 64, 32, 32);
        assert_eq!((r, g, b, a), (0, 255, 255, 255), "Paper should be cyan");
    }
}
