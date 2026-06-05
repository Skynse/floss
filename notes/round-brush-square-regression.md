# Round brush rendering as squares (regression)

## Symptom

Large round brushes paint solid square/rectangular blocks instead of circular dabs.

## Root cause

Pressure-lag fix keyed large dab cache on **preset `brush.Size`** but placed/sampled dabs at **`stamp.Size`** via center-crop + `maskPad` fast-path offsets.

When `stamp.Size != dab.LogicalWidth` (pressure dynamics, size graph, or `MaxOutput > 1`):

- Fast path used negative `maskPad` when `renderDiameter > logicalWidth`
- `SampleMaskAlpha` indexed past mask bounds when `renderDiameter >= logicalWidth` on the non-scaled branch
- Out-of-bounds mask reads → non-zero alpha across the whole `renderD × renderD` footprint → solid squares

## Fix

1. **Revert center-crop placement** — place dabs at full cached logical bounds (`offset + logical size`), original mask sampling.
2. **Cache key** — `QuantizeLargeStampCacheSize(stamp.Size)` on large brushes (coarse buckets), not `brush.Size` alone.
3. Keep **prewarm on `BeginStroke`**, pen-down sample duplicate, hardness quantize on cache key.
4. Guard fast path: only when `renderDiameter == logicalWidth` (if any scaled sampling remains in scratch paths).

## Node graph optimization (separate)

- Lower cached tile-major threshold to **128px** for `NodeBrushTip` (non-image-sampler) so complex graphs bake once instead of per-pixel / Skia fallback.
- Image-sampler graphs already bake via `TryBakeLargeMaskDabCpu`.

## Files

- `src/Floss.App/Brushes/Engine/BrushEngine.cs`
- `notes/pressure-pen-down-lag.md`
