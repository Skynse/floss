# Draw-path jank fixes (profile 2026-06-04)

## Profile findings
- Brush rasterize &lt;1% CPU; jank from alloc/GPU upload + invalidate loop.
- `FlushDirtyCellImages`: full `SKImage.FromBitmap` per dirty cell per frame → RSS 600↔1100MB spikes.
- `FlushPreviewDirty`: duplicate `InvalidateRender` after `NotifyChanged`.
- `CaptureTiles` every segment batch even when selection/alpha-lock/color-mix unused.
- `DirtyTileBudget=32` too low during live stroke → multi-pass catch-up.

## Changes
1. **LayerCompositor.DrawTiles**: draw `SKBitmap` via `DrawBitmap`; cell `_cellRevision` bumps on `CopyTileToCell` so Avalonia scene graph sees updates without recreating `SKImage`.
2. **DirectDrawOutput.FlushPreviewDirty**: drop redundant `InvalidateRender()` (`NotifyChanged` → projection scheduler).
3. **DirectDrawOutput**: skipped lazy `CaptureTiles` — undo/`CommitLayerTileMutation` still requires before-tile snapshots on first touch.
4. **LayerCompositor.CompositeCore**: `StrokeSuspendTileBudget=256` during active stroke.

## Files
- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs`
- `src/Floss.App/Processes/Output/DirectDrawOutput.cs`
