//! DrawingDocument — the top-level model for the canvas.
//!
//! Owns the layer stack, selection, undo/redo history, and canvas size.
//! This is the authoritative source of truth for all document state.

use std::collections::HashMap;

use floss_core::{Color, Rect};

use crate::history::{History, HistoryCommand, LayerPropertySnapshot, LayerSnapshot, SelectionSnapshot};
use crate::layer::DrawingLayer;
use crate::selection::SelectionMask;

/// Manages the document: layers, selection, history, and canvas size.
pub struct DrawingDocument {
    /// Layers from bottom to top.
    layers: Vec<DrawingLayer>,
    /// Index of the active (selected) layer.
    active_layer: usize,
    /// Current selection mask (None = select all).
    selection: SelectionMask,
    /// Undo/redo history.
    history: History,
    /// Canvas width in pixels.
    width: i32,
    /// Canvas height in pixels.
    height: i32,
    /// Canvas paper color.
    paper_color: Color,
    /// Document has unsaved changes.
    dirty: bool,
}

impl DrawingDocument {
    /// Create a new document with one empty layer.
    pub fn new(width: i32, height: i32) -> Self {
        let initial_layer = DrawingLayer::new("Background", width, height);
        Self {
            layers: vec![initial_layer],
            active_layer: 0,
            selection: SelectionMask::empty(),
            history: History::new(256),
            width: width.max(1),
            height: height.max(1),
            paper_color: Color::from_bytes(255, 255, 255, 255),
            dirty: false,
        }
    }

    // ── Canvas size ───────────────────────────────────────────────────────

    pub fn width(&self) -> i32 { self.width }
    pub fn height(&self) -> i32 { self.height }
    pub fn paper_color(&self) -> Color { self.paper_color }
    pub fn layers(&self) -> &[DrawingLayer] { &self.layers }

    pub fn resize(&mut self, width: i32, height: i32) {
        self.width = width.max(1);
        self.height = height.max(1);
        for layer in &mut self.layers {
            layer.pixels.resize(self.width, self.height);
        }
        self.mark_dirty();
    }

    // ── Layers ────────────────────────────────────────────────────────────

    pub fn layer_count(&self) -> usize {
        self.layers.len()
    }

    pub fn active_layer_index(&self) -> usize {
        self.active_layer
    }

    pub fn active_layer(&self) -> &DrawingLayer {
        &self.layers[self.active_layer]
    }

    pub fn active_layer_mut(&mut self) -> &mut DrawingLayer {
        &mut self.layers[self.active_layer]
    }

    /// Returns true if the active layer can be painted on.
    pub fn can_paint_active_layer(&self) -> bool {
        self.active_layer().can_paint()
    }

    /// Get a reference to any layer by index.
    pub fn layer(&self, index: usize) -> &DrawingLayer {
        &self.layers[index]
    }

    /// Get a mutable reference to any layer.
    pub fn layer_mut(&mut self, index: usize) -> &mut DrawingLayer {
        &mut self.layers[index]
    }

    /// Set the active layer.
    pub fn set_active_layer(&mut self, index: usize) {
        if index < self.layers.len() {
            self.active_layer = index;
        }
    }

    pub fn set_paper_color(&mut self, color: Color) {
        self.paper_color = color;
        self.mark_dirty();
    }

    pub fn replace_for_import(
        &mut self,
        width: i32,
        height: i32,
        paper_color: Color,
        layers: Vec<DrawingLayer>,
        active_layer: usize,
    ) {
        self.width = width.max(1);
        self.height = height.max(1);
        self.paper_color = paper_color;
        self.layers = if layers.is_empty() {
            vec![DrawingLayer::new("Background", self.width, self.height)]
        } else {
            layers
        };
        self.active_layer = active_layer.min(self.layers.len().saturating_sub(1));
        self.selection.clear();
        self.history.clear();
        self.mark_clean();
    }

