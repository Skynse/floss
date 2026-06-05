# Draw-path jank fixes (profile 2026-06-04)

## Profile findings
- Brush rasterize &lt;1% CPU; jank from alloc/GPU upload + invalidate loop.
- `FlushDirtyCellImages`: full `SKImage.FromBitmap` per dirty cell per frame → RSS 600↔1100MB spikes.
- `FlushPreviewDirty`: duplicate `InvalidateRender` after `NotifyChanged`.
- `CaptureTiles` every segment batch even when selection/alpha-lock/color-mix unused.
- `DirtyTileBudget=32` too low during live stroke → multi-pass catch-up.

## Changes (kept)
1. **LayerCompositor.DrawTiles**: `SKImage.FromBitmap` only when `_cellRevision` changes (`EnsureCellDisplayImage`), not every frame. `SkiaTileDrawOp.Equals` always false so pan/zoom repaints.
2. **DirectDrawOutput.FlushPreviewDirty**: drop redundant `InvalidateRender()` (`NotifyChanged` → projection scheduler).
3. **DirectDrawOutput**: skip `CaptureTiles` when selection/alpha-lock/color-mix not needed.
4. **LayerCompositor.CompositeCore**: `StrokeSuspendTileBudget=256` during active stroke.
5. **LayerCompositor.DispatchToPool**: `UseParallelComposite` — no parallel merge on UI thread (avoids `SemaphoreSlim.Wait` stalls).

## Reverted (2026-06-04, caused idle lag)
- Per-tile `DrawTiles` (hundreds of Skia ops/frame vs 1–4 cells).
- Viewport prefetch / `_tileDisplayReady` / LRU scratch cache / sync stroke composite on every `Render`.
- Calm `BackgroundCompositePass` invalidate — restored always-invalidate behavior.

## Files
- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs`
- `src/Floss.App/Processes/Output/DirectDrawOutput.cs`
