# Krita transform tool — compositor-compliant reference

Researched from `/home/neckles/projects/krita` for Floss transform/compositor work.

## Architecture overview

Krita’s transform tool is **not** “overlay only.” It is a **stroke** (`InplaceTransformStrokeStrategy` / `TransformStrokeStrategy`) that:

1. Snapshots source pixels into a **composition-source cache** (`createCompositionSourceDevice`).
2. **Clears** the live paint device (undoable transaction).
3. On each handle move, **undoes the previous preview**, **re-merges** transformed cache into the live device, and notifies the **projection graph** with batched dirty rects.
4. On commit/cancel, uses the same undo command stack to finalize or restore.

Canvas stays compositor-correct because updates go through **`KisNode` / `projectionPlane()` / `KisUpdateCommandEx`**, not ad-hoc tile clears.

```
Tool (KisFreeTransformStrategy)
  └─ requestImageRecalculation → stroke pendingUpdateArgs
InplaceTransformStrokeStrategy
  ├─ init: createCacheAndClearNode (cache + clear each node)
  ├─ drag: reapplyTransform (undo prev → transformAndMergeDevice → KisUpdateCommandEx)
  └─ finish/cancel: undo stack or final lod0 transform
Image projection graph
  └─ per-node projection planes, tightUserVisibleBounds invalidation
```

## Key files

| Area | Path |
|------|------|
| In-place stroke (main path) | `plugins/tools/tool_transform2/strokes/inplace_transform_stroke_strategy.cpp` |
| Legacy/alternate stroke | `plugins/tools/tool_transform2/strokes/transform_stroke_strategy.cpp` |
| Free-transform UI / handles | `plugins/tools/tool_transform2/kis_free_transform_strategy.cpp` |
| Transform merge helper | `plugins/tools/tool_transform2/kis_transform_utils.cpp` |
| Composition-source devices | `libs/image/kis_paint_device.h` (`createCompositionSourceDevice`) |
| Projection updates | `libs/image/commands_new/KisUpdateCommandEx.h`, `KisBatchNodeUpdate` |
| Tool entry | `plugins/tools/tool_transform2/kis_tool_transform.cc` |

## 1. Source cache (`createCompositionSourceDevice`)

**File:** `inplace_transform_stroke_strategy.cpp` — `createCacheAndClearNode`

- Copy selected/content pixels into a temp device with **composition-correct color space**:
  - `device->createCompositionSourceDevice(device)` (full layer)
  - or selection-limited bitBlt via `KisPainter` + `m_selection`
- Store in `devicesCacheHash[device]`.
- **Then** clear live device (`device->clear()` or `clearSelection`), wrapped in `KisTransaction` → undoable `Clear` command group.

**Docs:** `kis_paint_device.h` lines ~657–684 — temp devices for “fill then paint over destination” must use `createCompositionSourceDevice()`.

**Floss gap:** We extract to `byte[]` / `WriteableBitmap` overlay. Krita keeps a **paint-device cache** tied to composition color space and undo.

## 2. Preview during drag (`reapplyTransform`)

**File:** `inplace_transform_stroke_strategy.cpp` — `reapplyTransform`, `transformNode`, `doCanvasUpdate`

Per preview frame (throttled):

1. `undoTransformCommands(levelOfDetail)` — revert last preview write.
2. `KisDisableDirtyRequestsCommand` — block re-entrant dirty storms.
3. For each node: `KisTransformUtils::transformAndMergeDevice(config, cachedPortion, device)` — SK-style merge from **cache → live device**.
4. `addDirtyRect(node, cachedPortion->extent() | projectionPlane()->tightUserVisibleBounds(), lod)`.
5. `fetchAllUpdateRequests` → `KisUpdateCommandEx` → projection/compositor refresh.

**Throttle:** `updateInterval = 30` ms; skip if `updatesFacade->hasUpdatesRunning()` (`tryPostUpdateJob` / `doCanvasUpdate`).

**Floss gap:** We invalidate whole canvas / huge tile regions. Krita unions **old | new tight bounds per node** and posts through update facade.

## 3. LOD preview for large transforms

**File:** `inplace_transform_stroke_strategy.cpp` — `calculatePreferredLevelOfDetail`

- If source rect max dimension > ~2000px → compute `previewLevelOfDetail` (log2 scale).
- During drag: transform at LOD into live device (`TransformLod` command group) or mask static cache.
- On **commit**: undo LOD commands, `reapplyTransform(..., levelOfDetail=0)` full resolution, `repopulateUI` full bounds.

**Floss gap:** No LOD transform preview; large layers hit full-res merge every move.

## 4. Multi-layer / group preview thumbnail

**File:** `transform_stroke_strategy.cpp` — `PreparePreviewData`

For groups or multiple nodes:

- Clone nodes into temporary `KisImage`.
- `refreshGraphAsync` + `waitForDone` — **full projection**.
- `previewDevice = createDeviceCache(clonedImage->projection())`.
- Used for **tool thumbnail / handles** (`sigPreviewDeviceReady`), not as a substitute for skipping projection.

Pass-through groups: forced to non-pass-through for preview clone to avoid dropped projection leaf crash (comment ~L214–219).