    /// Add a new empty layer above the active layer.
    pub fn add_layer(&mut self) {
        let new_layer = DrawingLayer::new(
            format!("Layer {}", self.layers.len()),
            self.width,
            self.height,
        );
        let insert_at = self.active_layer + 1;
        self.layers.insert(insert_at, new_layer);
        self.active_layer = insert_at;
        self.rebuild_parent_group_indices();
        let snapshot = self.snapshot_layer_range(insert_at, insert_at + 1);
        self.history.push(HistoryCommand::AddLayer { index: insert_at, layer_data: snapshot });
        self.mark_dirty();
    }

    /// Add a group layer.
    pub fn add_group_layer(&mut self) {
        let mut group = DrawingLayer::new(
            format!("Group {}", self.layers.len()),
            self.width,
            self.height,
        );
        group.is_group = true;
        let insert_at = self.active_layer + 1;
        self.layers.insert(insert_at, group);
        self.active_layer = insert_at;
        self.rebuild_parent_group_indices();
        let snapshot = self.snapshot_layer_range(insert_at, insert_at + 1);
        self.history.push(HistoryCommand::AddLayer { index: insert_at, layer_data: snapshot });
        self.mark_dirty();
    }

    /// Add a white-filled background layer.
    pub fn add_background_layer(&mut self) {
        let mut bg = DrawingLayer::new("Paper", self.width, self.height);
        bg.pixels.fill_solid(
            Rect::new(0, 0, self.width, self.height),
            255, 255, 255, 255,
        );
        bg.is_paper = true;
        self.layers.insert(0, bg);
        self.active_layer = 1.min(self.layers.len() - 1);
        self.rebuild_parent_group_indices();
        let snapshot = self.snapshot_layer_range(0, 1);
        self.history.push(HistoryCommand::AddLayer { index: 0, layer_data: snapshot });
        self.mark_dirty();
    }

    /// Duplicate the active layer.
    pub fn duplicate_active_layer(&mut self) {
        let range = self.subtree_range(self.active_layer);
        let insert_at = range.end;
        let snapshots = self.snapshot_layer_range(range.start, range.end);
        let cloned = self.duplicate_snapshot_range(&snapshots, range.start, range.end, insert_at);
        self.insert_layer_snapshots(insert_at, cloned.clone());
        self.rebuild_parent_group_indices();
        self.active_layer = insert_at;
        self.history.push(HistoryCommand::AddLayer { index: insert_at, layer_data: cloned });
        self.mark_dirty();
    }

    /// Delete the active layer. Refuses if only one layer remains.
    pub fn delete_active_layer(&mut self) {
        if self.layers.len() <= 1 {
            return;
        }

        let range = self.subtree_range(self.active_layer);
        if range.len() >= self.layers.len() {
            return;
        }
        let snapshot = self.snapshot_layer_range(range.start, range.end);
        self.layers.drain(range.clone());
        self.rebuild_parent_group_indices();
        self.history.push(HistoryCommand::RemoveLayer {
            index: range.start,
            layer_data: snapshot,
        });

        self.active_layer = range.start.min(self.layers.len() - 1);
        self.mark_dirty();
    }

    /// Move a layer from one index to another.
    pub fn move_layer(&mut self, from: usize, to: usize) {
        if from == to || from >= self.layers.len() || to > self.layers.len() {
            return;
        }
        let from_range = self.subtree_range(from);
        let count = from_range.len();
        let insert_at = if to > from_range.start { to.saturating_sub(count) } else { to };
        if insert_at == from_range.start {
            return;
        }
        self.history.push(HistoryCommand::MoveLayer {
            from_index: from_range.start,
            to_index: insert_at,
            count,
        });
        let block: Vec<_> = self.layers.drain(from_range.clone()).collect();
        for (offset, layer) in block.into_iter().enumerate() {
            self.layers.insert(insert_at + offset, layer);
        }
        self.rebuild_parent_group_indices();
        self.active_layer = insert_at;
        self.mark_dirty();
    }

