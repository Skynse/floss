# Large-canvas draw jank (10k×8k)

Research: profile `tmp/profile/floss-drawing-2.speedscope.speedscope.json`, baseline `LayerCompositor.cs`.

## User symptom

Drawing at 1:1 feels laggy; **worse as document dimensions grow**. Pan/zoom OK.

## Why size matters (10 000×8 000)

| Quantity | Value |
|----------|-------|
| Cell grid | `ceil(10000/16384)×ceil(8000/16384)` = **1×1 cell** |
| Cell bitmap | 10 000×8 000×4 ≈ **305 MB** |
| Compositor tiles | 157×125 = 19 625 (64×64 each) |

Per dab (baseline):

1. Compositor merges **only dirty 64×64 tiles** (stroke-below cache — cost ~viewport, not full canvas).
2. `CopyTileToCell` — copies **4 KB** into cell, increments `_cellRevision[0]`.
3. `DrawTiles` → `EnsureCellDisplayImage` → **`SKImage.FromBitmap(entire 305 MB cell)`** on **UI render thread**.

Profile (UI thread, 15s draw): `EnsureCellDisplayImage` + `FromBitmap` ≈ **33%** of wall time. Scales with **cell area**, not zoom.

2k×2k doc: one 16 MB cell → ~20× smaller snapshot per dab.

## What NOT to do (reverted — made jank worse)

- Sync `Composite` on UI thread (`FlushStrokeCompositorPreview`)
- GPU mipmaps / zoom display
- Per-tile display **plus** sync composite **plus** lock in `Render`

## Live profile `tmp/profile/live-floss.speedscope.speedscope.json` (20s, running Floss)

| Thread | Hotspot | Self time |
|--------|---------|-----------|
| Render timer (903983) | `TileGridDrawOp.Render` + `DrawImage` | **19.6s / 20s (98%)** |
| UI thread | `DrawingCanvas.Render` | 17ms |
| Workers | `PublishTileDisplayImage` | ~0 on render thread |

**All-viewport tile grid every frame** (~500 `DrawImage` calls) saturates the render thread — worse than 1 cell draw even on small canvases.

## Correct fix (hybrid)

- **Small cell** (≤32 MB): baseline cell `SkiaTileDrawOp` + revision-gated `EnsureCellDisplayImage`.
- **Large cell** (>32 MB, e.g. 10k×8k): during stroke only — freeze one cell snapshot at stroke start, overlay **dirty 64×64 tiles only** (1–4 `DrawImage`/dab). Outside stroke, normal cell snapshot on revision change (rare).

## Long-stroke zigzag choke (profile `live-draw-15s`)

Symptom: long zigzag on large canvas feels fine at first, then **chokes until pen up**.

Cause: `_strokeOverlayTiles` only grows during stroke. Each frame draws **1 frozen cell + every overlay tile in viewport**. Zigzag covers more tiles → render `SkiaTileDrawOp`/`DrawImage` cost climbs (~60ms/s early → ~900ms/s late in 15s trace) even though brush rasterize stays flat.

Fix: when overlay count ≥ `MaxStrokeOverlayTiles` (64), **re-freeze** from live `_cellBitmaps[ci]` (already updated by `CopyTileToCell`) and clear overlays. One ~250ms `FromBitmap` per consolidation vs hundreds of `DrawImage`/frame.

## Files

- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs`
