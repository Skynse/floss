# Large brush becomes squarish / octagonal

## Symptom

Circle brushes (e.g. Technical Pen at 488px, 96% hardness) turn octagonal then fully square on canvas and in previews as size increases.

## Cause

1. **`BaseMaskSize` capped at 512** while `brush.Size` keeps growing → dab bake upscaled a 512px node-graph mask to 600–1024px. High hardness + upsample → faceted / boxy edge.
2. **Node-graph `EvaluateViaStamp`** rasterizes analytic circles on a square grid — fine at small sizes, wrong path for large built-in circles vs Drawpile LUT.
3. **List preview** used a **28px mask** for all large brushes → blocky icon strokes.
4. **`SampleMaskAlpha` nearest-neighbor** on scaled LOD dabs (when `IsScaled`) amplified blockiness (reverted to bilinear).

## Fix

1. `ProceduralBrushTip.GenerateMask` for **Circle / SoftRound** → `ClassicBrushLut.ToAlpha8Bitmap` at full `baseSize` (Drawpile LUT, resolution-independent).
2. `BaseMaskSize` cap raised **512 → 4096** so non-LUT tips bake at stamp resolution.
3. **Unify large dab path with textured/image brushes**: remove `TryBakeLargeCircleDab`; large circles use `TryBakeLargeMaskDab` only.
4. `UsesProceduralStampEvaluation` → **false** for built-in Circle/SoftRound so rasterization does not fall back to square UV graph eval.
5. `BakeDabMaskCpu` centers on actual mask dimensions (not `BaseMaskSize` alone).
6. `BrushStrokePreview` compact row: mask up to **32–96px** scaled with brush size (was fixed 28px).

## Files

- `src/Floss.App/Brushes/Engine/ClassicBrushLut.cs` — `ToAlpha8Bitmap`
- `src/Floss.App/Brushes/Tips/ProceduralBrushTip.cs`
- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — `BaseMaskSize`, `SampleMaskAlpha`
- `src/Floss.App/Controls/BrushStrokePreview.cs`
- `tests/Floss.App.Tests/BrushTests.cs` — `ProceduralCircle_GenerateMask_StaysRoundAtLargeSize`