    /// Group selected layers together.
    pub fn group_selected_layers(&mut self, indices: &[usize]) {
        if indices.len() < 2 {
            return;
        }
        // Create a group layer at the position of the lowest index
        let group_idx = *indices.iter().min().unwrap();
        let mut group = DrawingLayer::new(
            format!("Group {}", self.layers.len()),
            self.width,
            self.height,
        );
        group.is_group = true;

        // Move layers into the group by setting their parent_group
        self.layers.insert(group_idx, group);
        // Set parent_group for the next layers (they shifted by 1)
        for &idx in indices.iter().rev() {
            let adjusted = if idx >= group_idx { idx + 1 } else { idx };
            if adjusted < self.layers.len() {
                self.layers[adjusted].parent_group = group_idx as i32;
                self.layers[adjusted].indent_level = 1;
            }
        }
        self.rebuild_parent_group_indices();
        let snapshot = self.snapshot_layer_range(group_idx, group_idx + 1);
        self.history.push(HistoryCommand::AddLayer { index: group_idx, layer_data: snapshot });
        self.mark_dirty();
    }

    // ── Selection ─────────────────────────────────────────────────────────

    pub fn has_selection(&self) -> bool {
        self.selection.has_selection()
    }

    pub fn selection(&self) -> &SelectionMask {
        &self.selection
    }

    pub fn selection_mut(&mut self) -> &mut SelectionMask {
        &mut self.selection
    }

    pub fn clear_selection(&mut self) {
        let before = if self.selection.has_selection() {
            Some(self.selection.capture_snapshot())
        } else {
            None
        };
        self.selection.clear();
        self.history.push(HistoryCommand::SelectionChange {
            before,
            after: None,
        });
    }

    pub fn commit_selection_mutation(&mut self, before: SelectionSnapshot) {
        let after = if self.selection.has_selection() {
            Some(self.selection.capture_snapshot())
        } else {
            None
        };
        self.history.push(HistoryCommand::SelectionChange { before: Some(before), after });
    }

    // ── Paint operations ──────────────────────────────────────────────────

    /// Record a paint mutation to the active layer for undo.
    /// Call this BEFORE painting, with a snapshot of the affected region.
    pub fn record_paint_region(&mut self, region: Rect) -> HashMap<(i32, i32), Box<[u8; 64 * 64 * 4]>> {
        self.active_layer_mut().pixels.capture_region(region)
    }

    pub fn commit_paint_region(
        &mut self,
        region: Rect,
        before_tiles: HashMap<(i32, i32), Box<[u8; 64 * 64 * 4]>>,
    ) {
        let after_tiles = self.active_layer_mut().pixels.capture_region(region);
        if before_tiles != after_tiles {
            self.history.push(HistoryCommand::PaintTiles {
                layer_index: self.active_layer,
                before_tiles,
                after_tiles,
            });
            self.mark_dirty();
        }
    }

    /// Clear the active layer.
    pub fn clear_active_layer(&mut self) {
        if !self.can_paint_active_layer() {
            return;
        }
        let tiles = self.layers[self.active_layer].pixels.capture_tiles();
        self.history.push(HistoryCommand::ClearActiveLayer {
            index: self.active_layer,
            tiles,
        });
        self.layers[self.active_layer].pixels.clear_all();
        self.mark_dirty();
    }

    // ── Undo / Redo ───────────────────────────────────────────────────────

    pub fn can_undo(&self) -> bool {
        self.history.can_undo()
    }

    pub fn can_redo(&self) -> bool {
        self.history.can_redo()
    }

    /// Undo the most recent operation.
    pub fn undo(&mut self) {
        let cmd = match self.history.pop_undo() {
            Some(c) => c,
            None => return,
        };
        self.apply_reverse(cmd);
    }

    /// Redo the most recently undone operation.
    pub fn redo(&mut self) {
        let cmd = match self.history.pop_redo() {
            Some(c) => c,
            None => return,
        };
        self.apply_forward(cmd);
    }

