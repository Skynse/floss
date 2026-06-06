# Large brush color dimming fix

## Symptom

Large brushes (≥256px cached path) look washed out / dimmer than small brushes at the same opacity and full pressure, especially along stroke bodies with heavy dab overlap.

## Root cause (Jun 5 optimization, `9315929`)

`StrokeMaskCached`, `CachedTileMajorLighten`, and `CachedLightenScratch` merged overlapping dabs with **per-pixel max alpha**:

```csharp
if (stampA > maskTile[idx])
    maskTile[idx] = (byte)stampA;
```

For solid SrcOver strokes, max-alpha is **not** equivalent to sequential dab compositing. In overlap zones each pixel keeps the strongest single dab contribution instead of building up:

```
compound: outA = a + b*(255-a)/255
max:      outA = max(a, b)   // lighter when a≈b
```

Large brushes have far more overlap area than small brushes, so the stroke reads as globally dimmer even when dab centers still hit peak opacity.

`b00923c` also removed `TryBakeLargeCircleDab` (direct `ClassicBrushLut.GetStamp`); circles now always bake via `TryBakeLargeMaskDabCpu` bilinear resample.

## Fix

1. **Mask accumulation** — replace max-alpha with Porter-Duff src-over alpha accumulation for same-color masks:
   - `MaxCachedDabsIntoStrokeMask`
   - `ApplyCachedDabsLightenToTile`
   - `TryRasterizeCachedDabsLightenScratch`
2. **Stroke mask reapply region** — track cumulative `StrokeMaskDirty` on `ActiveStroke`; each live batch rebuilds all tiles touched by the stroke mask, not only the current segment dirty rect (avoids stale tiles during UI time-slices).
3. **Restore `TryBakeLargeCircleDab`** for plain procedural circles / soft rounds at ≥256px (LUT stamp, no extra bilinear bake).

Final layer composite remains `SrcOver(beforeTile, brushColor, accumulatedMask)` — correct for solid color once mask uses src-over accumulation.

## Files

- `src/Floss.App/Brushes/Engine/BrushEngine.cs`
- `tests/Floss.App.Tests/BrushTests.cs` — peak/mean opacity regression tests

## References

- `notes/large-stamp-cached-raster.md` — original max-alpha stroke mask rationale (banding fix)
- `notes/dark-brush-edge-fix.md` — orthogonal SIMD edge fix
