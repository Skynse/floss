# Ctrl+T transform — implementation audit (2026-06-06)

Reference: `notes/krita-transform-reference.md` (Krita in-place stroke model).

## Architecture (current)

Krita-style preview on live layer:

1. **TryCreate** — capture source tiles + float cache (`CaptureTiles`, `Capture`, `ExtractFloatPixels`).
2. **CompleteInit** (deferred) — stroke suspend, clear source from document, first preview.
3. **ReapplyPreview** (throttled) — clear previous dest → stamp cache → layer via SK (`StampRotatedLayer` / move fast path).
4. **Commit** — full-res preview + `CommitLayerTileMutations` with expanded before-tiles.
5. **Cancel** — restore all before-tiles (source + dest captures).

Overlay = handles only (`RenderOverlay`). Pixels live on layer during drag (compositor sees them).

## Files

| Area | Path |
|------|------|
| Tool + operation | `src/Floss.App/Tools/Selection/TransformTool.cs` |
| Stroke suspend | `src/Floss.App/Canvas/Compositing/LayerCompositor.cs` |
| Document notify | `src/Floss.App/Document/DrawingDocument.cs` |
| Canvas wiring | `src/Floss.App/Canvas/DrawingCanvas.cs` |

---

## Bugs found and fixes applied (this pass)

### 1. Cancel destroyed art under preview dest (critical)

**Was:** `Cancel()` cleared last preview bbox, then restored only `data.BeforeTiles` (source region from TryCreate).

**Problem:** `CaptureDestBeforeStamp` during drag adds **dest** tiles to `_commitBeforeTilesByLayer`. Underlying pixels in the preview area (outside the source box) were cleared on cancel, not restored.

**Fix:** Cancel restores **`GetCommitBeforeTiles(data)`** (full undo snapshot). No separate `Clear(dest)`.

### 2. Init race — double image if drag before CompleteInit

**Was:** `CompleteInit` posted to `DispatcherPriority.Background` after `TryCreate` returns; user could drag immediately while source still on layer.

**Fix:** `_initComplete` gate — `ReapplyPreview` no-op until init finishes (clear + first preview).

### 3. Multi-layer compositing — stroke-below cache thrash

**Was:** `ReapplyPreview` called `NotifyChanged(dirty, data.Index)` **per layer**. Compositor invalidates stroke-below cache when `layerIndex != _strokePaintLayerIndex` (`InvalidateGroupCaches`). N transforming layers ⇒ N−1 cache rebuilds per preview frame.

**Fix:** Single batched `NotifyChanged(combinedDirty, _layerData[0].Index)` + `NotifyStrokeSuspendExtend(combinedDirty)` per preview pass.

---

## Remaining issues / follow-ups

### Perf — drag path

| Issue | Detail | Krita analogue |
|-------|--------|----------------|
| **StampRotatedLayer alloc** | Every preview: new `byte[]`, `Capture`, SK bitmap install, `Restore`. Hot on large rotated drags. | `transformAndMergeDevice` into tiles, LOD |
| **LOD only during drag** | `PreviewLodMaxDimension = 1024`; `EndDrag`/`Commit` force full res. `PreviewLodCommitMaxDimension = 2000` **unused**. | LOD threshold ~2000, commit at lod0 |
| **Move fast path** | `StampTranslatedLayer` block-copies cache when size unchanged — good. | — |
| **Throttle + compositor busy** | 16 ms (32 ms if >2M px); skips frame if `IsCompositorBusy`. Handles can outrun preview. | 30 ms + skip if updates running |
| **TryCreate sync cost** | Still `Capture` + per-pixel `ExtractFloatPixels` on UI thread before transform starts. | Composition-source device |

### Correctness — edge cases

| Issue | Detail |
|-------|--------|
| **Move fast path over existing pixels** | `StampTranslatedLayer` replaces dest with cache (no SrcOver). Rotated path composites with SrcOver. Moving over other art replaces it during preview. |
| **Stroke suspend single paint layer** | `BeginTransformSession` uses `_layerData[0].Index` only. OK for stack split if below-cache rebuilds when other transformed layers notify (now batched once). |
| **ExtendStrokeSuspend noop** | Compositor `ExtendStrokeSuspend` is empty — growing dirty rect during drag does not expand frozen cell snapshot region (large canvas + stroke overlay tiles cap). |
| **Selection transform clear** | `_usingSelection` clears only masked pixels in source box via CPU loop — OK but slow on large selections. |
| **Clipping layers** | Clip base captured if not in transform set; applied in `ExtractFloatPixels`. |

### Compositing

| Path | Behavior |
|------|----------|
| Stroke suspend active | `StrokeSuspendTileBudget = 256`, split below/above paint layer, cache below tiles |
| Transform preview | Live pixels on transforming layer(s); compositor full stack merge into scratch tiles |
| `wasFull` dirty | Disables stroke cache path → full stack composite every tile (expensive if full canvas dirty) |

### Undo / commit

| Path | Behavior |
|------|----------|
| **Commit** | `ReapplyPreview(fullResolution)` then `CommitLayerTileMutations` with `GetCommitBeforeTiles` — correct |
| **CommitDelete** | Clears preview dest + source, then mutations — correct |
| **Cancel** | Full tile restore from commit before map — fixed |

---

## ReapplyPreview loop (reference)

```
for each layer:
  clear previous preview dest (last frame)
  CaptureDestBeforeStamp(dest)  → expand commit before-tiles
  StampRotatedLayer / StampTranslatedLayer
  union dirty
NotifyChanged once (paint layer index = first transformed layer)
```

## StampRotatedLayer (reference)

- Captures existing dest into flat buffer.
- SK canvas: translate to doc space, rotate/flip, `DrawBitmap` cache with `SrcOver`.
- `Restore` clipped region to layer tiles.
- `previewLod > 0`: downsampled cache, `IsAntialias = false` during drag.

## Tests

- `DrawingDocumentTests.Transform_StartsOnEmptySelection` — starts transform on empty selection.
- Add: cancel after move restores dest tiles (TODO).

## When changing transform/compositor

1. Read `notes/krita-transform-reference.md`.
2. Read this file.
3. Never per-layer `NotifyChanged` during stroke suspend for multi-layer ops — batch dirty.
4. Cancel/undo must use **`GetCommitBeforeTiles`**, not TryCreate-only source tiles.
5. Do not defer source clear without gating preview (`_initComplete`).
