# Large brush size performance (not pressure)

## User observation

- Lag and visual glitches appear at ~500–600px brush diameter.
- Pressure-to-size mapping made it *look* like a pressure bug: low pressure never reaches 600px; at high pressure the brush hits max size and then stutters/freezes.
- With size dynamics disabled, lag still starts around 500px → **root cause is brush diameter**, not curve evaluation.

## What Floss does at large sizes

### Thresholds (`BrushEngine.cs`)

| Constant | Value | Effect |
|----------|-------|--------|
| `LargeStampCachedRasterMinDiameterPx` | 256 | Switches to cached dab + tile-major raster |
| `LargeDabCpuBakeMaxDimension` | 512 | CPU bilinear bake cap (angled/flipped tips only) |
| `BaseMaskSize` | `min(ceil(brush.Size), 512)` | Tip mask source for scaled dabs |
| `QuantizeLargeStampCacheSize` | 32/64/128px steps | Dab cache key quantization |

### Per-stamp cost at 600px (circle, SrcOver)

1. **Dab bake (cache miss):** `ClassicBrushLut.GetStamp` → ~602×602 byte mask (~360k pixels), copied to `SKBitmap`.
2. **Tile-major composite:** 600px footprint spans ~10×10 **64px tiles** (~100 tiles). Each dab touches all of them; `ApplyCachedDabToTile` runs SIMD row blend per intersecting row.
3. **Overlap:** multiple stamps per segment; lighten/scratch paths merge within a batch but UI time-slices split batches.

### UI-thread backlog (`DirectDrawOutput.cs`)

- Preview raster runs **synchronously on the UI thread** (`ProcessQueuedSync`).
- At diameter ≥ 128px → `ShouldTimeSlicePreview` → max **4 segments / 8ms** per pointer event, **16ms** `NotifyChanged` throttle.
- When `RasterizeSegments` cannot keep up, sample queue grows → each event takes longer → visible freeze until pen-up drains queue.

See also: `notes/standard-canvas-stroke-budget.md`.

## Krita reference (`../krita`) — useful hints

Not a drop-in port, but the architecture explains why Krita stays responsive at huge brushes.

### Files

| File | Role |
|------|------|
| `plugins/paintops/defaultpaintops/brush/kis_brushop.cpp` | `paintAt` only **queues** dabs; no sync layer write |
| `plugins/paintops/libpaintop/kis_dab_cache.h` | Dab cache; size/angle tolerance via precision levels |
| `plugins/paintops/libpaintop/kis_dab_cache_base.cpp` | `precisionLevels[]` — quantize size/angle/subpixel for cache hits |
| `libs/brush/kis_auto_brush.cpp` | Procedural mask via `KisBrushMaskApplicator` into reused `KisFixedPaintDevice` |
| `libs/image/kis_brush_mask_scalar_applicator.h` | Per-pixel mask generation (still O(dab area) but C++/reused buffer) |

### Architectural differences

1. **Async dab pipeline:** `m_dabExecutor->addDab()` on input; `doAsynchronousUpdate()` fetches ready dabs with a **time budget** (`m_maxUpdatePeriod / averageDabRenderingTime`).
2. **Concurrent composite:** `painter->bltFixed(rc, dabsQueue)` runs in **parallel jobs** per split rect (`KisPaintOpUtils::splitDabsIntoRects`).
3. **Reused dab buffer:** `KisFixedPaintDevice` grown lazily — not a new `SKBitmap` per cache entry.
4. **Cache tolerance:** precision level 3+ allows ~1–5% size drift → fewer cache misses while drawing.

Floss still does: pointer event → full `RasterizeSegments` on UI thread → tile writes → invalidate.

## Likely improvement directions (ordered)

1. **Decouple input from composite** (Krita-style): background dab bake + bounded composite queue; UI shows latest ready tiles without draining full sample backlog per event.
2. **Tile-clipped stamp eval:** for procedural circles, evaluate LUT/mask only for pixels in each 64×64 tile intersection instead of baking full 600×600 bitmap first.
3. **Stronger dab cache reuse at 512+:** quantize display size more aggressively during live stroke (Krita precision model); separate “display dab” from “commit dab” if needed.
4. **Raise or adapt time-slice budget** using measured `RenderTelemetry` ms/dab — current 8ms/4 segments is conservative once dab cost is understood.
5. **Do not reintroduce StrokeMaskCached** for overlap — it caused tile squares; overlap needs per-tile max-alpha or async merge, not full-dirty beforeTile restore.

## Profiling in Floss

Enable `AppConfig.ShowRenderTelemetry` — overlay shows `LastStats.Path`, stamp count, cached dab count, segment ms from `DirectDrawOutput` / `BrushEngine`.

## Relevant Floss files

- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — dab cache, tile-major raster
- `src/Floss.App/Brushes/Engine/ClassicBrushLut.cs` — large circle bake
- `src/Floss.App/Processes/Output/DirectDrawOutput.cs` — UI-thread time slicing
- `src/Floss.App/Document/TiledPixelBuffer.cs` — `TileSize = 64`