    fn apply_reverse(&mut self, cmd: HistoryCommand) {
        match cmd {
            HistoryCommand::PaintTiles { layer_index, before_tiles, after_tiles } => {
                if layer_index < self.layers.len() {
                    self.layers[layer_index].pixels.restore_tiles(before_tiles.clone());
                }
                self.history.push_redo(HistoryCommand::PaintTiles { layer_index, before_tiles, after_tiles });
            }
            HistoryCommand::AddLayer { index, layer_data } => {
                let remove_end = index.saturating_add(layer_data.len()).min(self.layers.len());
                if index < remove_end {
                    self.layers.drain(index..remove_end);
                    self.rebuild_parent_group_indices();
                    self.history.push_redo(HistoryCommand::AddLayer { index, layer_data });
                    if !self.layers.is_empty() {
                        self.active_layer = self.active_layer.min(self.layers.len().saturating_sub(1));
                    }
                }
            }
            HistoryCommand::RemoveLayer { index, layer_data } => {
                self.insert_layer_snapshots(index, layer_data.clone());
                self.rebuild_parent_group_indices();
                self.history.push_redo(HistoryCommand::RemoveLayer { index, layer_data });
                self.active_layer = index;
            }
            HistoryCommand::LayerPropertyChange { index, ref before, .. } => {
                if index < self.layers.len() {
                    self.apply_layer_properties(index, &before);
                    self.history.push_redo(cmd.clone());
                }
            }
            HistoryCommand::MoveLayer { from_index, to_index, count } => {
                // Reverse: move back
                if to_index < self.layers.len() && count > 0 {
                    let end = (to_index + count).min(self.layers.len());
                    let block: Vec<_> = self.layers.drain(to_index..end).collect();
                    for (offset, layer) in block.into_iter().enumerate() {
                        self.layers.insert(from_index + offset, layer);
                    }
                    self.rebuild_parent_group_indices();
                    self.active_layer = from_index;
                    self.history.push_redo(cmd.clone());
                }
            }
            HistoryCommand::SelectionChange { ref before, .. } => {
                match before {
                    Some(snap) => self.selection.restore_snapshot(snap.clone()),
                    None => self.selection.clear(),
                }
                self.history.push_redo(cmd.clone());
            }
            HistoryCommand::ClearActiveLayer { index, tiles } => {
                if index < self.layers.len() {
                    self.layers[index].pixels.restore_tiles(tiles.clone());
                    self.history.push_redo(HistoryCommand::ClearActiveLayer { index, tiles });
                }
            }
        }
        self.mark_dirty();
    }

