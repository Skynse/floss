# Floss Development Skills & Architecture Reference

## Build & Test

```sh
dotnet build src/Floss.App/Floss.App.csproj
dotnet test tests/Floss.App.Tests/Floss.App.Tests.csproj
```

Target: `net10.0`, Avalonia `12.0.2`, SkiaSharp `3.119.4-preview.1.1`.

---

## Architecture Overview

### Layer Compositing (Krita-aligned)

- **Tree walk only.** `LayerProjectionPlane` composites one sibling list at a time (paint layers + groups under the same parent). `LayerStackComposition.GetRootLayers()` selects roots — never use the flat `_layers` list for compositing.
- **Groups:** Non–pass-through groups merge children into a `GroupProjectionCache` buffer, then blend onto the parent (`KisGroupLayer::original` + `projectionPlane()->apply`). Pass-through groups recurse with accumulated opacity.
- **Display vs merge:** `LayerCompositor` owns viewport tiles (`_compTiles`); pixel blend ops in `LayerCompositorPixelOps.cs`; merge orchestration in `LayerProjectionPlane` via `ILayerMergeHost` / `MergeHost`. Stroke-below cache lives on `LayerProjectionPlane.StrokeBelow`.
- **Dirty rects:** `InvalidateGroupCaches` walks the changed layer's parent chain (KisMergeWalker-style). Group caches track partial clean/dirty regions.
- **`tryObligeChild()`:** Nested groups only — reuse child group projection when a single visible group child qualifies (paint-layer oblige disabled until tile-local buffers match document-space projection).
- **Not yet ported from Krita:** full `KisMergeWalker` `needRect`/`changeRect`, clone/mask nodes, paint-layer oblige on tiles.

### Compositor Threading

- `BackgroundCompositePass` runs on `Task.Run` (thread pool). Holds `CompositeGate` lock for the entire pass.
- `DrawTiles()` on UI thread uses lock-free snapshot of `_compTiles` (ConcurrentDictionary).
- `Parallel.For` inside `CompositeCore` with `MaxDegreeOfParallelism = cores - 1` (leaves one core for UI).
- **Only first paint is synchronous** (prevents blank canvas). All subsequent composites are background.

### CompositeCore Fast Path

When called via `Render()`, `viewportClip` is always provided. The fast-path checks:
1. `queueDirtyClip.IsEmpty && _pendingDirtyTiles.Count == 0`
2. No missing viewport tiles at current LOD

If both true, returns `false` immediately — no composite pass runs.

### Idle Steady Skip

`Render()` checks `PendingDirtyTileCount == 0 && PendingCount == 0 && !lodTransition` to skip `ScheduleBackgroundComposite` entirely during idle (60fps render ticks with no work).

### Dynamic Tile Cache

`MaxCompositeCacheTiles = 768` is the floor. `SetSize()` computes `_effectiveMaxCacheTiles` as `max(768, ceil(w/256) * ceil(h/256) * 1.5)` so large canvases (10k×8k = 1280 tiles at LOD 0) don't trigger the eviction-recomposite loop.

`TrimCompositeCache` evicts furthest-from-viewport tiles when `_compTiles.Count > _effectiveMaxCacheTiles`. Viewport tiles are preserved.

---

## Brush Engine

### Async Rendering

Brush stamp rendering runs on `Task.Run` via `DirectDrawOutput.ProcessQueuedSegments()` at line ~232. The UI thread queues samples and returns immediately; background threads render stamps and call `CommitMutation`.

### Render Path Selection

`RenderCurrentStamps()` selects the optimal path:
1. `TryRasterizeCachedDabsTileMajor` — reuse previously-generated dab stamps (cache hit)
2. `TryRasterizeCachedColorDabsTileMajor` — cached color-mix dabs
3. `RasterizeStampsDirect` — procedural stamps (CPU per-pixel)
4. `RenderStampsViaScratch` / `RenderColorMixStampsViaScratch` — scratch buffer batching for SrcOver
5. `RenderWithSkiaOnLayer` — Skia GPU fallback (non-SrcOver blend modes, huge stamps, color mix edge cases)

### Dab Cache

`ActiveStroke._dabCache` (Dictionary<CachedDabKey, CachedDab>, max 128 entries, max 1M dab pixels). Key is quantized (size, hardness, thickness, angle, flip bits, tip index). Cache hits skip stamp mask generation entirely.

### Mask Caching

All brush tip types cache generated masks:
- `ProceduralBrushTip` — caches by `(size, quantizedHardness)`
- `ImageBrushTip` — caches by `(size, quantizedHardness)`
- `CompoundBrushTip` — caches by `(size, quantizedHardness)`

