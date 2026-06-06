# Layer panel: select + drag in one gesture

## Problem

Clicking an unselected layer row then dragging to reorder required two gestures (click to select, then drag). Root cause:

1. `LayerRowPointerPressed` calls `SelectLayerWithModifiers(..., rebuildList: false)` on unselected rows.
2. That still calls `_canvas.SelectLayer(index)` → `DrawingDocument.LayersChanged` → `DrawingCanvas.LayersChanged`.
3. `MainWindow.OnCanvasLayersChanged` always calls `ScheduleLayerListRebuild()` → `BuildLayerList()` at Render priority.
4. The virtualized list rebuild destroys/recreates the row while pointer capture / pending drag state is on the old row.

`rebuildList: false` only skips the direct `BuildLayerList()` in `SelectLayerWithModifiers`; it does not stop the async rebuild from `LayersChanged`.

## Fix

- On plain press of an unselected row: update `_selectedLayerIndices` + `RefreshLayerRowSelectionStyles()` only. Set `_pendingLayerSelectIndex`. Do **not** call `_canvas.SelectLayer` yet.
- On pointer release (no drag): `CommitPendingLayerSelection()` → `_canvas.SelectLayer`.
- On drag start: set `_layerDragInProgress`; do not commit on release mid-drag.
- In drag `finally`: commit pending selection (no-op if `MoveLayer` already updated active layer), then `BuildLayerList()`.

## Drop model

- **Folder row** — whole row is drop-onto (nest into folder). Row border highlights.
- **Other rows** — top/bottom half picks insert above/below (insertion line).

To reorder above or below a folder without nesting, use the adjacent layer row (e.g. bottom half of the row above the folder).

Drag handlers use tunnel+bubble routing and resolve the row from `e.Source` (`TryGetLayerRow`) so drops on thumbnails/buttons still hit the row.

## Key files

| File | Role |
|------|------|
| `MainWindow.LayerPanel.cs` | Pointer gesture deferral, `TryGetLayerRow`, drag/drop handlers |
| `MainWindow.Tabs.cs` | `OnCanvasLayersChanged` → `ScheduleLayerListRebuild` |
| `DrawingDocument.cs` | `SelectLayer` → `LayersChanged`, `MoveLayer` / `Into` |
