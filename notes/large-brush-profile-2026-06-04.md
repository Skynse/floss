# Large brush profile (2026-06-04)

Bench: `dotnet run -c Release --project bench/PipelineBench/PipelineBench.csproj -- --sizes`

Setup: 3000×4080 canvas, circle SrcOver, spacing 0.04, 960px horizontal stroke in 8px steps (120 segments, ~120 stamps).

## BrushEngine `RasterizeSegment` (engine-only, no tile capture, no compositor)

| Size | Total | ms/seg | ms/stamp | 1st segment | Raster path |
|------|-------|--------|----------|-------------|-------------|
| 48px | 42ms | 0.35 | 0.31 | 10.7ms | ProceduralStampFast |
| 128px | 55ms | 0.46 | 0.46 | 0.4ms | ProceduralStampFast |
| 256px | 93ms | 0.78 | 0.78 | 12.1ms | CachedTileMajorLighten |
| 512px | 318ms | 2.65 | 2.65 | 3.0ms | CachedTileMajorLighten |
| 600px | 506ms | 4.21 | 4.21 | 5.1ms | CachedTileMajorLighten |
| 1024px | 1171ms | 9.76 | 9.76 | 11.4ms | CachedTileMajorLighten |

## Interpretation

1. **Cliff at 256px** — switches from `ProceduralStampFast` to cached tile-major lighten (`LargeStampCachedRasterMinDiameterPx = 256`).
2. **Cost scales ~linearly with diameter** above 256px: ~0.004ms per stamp per px of diameter (600px ≈ 4.2ms/stamp, 1024px ≈ 9.8ms/stamp).
3. **First-segment spikes at 48/256** are dab-cache cold start / mask generation, not sustained rate.
4. **600px live stroke UI budget** (`DirectDrawOutput`): `ShouldTimeSlicePreview` at ≥128px → max 4 segments × ~4.2ms ≈ **17ms raster per pointer event** before compositor/tile capture. Exceeds `RenderSliceBudgetMs` (8ms) → backlog grows → freeze feel.
5. **Compositor is not the primary bottleneck** for large brush raster; engine segment time dominates. Compositor profile (DrawPathProfile 10k canvas): ~64×45 visible tiles, composite_per_dab low for 64×64 dirty regions.

## UI-thread math (600px brush)

- ~4.2ms/segment × 120 segments = **~500ms** engine-only for full stroke
- Time-sliced preview: ~2 segments fit in 8ms budget → **~60 UI slices** minimum to drain one stroke's segments, plus `CaptureTiles` per batch and compositor invalidate

## Live process profile (PID 962074, 20s, `tmp/profile/live-large-brush-124131.speedscope.speedscope.json`)

User drawing during capture. UI thread **14.6s CPU / 20s wall** (73% saturated).

| Self time (UI) | % CPU | Hotspot |
|----------------|-------|---------|
| 13.2s | 90% | `TryRasterizeCachedDabsLightenScratch` |
| 13.0s | 89% | `SampleMaskAlpha` (bilinear, inside scratch path) |
| 1.8s | 12% | `CachedDab.get_MaskScaleY()` |
| 1.5s | 10% | `CachedDab.get_MaskScaleX()` |
| 1.1s | 8% | `CachedDab.get_IsScaled()` → forces slow mask path |
| 0.66s | 5% | `ApplyCachedDabsLightenToTile` (tile-major fallback) |
| 0.20s | 1% | `TryGetCachedDab` / dab bake |
| 0.83s | 6% | Compositor (`LayerCompositor` total) |

**Root cause on live app:** lighten-scratch takes slow path because `dab.IsScaled` is true (`LogicalWidth != Bitmap.Width`). Every pixel in the dirty rect calls `SampleMaskAlpha` bilinear instead of direct mask row reads. Render thread pool mostly **idle waiting** (~30s aggregate in `SemaphoreSlim.Wait`).

**Fix (implemented):**
1. Route large SrcOver through **`CachedTileMajorLighten` before** full-dirty `CachedLightenScratch`.
2. Gate scratch via `PreferCachedDabsLightenScratch` — off for diameter ≥256px or dirty area >512².
3. `SampleMaskAlpha` uses nearest-neighbor for scaled LOD dabs (not bilinear).

## Next profiling targets

- Verify `CachedDab.IsScaled` after `TryBakeLargeCircleDab` at 600px
- Re-profile after scratch fast-path fix