## 5. Compositor / projection compliance

Mechanisms:

- **`KisBatchNodeUpdate`**: per-node dirty rects; `prevDirtyRects | dirtyRects` compressed.
- **`projectionPlane()->tightUserVisibleBounds()`** at init and after each transform.
- **`KisUpdateCommandEx`**: flip-flop command driving `KisUpdatesFacade` (wraps image graph refresh).
- **`KisDisableDirtyRequestsCommand`**: suppress duplicate dirty requests during batch.
- **`initialUpdatesBeforeClear`**: snapshot for undo/update baseline before clear.

Init sequence (barrier jobs):

```cpp
// inplace_transform_stroke_strategy.cpp ~436–446
prevDirtyRects.addUpdate(node, node->projectionPlane()->tightUserVisibleBounds());
executeAndAddCommand(new KisUpdateCommandEx(..., INITIALIZING), ...);
executeAndAddCommand(new KisDisableDirtyRequestsCommand(..., INITIALIZING), ...);
// ... createCacheAndClearNode per node ...
```

**Floss gap:** `NotifyChanged(hugeRegion)` on tile compositor without stroke-scoped undo of preview writes or per-node tight bounds.

## 6. Cancel / commit

**Cancel** (`cancelAction`):

- If identity: `undoTransformCommands(0)` + `undoAllCommands()` (restores clear + transforms).
- Else: `reapplyTransform(initialTransformArgs, lod0)` to restore geometry.

**Commit** (`finishAction`):

- If had LOD preview: undo LOD, then `reapplyTransform(currentArgs, lod0)`.
- Push final undo macro; selection update job.

All preview steps are **undoable command groups** (`Clear`, `Transform`, `TransformLod`, `TransformTemporary`).

## 7. UI strategy (handles only)

**File:** `kis_free_transform_strategy.cpp` — `recalculateTransformations`

- Computes handle matrices, `paintingTransform` for decoration.
- Emits `requestImageRecalculation` → stroke, not direct pixel writes.
- Optional thumbnail via `setThumbnailImage` / `initThumbnailImage` (`kis_tool_transform.cc`).

Decorations disabled during preview init so outline doesn’t fight transform preview (`setDecorationsVisible(false)`).

## Comparison to Floss (current)

| Aspect | Krita | Floss (before fix) | Floss (after recent fix) |
|--------|-------|--------------------|---------------------------|
| Live layer during drag | Cleared; preview written back via merge | Cleared at start; overlay only | Unchanged; skipped in compositor + overlay |
| Compositor notify | Node projection + batched tight rects | Huge `NotifyChanged` | Viewport region invalidate + skip layers |
| Preview perf | 30 ms throttle, LOD, undo prev preview | Full invalidation / per-pixel extract | Overlay only on drag (better) |
| Undo model | Stroke commands (clear + each preview) | Tile mutations at commit | Tile mutations at commit |
| Multi-layer | Clone image + projection / per-node cache | Per-pixel loops in `TryCreate` | Same |

## Recommended Floss directions (informed by Krita)

1. **Short term (keep overlay UX):** Current skip-layer + defer-clear matches Krita’s *intent* (don’t break stack composite) without full stroke infrastructure.

2. **Medium term — Krita-like preview path:**
   - `TransformStroke` with cached source tiles (composition-source equivalent).
   - On move: transform cache → temp destination, **overlay OR merge preview layer**, throttle 30 ms.
   - Dirty union = `srcBounds | transformedBounds` per layer index through `ProjectionUpdateScheduler`.

3. **Large layer perf (from Krita LOD):**
   - If content bounds max dimension > N, preview transform at reduced res during drag; full res on commit.

4. **Multi-layer transform:**
   - Prefer clone-and-project subset (Krita `PreparePreviewData`) over O(pixels) `GetPixel`/`SetPixel` in `TryCreate`.

5. **Commit path:**
   - `transformAndMergeDevice`-style: one SK pass cache→layer, then single `CommitLayerTileMutations` with before/after tile capture (already close in `StampRotatedLayer`).

## Snippets to re-read when implementing

**Cache + clear (init):**

```cpp
// inplace_transform_stroke_strategy.cpp createCacheAndClearNode
cache = device->createCompositionSourceDevice(device);
m_d->devicesCacheHash.insert(device.data(), cache);
device->clear(); // or clearSelection
executeAndAddCommand(transaction.endAndTake(), Clear, CONCURRENT);
```

**Preview reapply:**

```cpp
// inplace_transform_stroke_strategy.cpp transformNode
KisTransformUtils::transformAndMergeDevice(config, cachedPortion, device, &helper);
addDirtyRect(node, cachedPortion->extent() | node->projectionPlane()->tightUserVisibleBounds(), levelOfDetail);
```

**Throttle:**

```cpp
// Private::updateInterval = 30;
if (updateTimer.elapsed() > updateInterval && !updatesFacade->hasUpdatesRunning())
    doCanvasUpdate(force);
```

**LOD threshold:**

```cpp
// calculatePreferredLevelOfDetail — maxSize = 2000
const int calculatedLod = qCeil(std::log2(qMax(1.0, qreal(maxDimension) / maxSize)));
```
