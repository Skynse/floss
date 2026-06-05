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
3. **`NodeBrushTip.GenerateMask`** — classic **Circle** → `ClassicBrushLut`; all other primitives → `BrushTipNodeGraphEvaluator.Evaluate` at full `baseSize`.
4. **`UsesProceduralStampEvaluation`** → **false** for `ProceduralBrushTip` and `NodeBrushTip` (except direct image sampler tips).
5. **Unified large dab bake** — removed `TryBakeLargeCircleDab`; all shapes use `TryBakeLargeMaskDab`.
6. **`BakeDabMaskCpu`** — centers on actual mask `Width`/`Height`, not `BaseMaskSize` alone.
7. **Dab canvas draw** — centers mask/color stamp on bitmap dimensions.

## Files

- `src/Floss.App/Brushes/Graph/BrushTipMaskRasterization.cs`
- `src/Floss.App/Brushes/Graph/BrushTipNodeGraph.cs` — `NodeBrushTip.GenerateMask`
- `src/Floss.App/Brushes/Engine/ClassicBrushLut.cs` — `ToAlpha8Bitmap`
- `src/Floss.App/Brushes/Engine/BrushEngine.cs`
- `tests/Floss.App.Tests/BrushTests.cs`
