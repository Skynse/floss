# Large stamp cached tile-major raster

Profile: `tmp/profile/live-1024brush-30s.speedscope.speedscope.json` — 1024px slow draw, ~19.6s `RasterizeStampsDirect` on UI.

## Cause

`RenderCurrentStamps` skips `TryRasterizeCachedDabsTileMajor` when `UsesProceduralStampEvaluation` is true (default circle). At 1024px that runs per-pixel `RasterizeStampsDirect` (~1M evals/dab). Smaller brushes use baked `CachedTileMajor` via `ActiveStroke.TryGetCachedDab`.

## Fix

1. Brush diameter ≥ 256px → `CachedTileMajor` before `RasterizeStampsDirect`.
2. Post-fix profile `live-1024brush-after-fix`: bottleneck moved to `TryGetCachedDab` + **SKCanvas.DrawBitmap** (~12.7s/20s). `ApplyCachedDabToTile` only ~0.65s.
3. Large circle/soft-round dabs: bake with **`ClassicBrushLut.GetStamp`** (CPU LUT) instead of Skia scale-draw.
4. **Textured / image tips**: full-res CPU mask bake (no Skia). Quantize dab cache size so pressure dynamics hit cache.
5. **Dark band / square overlap artifact** (≥256px live stroke, SrcOver): UI batches were SrcOver-compounding onto the layer across time-slices. Fix: `StrokeMaskCached` — persistent per-tile mask with **src-over alpha accumulation** (not max-alpha; see `notes/large-brush-dimming-fix.md`), all stroke-mask tiles rebuilt each batch as `SrcOver(beforeTile, brush, mask)` using `DirectDrawOutput.BeforeTiles`. Fallbacks: `CachedLightenScratch`, `CachedTileMajorLighten`. Sub-256px / non-live keep direct `CachedTileMajor`.

## File

- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — `PreferCachedTileMajorRaster`, `TryBakeLargeCircleDab`, `RenderCurrentStamps`
