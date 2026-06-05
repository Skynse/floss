# Draw jank on large canvases — cell snapshot root cause

Research: 2026-06-04. User issue: **1:1 drawing jank**, not zoom/pan.

## Root cause

`CompositeCore` → `CopyTileToCell` copies only a **64×64** composited tile into a cell bitmap, but increments **`_cellRevision[ci]` for the whole cell** (up to 16384×16384).

`DrawTiles` → `EnsureCellDisplayImage` → `SKImage.FromBitmap(entire cell)` when revision changes.

On a 20k×20k document one cell can be ~1GB copied **per dab batch** that touches any tile in that cell.

Source: `docs/krita-floss-render-pipeline.md` lines 53–61, `LayerCompositor.CopyTileToCell` / `EnsureCellDisplayImage`.

Brush rasterize is &lt;1% CPU (`notes/draw-jank-fixes.md`). Display snapshot dominates.

## Fix

1. **Per-tile display images** (`_tileDisplayImages`, 64×64, ~16KB snapshot) — `DrawTiles` draws visible compositor tiles directly, not cell `SKImage`s.
2. **Sync stroke composite** — after `NotifyChanged` during stroke, `FlushStrokeCompositorPreview` runs one `Composite` on the UI thread so display is not one frame behind background worker.

Cells remain for `Bitmap` export / `CopyTileToCell`; not used for live `DrawTiles`.

## Files

- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs` — `PublishTileDisplayImage`, tile `DrawTiles`
- `src/Floss.App/Canvas/DrawingCanvas.cs` — `FlushStrokeCompositorPreview`
- `src/Floss.App/Tools/Core/ITool.cs` — `ToolContext.FlushStrokeCompositorPreview`
- `src/Floss.App/Processes/Output/DirectDrawOutput.cs` — call after `NotifyChanged`

## Crash fix (SIGSEGV in TileDrawBatch.Render)

Crash report `coredump.876367`: `DrawImage` on disposed `SKImage`.

Cause: draw op captured `SKImage` refs at enqueue time; `PublishTileDisplayImage` + `DrainDisposalQueue` during stroke disposed backing bitmap while Avalonia still rendered.

Fix:
- `SKImage.FromPixelCopy` (owned pixel buffer, no shared bitmap)
- `TileDrawBatch` resolves tile index under `CompositeGate` at render time
- Do not call `DrainDisposalQueue` from `FlushStrokeCompositorPreview`
- `DelayedDisposeFrames = 16`

## Revert irrelevant change

Display GPU mipmaps (`notes/display-mipmaps.md`) — zoom-out only; removed from hot path.