### Color Pipeline

Linear-light LCh color mixing with 4096-entry LUTs for sRGB decode/encode and cube root (avoids `MathF.Pow` in hot path).

---

## Undo / Redo

### Tile History

`LayerTileHistoryState` stores per-tile patches (`LayerTilePatch` = tileX, tileY, BeforePixels, AfterPixels). `CommitLayerTileMutation` only stores patches where `before != after` (unchanged tiles are discarded).

**Krita-style optimization applied:** `CaptureRedo()` reuses the same `Patches[]` array with an `_isRedo` flag instead of duplicating the entire patch list. Halves undo memory.

`MoveLayer` uses a zero-pixel structural history state (`MoveLayerHistoryState`) — only position is stored.

### Dirty Rect on Undo/Redo

`NotifyChanged(DirtyRegion, LayerIndex)` is called after restore. For tile history states, `DirtyRegion` matches the patch extent. For layer moves, `DirtyRegion = oldBounds ∪ newBounds` (Krita `startUpdates()` pattern).

---

## Layer Moves & Cache Invalidation

### Targeted Dirty Rect

`MoveLayer()` captures `source.DocumentContentBounds` before and after the move, emits union as dirty rect via `DocumentChangedEventArgs(totalExtent, sourceIndex, movedFromParent: oldParent)`. Previously emitted `(null, null)` = full canvas dirty — 100x more tiles for a small layer on a large canvas.

### Group Cache Invalidation

`InvalidateGroupCaches` walks BOTH the new parent chain (from changed layer) AND the old parent chain (from `MovedFromParent`). When a layer moves between groups, both caches are invalidated for the dirty region. Group caches use partial `Invalidate(region)` — only the dirty rect is cleared, not the entire cache.

### Threading

`DocumentChangedEventArgs.MovedFromParent` flows through:
`DrawingDocument` → `DrawingCanvas._document.Changed` → `ProjectionUpdateScheduler.Invalidate` → `LayerCompositor.Invalidate` → `InvalidateGroupCaches`

---

## Blend Modes

26 blend modes implemented in `LayerCompositorPixelOps.ApplyBlendMode()`:
Normal, PassThrough, Dissolve, Multiply, Screen, Overlay, SoftLight, HardLight, ColorDodge, ColorBurn, LinearDodge, LinearBurn, Darken, Lighten, DarkerColor, LighterColor, Difference, Exclusion, Subtract, Divide, Hue, Saturation, Color, Luminosity, VividLight, LinearLight, PinLight, HardMix.

RGB values are in 0.0–1.0 double precision. SrcOver composite uses premultiplied alpha with Krita-matching formula. Integer fast path for Normal blend in `CompositeLayer`.

### LOD Blend Fix

`CompositeSingleLayerLod` was compositing layers against a transparent buffer then SrcOver-sampling, destroying non-Normal blend results. Now **point-samples source pixels at LOD resolution and applies `ApplyBlendMode` directly against actual destination pixels**, followed by premultiplied-SrcOver blend. Matches the group blend path.

---

## Viewport & Input

### Coordinate System
- `TransformGroup` with flip→zoom→rotate→pan transforms
- `RenderTransformOrigin = RelativePoint(0.5, 0.5)` — centered
- Viewport tools receive raw viewport pixel coords; drawing tools receive canvas-local coords
- `SyncViewportStateToCanvas()` centralizes all transform state sync

### Input Pipeline
- `Workspace_OnPointerMoved` → `CanvasInputRouter.PointerMoved` — returns immediately when `_state != RouterState.Running` (idle hover is free)
- `IsViewportTool()` routes viewport tools separately from drawing tools
- Middle-button pan via `_isMiddleButtonPanning` flag

---

## Key File Map

| File | Purpose |
|------|---------|
| `Canvas/DrawingCanvas.cs` | Render loop, compositor dispatch, cursor overlay, `ClearSelectionContent` |
| `Canvas/Compositing/LayerCompositor.cs` | Tile cache, `CompositeCore`, `Invalidate`, LOD, trimming, `CompositeSingleLayerLod` |
| `Canvas/Compositing/LayerCompositorPixelOps.cs` | Blend modes, `ApplyBlendMode`, `BlendPixel`, `CompositeLayer` |
| `Canvas/Compositing/LayerProjectionPlane.cs` | Group cache, `InvalidateGroupCaches`, `CompositeSiblingStack`, `GetGroupProjection` |
| `Canvas/Compositing/ProjectionUpdateScheduler.cs` | Batches invalidation requests, `ApplyPending`, `Invalidate` |
| `Brushes/Engine/BrushEngine.cs` | Stamp rendering, dab cache, render path selection, LCh color mixing |
| `Processes/Output/DirectDrawOutput.cs` | Async brush stroke pipeline, `ProcessQueuedSegments` via `Task.Run` |
| `Document/DrawingDocument.cs` | Layers, undo/redo history, `MoveLayer`, `CommitLayerTileMutation` |
| `Document/TiledPixelBuffer.cs` | Tile storage, `CaptureTiles`, `Clear`, `ForEachTile`, `PruneRegion` |
| `Tools/Selection/SelectionMask.cs` | Selection state, `IsSelected`, `GetMaskBounds` |
| `MainWindow/MainWindow.axaml.cs` | UI layout, menu bar, dockers, popups, blend mode list |
| `MainWindow/MainWindow.LayerPanel.cs` | Layer list, drop indicator, drag-drop, context menus |
| `MainWindow/MainWindow.Viewport.cs` | Hand/Rotate/Zoom tools, `InvalidateViewport` |