    fn apply_forward(&mut self, cmd: HistoryCommand) {
        // Redo: apply the original command forward, push reverse to undo.
        match cmd {
            HistoryCommand::PaintTiles { layer_index, before_tiles, after_tiles } => {
                if layer_index < self.layers.len() {
                    self.layers[layer_index].pixels.restore_tiles(after_tiles.clone());
                    self.history.push_undo(HistoryCommand::PaintTiles { layer_index, before_tiles, after_tiles });
                }
            }
            // ── Redo: apply the original operation forward ─────────────────
            HistoryCommand::RemoveLayer { index, layer_data } => {
                let remove_end = index.saturating_add(layer_data.len()).min(self.layers.len());
                if index < remove_end {
                    self.layers.drain(index..remove_end);
                    self.rebuild_parent_group_indices();
                    self.history.push_undo(HistoryCommand::RemoveLayer { index, layer_data });
                    if !self.layers.is_empty() && self.active_layer >= self.layers.len() {
                        self.active_layer = self.layers.len().saturating_sub(1);
                    }
                }
            }
            HistoryCommand::AddLayer { index, layer_data } => {
                self.insert_layer_snapshots(index, layer_data.clone());
                self.rebuild_parent_group_indices();
                self.history.push_undo(HistoryCommand::AddLayer { index, layer_data });
                self.active_layer = index;
            }
            HistoryCommand::LayerPropertyChange { index, before: _, ref after } => {
                if index < self.layers.len() {
                    self.apply_layer_properties(index, &after);
                    self.history.push_undo(cmd.clone());
                }
            }
            HistoryCommand::MoveLayer { from_index, to_index, count } => {
                if from_index < self.layers.len() && count > 0 {
                    let end = (from_index + count).min(self.layers.len());
                    let block: Vec<_> = self.layers.drain(from_index..end).collect();
                    for (offset, layer) in block.into_iter().enumerate() {
                        self.layers.insert(to_index + offset, layer);
                    }
                    self.rebuild_parent_group_indices();
                    self.active_layer = to_index;
                    self.history.push_undo(cmd.clone());
                }
            }
            HistoryCommand::SelectionChange { ref after, .. } => {
                match after {
                    Some(snap) => self.selection.restore_snapshot(snap.clone()),
                    None => self.selection.clear(),
                }
                self.history.push_undo(cmd.clone());
            }
            HistoryCommand::ClearActiveLayer { index, tiles } => {
                if index < self.layers.len() {
                    self.layers[index].pixels.clear_all();
                    self.history.push_undo(HistoryCommand::ClearActiveLayer { index, tiles });
                }
            }
        }
        self.mark_dirty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    fn snapshot_layer_at(&mut self, index: usize) -> LayerSnapshot {
        let layer = &mut self.layers[index];
        let pixel_snapshot = layer.pixels.capture_tiles();
        LayerSnapshot {
            name: layer.name.clone(),
            visible: layer.visible,
            locked: layer.locked,
            blend_mode: layer.blend_mode,
            opacity: layer.opacity,
            layer_color: layer.layer_color,
            expression_color: layer.expression_color,
            offset_x: layer.offset_x,
            offset_y: layer.offset_y,
            is_group: layer.is_group,
            is_open: layer.is_open,
            is_clipping: layer.is_clipping,
            is_alpha_locked: layer.is_alpha_locked,
            is_reference: layer.is_reference,
            is_paper: layer.is_paper,
            indent_level: layer.indent_level,
            parent_group: layer.parent_group,
            pixel_snapshot,
            width: self.width,
            height: self.height,
        }
    }

    fn snapshot_layer_range(&mut self, start: usize, end: usize) -> Vec<LayerSnapshot> {
        (start..end).map(|index| self.snapshot_layer_at(index)).collect()
    }

    fn insert_layer_snapshots(&mut self, index: usize, snapshots: Vec<LayerSnapshot>) {
        for (offset, snapshot) in snapshots.into_iter().enumerate() {
            let restored = self.restore_layer_from_snapshot(snapshot);
            self.layers.insert(index + offset, restored);
        }
    }

    fn duplicate_snapshot_range(
        &self,
        snapshots: &[LayerSnapshot],
        original_start: usize,
        original_end: usize,
        insert_at: usize,
    ) -> Vec<LayerSnapshot> {
        snapshots
            .iter()
            .enumerate()
            .map(|(offset, snapshot)| {
                let mut cloned = snapshot.clone();
                cloned.parent_group = if snapshot.parent_group < 0 {
                    -1
                } else if (original_start as i32..original_end as i32).contains(&snapshot.parent_group) {
                    insert_at as i32 + (snapshot.parent_group - original_start as i32)
                } else {
                    snapshot.parent_group
                };
                if offset == 0 {
                    cloned.name = format!("{} Copy", cloned.name);
                }
                cloned
            })
            .collect()
    }

    fn subtree_range(&self, index: usize) -> std::ops::Range<usize> {
        if index >= self.layers.len() {
            return index..index;
        }
        let indent = self.layers[index].indent_level;
        if !self.layers[index].is_group {
            return index..index + 1;
        }

        let mut end = index + 1;
        while end < self.layers.len() && self.layers[end].indent_level > indent {
            end += 1;
        }
        index..end
    }

    fn rebuild_parent_group_indices(&mut self) {
        let mut group_stack: Vec<usize> = Vec::new();
        for index in 0..self.layers.len() {
            let requested_indent = self.layers[index].indent_level.max(0) as usize;
            let resolved_indent = requested_indent.min(group_stack.len());
            self.layers[index].indent_level = resolved_indent as i32;
            while group_stack.len() > resolved_indent {
                group_stack.pop();
            }
            self.layers[index].parent_group = group_stack.last().copied().map(|v| v as i32).unwrap_or(-1);
            if self.layers[index].is_group {
                group_stack.push(index);
            }
        }
    }

    fn restore_layer_from_snapshot(&self, snap: LayerSnapshot) -> DrawingLayer {
        let mut layer = DrawingLayer::new(snap.name, snap.width, snap.height);
        layer.visible = snap.visible;
        layer.locked = snap.locked;
        layer.blend_mode = snap.blend_mode;
        layer.opacity = snap.opacity;
        layer.layer_color = snap.layer_color;
        layer.expression_color = snap.expression_color;
        layer.offset_x = snap.offset_x;
        layer.offset_y = snap.offset_y;
        layer.is_group = snap.is_group;
        layer.is_open = snap.is_open;
        layer.is_clipping = snap.is_clipping;
        layer.is_alpha_locked = snap.is_alpha_locked;
        layer.is_reference = snap.is_reference;
        layer.is_paper = snap.is_paper;
        layer.indent_level = snap.indent_level;
        layer.parent_group = snap.parent_group;
        layer.pixels.restore_tiles(snap.pixel_snapshot);
        layer
    }

    fn snapshot_layer_properties(&self, index: usize) -> LayerPropertySnapshot {
        let layer = &self.layers[index];
        LayerPropertySnapshot {
            name: layer.name.clone(),
            visible: layer.visible,
            locked: layer.locked,
            blend_mode: layer.blend_mode,
            opacity: layer.opacity,
            layer_color: layer.layer_color,
            expression_color: layer.expression_color,
            offset_x: layer.offset_x,
            offset_y: layer.offset_y,
            is_open: layer.is_open,
            is_clipping: layer.is_clipping,
            is_alpha_locked: layer.is_alpha_locked,
            is_reference: layer.is_reference,
            is_paper: layer.is_paper,
            indent_level: layer.indent_level,
        }
    }

    fn apply_layer_properties(&mut self, index: usize, props: &LayerPropertySnapshot) {
        let layer = &mut self.layers[index];
        layer.name = props.name.clone();
        layer.visible = props.visible;
        layer.locked = props.locked;
        layer.blend_mode = props.blend_mode;
        layer.opacity = props.opacity;
        layer.layer_color = props.layer_color;
        layer.expression_color = props.expression_color;
        layer.offset_x = props.offset_x;
        layer.offset_y = props.offset_y;
        layer.is_open = props.is_open;
        layer.is_clipping = props.is_clipping;
        layer.is_alpha_locked = props.is_alpha_locked;
        layer.is_reference = props.is_reference;
        layer.is_paper = props.is_paper;
        layer.indent_level = props.indent_level;
    }

    // ── Dirty tracking ────────────────────────────────────────────────────

    pub fn is_dirty(&self) -> bool {
        self.dirty
    }

    pub fn mark_clean(&mut self) {
        self.dirty = false;
    }

    pub fn mark_dirty(&mut self) {
        self.dirty = true;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn new_document_has_one_layer() {
        let doc = DrawingDocument::new(512, 512);
        assert_eq!(doc.layer_count(), 1);
        assert!(doc.can_paint_active_layer());
    }

    #[test]
    fn add_layer_increases_count() {
        let mut doc = DrawingDocument::new(512, 512);
        doc.add_layer();
        assert_eq!(doc.layer_count(), 2);
        assert_eq!(doc.active_layer_index(), 1);
    }

    #[test]
    fn cannot_delete_last_layer() {
        let mut doc = DrawingDocument::new(512, 512);
        assert_eq!(doc.layer_count(), 1);
        doc.delete_active_layer();
        assert_eq!(doc.layer_count(), 1);
    }

    #[test]
    fn undo_redo_add_layer() {
        let mut doc = DrawingDocument::new(256, 256);
        doc.add_layer();
        assert_eq!(doc.layer_count(), 2);
        doc.undo();
        assert_eq!(doc.layer_count(), 1);
        doc.redo();
        assert_eq!(doc.layer_count(), 2);
    }

    #[test]
    fn clear_active_layer_undo() {
        let mut doc = DrawingDocument::new(256, 256);
        doc.active_layer_mut().pixels.set_pixel(10, 10, 255, 0, 0, 255);
        assert!(doc.active_layer_mut().pixels.tile_count() > 0);
        doc.clear_active_layer();
        assert_eq!(doc.active_layer_mut().pixels.tile_count(), 0);
        doc.undo();
        assert!(doc.active_layer_mut().pixels.tile_count() > 0);
    }

    #[test]
    fn delete_layer_undo_redo_restores_pixels() {
        let mut doc = DrawingDocument::new(128, 128);
        doc.add_layer();
        doc.active_layer_mut().pixels.set_pixel(4, 7, 11, 22, 33, 255);
        doc.delete_active_layer();
        assert_eq!(doc.layer_count(), 1);

        doc.undo();
        assert_eq!(doc.layer_count(), 2);
        let px = doc.layer_mut(1).pixels.get_pixel(4, 7);
        assert_eq!(px, [11, 22, 33, 255]);

        doc.redo();
        assert_eq!(doc.layer_count(), 1);
    }

    #[test]
    fn delete_group_removes_entire_subtree_and_restores_on_undo() {
        let mut doc = DrawingDocument::new(128, 128);
        doc.add_group_layer();
        doc.add_layer();
        doc.layer_mut(2).indent_level = 1;
        doc.layer_mut(2).parent_group = 1;
        doc.layer_mut(2).pixels.set_pixel(8, 9, 77, 88, 99, 255);
        doc.set_active_layer(1);

        doc.delete_active_layer();
        assert_eq!(doc.layer_count(), 1);

        doc.undo();
        assert_eq!(doc.layer_count(), 3);
        assert!(doc.layer(1).is_group);
        assert_eq!(doc.layer(2).parent_group, 1);
        assert_eq!(doc.layer_mut(2).pixels.get_pixel(8, 9), [77, 88, 99, 255]);
    }

    #[test]
    fn duplicate_group_copies_descendants() {
        let mut doc = DrawingDocument::new(128, 128);
        doc.add_group_layer();
        doc.add_layer();
        doc.layer_mut(2).indent_level = 1;
        doc.layer_mut(2).parent_group = 1;
        doc.layer_mut(2).pixels.set_pixel(10, 10, 1, 2, 3, 255);
        doc.set_active_layer(1);

        doc.duplicate_active_layer();

        assert_eq!(doc.layer_count(), 5);
        assert!(doc.layer(3).is_group);
        assert_eq!(doc.layer(3).name, "Group 1 Copy");
        assert_eq!(doc.layer(4).parent_group, 3);
        assert_eq!(doc.layer_mut(4).pixels.get_pixel(10, 10), [1, 2, 3, 255]);
    }

    #[test]
    fn move_group_moves_entire_subtree() {
        let mut doc = DrawingDocument::new(128, 128);
        doc.add_group_layer();
        doc.add_layer();
        doc.layer_mut(2).indent_level = 1;
        doc.layer_mut(2).parent_group = 1;
        doc.add_layer();
        doc.set_active_layer(1);

        doc.move_layer(1, 4);

        assert_eq!(doc.layer(1).name, "Layer 3");
        assert!(doc.layer(2).is_group);
        assert_eq!(doc.layer(3).parent_group, 2);

        doc.undo();
        assert!(doc.layer(1).is_group);
        assert_eq!(doc.layer(2).parent_group, 1);
    }
}
