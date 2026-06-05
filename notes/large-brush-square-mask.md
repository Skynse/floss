# Large brush becomes squarish / octagonal

## Symptom

Circle brushes (e.g. Technical Pen at 488px, 96% hardness) turn octagonal then fully square on canvas and in previews as size increases.

## Cause

1. **`BaseMaskSize` capped at 512** while `brush.Size` keeps growing → dab bake upscaled a 512px node-graph mask to 600–1024px. High hardness + upsample → faceted / boxy edge.
2. **Node-graph `EvaluateViaStamp`** rasterizes analytic circles on a square grid — fine at small sizes, wrong path for large built-in circles vs Drawpile LUT.
3. **List preview** used a **28px mask** for all large brushes → blocky icon strokes.
4. **`SampleMaskAlpha` nearest-neighbor** on scaled LOD dabs (when `IsScaled`) amplified blockiness (reverted to bilinear).

## Fix (Krita-aligned, all procedural/node shapes)

1. **`BrushTipMaskRasterization`** — policy: procedural + node graphs rasterize masks at full stamp size; cached dab composite only.
2. **`StrokeBaseMaskSize`** — `min(4096, ceil(brush.Size))` (was capped at 512).
3. **`MaskResolutionForStamp`** — mask diameter = **stamp diameter** (no `max(stamp, brush.Size)` upsize/downscale mismatch).
4. **`ActiveStroke.MaskFor(tip, hardness, stampDiameter)`** — cache key `(tip, hardness, resolution)`; dab bake uses `key.Size`.
5. **`ComputeDabLayout`** — scale from actual mask `Width`/`Height`, not frozen `BaseMaskSize`.
6. **`NodeBrushTip.GenerateMask`** — classic **Circle** → `ClassicBrushLut`; all other primitives → full-size graph raster.
7. **`UsesProceduralStampEvaluation`** → **false** for procedural/node tips (except direct image sampler).
8. **Skia fallback / preview** — matrix scale uses per-dab mask resolution, not stroke-start `BaseMaskSize`.
9. **`BrushPreparationScheduler`** — warms masks at `StrokePeakMaskSize` (up to 4096).

## Files

- `src/Floss.App/Brushes/Graph/BrushTipMaskRasterization.cs`
- `src/Floss.App/Brushes/Graph/BrushTipNodeGraph.cs` — `NodeBrushTip.GenerateMask`
- `src/Floss.App/Brushes/Engine/ClassicBrushLut.cs` — `ToAlpha8Bitmap`
- `src/Floss.App/Brushes/Engine/BrushEngine.cs`
- `tests/Floss.App.Tests/BrushTests.cs`