---

## Known Issues

- `TiledDisplay_MatchesProjectionMerge_ForStackedNormalGroups` — intermittent failure, compositor test
- `MergeSelectedLayers_CombinesMultipleLayers` — intermittent failure, pre-existing
- `TransformTool` has no `ViewportFlipX`/`ViewportFlipY` — pre-existing LSP errors, not build-breaking
- `DrawingLayer` has no `RenderOffsetX`/`RenderOffsetY` / `ClearPreviewOffset` — pre-existing LSP errors
- Group projection caches still drop fully on `NotifyLayersChanged()` (add/remove layer) — correct behavior per Krita

---

## Performance Optimizations Applied

| Optimization | Where | Effect |
|-------------|-------|--------|
| Idle-steady skip | `Render()` | No composite dispatch at 60fps idle |
| CompositeCore fast path | `CompositeCore()` | Skips composite when no dirty + no missing viewport tiles |
| Dynamic tile cache | `SetSize()` / `TrimCompositeCache` | No eviction loop on large canvases |
| No sync compositing | `Render()` | Drawing never blocks UI on composite |
| Targeted dirty rect on moves | `MoveLayer()` | Drops 100x fewer tiles per layer move |
| Dual parent cache invalidation | `InvalidateGroupCaches` | Old+new group caches invalidated on cross-group moves |
| Undo halved memory | `LayerTileHistoryState.CaptureRedo` | Shares patch array with `_isRedo` flag |
| Dab cache | `ActiveStroke._dabCache` | Reuse identical stamps (128 entries, 1M pixels) |
| Mask caching | All brush tips | Cache by quantized size+hardness |
| GPU resource limit | `SkiaOptions.MaxGpuResourceSizeBytes` | 512MB max |
| Brush CPU path preference | `RenderCurrentStamps` | `RasterizeDab` now uses same path selection |
| Parallel compositor capped | `_compositeParallelOptions` | `MaxDegreeOfParallelism = cores - 1` |
| Brush rendering async | `DirectDrawOutput.ProcessQueuedSegments` | Task.Run for stamp rendering |
| Viewport tile preservation | `PruneTransparentTiles(viewportClip)` | No alpha-background recomposite loop |
| GC latency mode | `Program.cs` | `GCLatencyMode.SustainedLowLatency` |

---

## Krita References

Krita source at `../krita`. Key files referenced in Floss' architecture:

| Krita File | Floss Equivalent |
|-----------|-----------------|
| `kis_async_merger.cpp` | `BackgroundCompositePass` / `CompositeCore` |
| `kis_merge_walker.cpp` | `InvalidateGroupCaches` (partial) |
| `kis_projection_plane.cpp` | `LayerProjectionPlane.GetGroupProjection` |
| `kis_transaction_data.cpp` | `LayerTileHistoryState` / `CommitLayerTileMutation` |
| `kis_brushop.cpp` | `DirectDrawOutput.ProcessQueuedSegments` |
| `KisDabRenderingExecutor` | (not yet — dab rendering is async but not queued per-dab) |
| `KisDabRenderingQueue` | `ActiveStroke._dabCache` (simplified) |
| `kis_tile_data.cc` | `TiledPixelBuffer` |
| `kis_tile_data_pooler.cc` | (not yet — no COW pre-cloning) |
| `kis_tile_data_swapper.cpp` | (not yet — no disk swap) |

---

## Windows Build

```sh
dotnet publish src/Floss.App/Floss.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false \
  -o artifacts/floss-win-x64-compact
```

- **No Velopack** — removed
- **ONNX Runtime** included. User needs `vc_redist.x64.exe` (VC++ Redist).
- **app.manifest** must NOT contain `assemblyIdentity` — causes SxS errors with single-file publish.
