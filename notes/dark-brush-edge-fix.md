# Crucial fix: dark halos on brush strokes (2026-06-06)

**Status:** Confirmed fixed in app — user verified after rebuild.

## Symptom

Drawing with a solid round brush (especially large / cached dabs): dark gray or black fringes at stamp edges, dark lines where circular stamps overlap, sometimes visible stamp stepping. Saturated stroke color (e.g. blue) looked fine in the center but wrong at anti-aliased boundaries once composited on white paper.

## What did *not* fix it

Premul/unpremul cleanup on **image tips and node graph** (`ImageBrushTip`, `BrushTipNodeGraph.ImageSamplerColor`, `SampleColorDabPixel`, UI previews). Those paths matter for image/graph tips but **not** for the default procedural round brush, which hits the CPU cached-dab row stamp path instead.

See also: `notes/premul-alpha-audit.md` (secondary fixes, still valid for image tips).

## Root cause

Layer tiles store **unpremul BGRA** with Porter-Duff src-over (`SimdPixelOps.StampSrcOver`, compositor normal blend).

The hot path for cached round-brush dabs is:

```
BrushEngine.ApplyCachedDabToTile
  → SimdPixelOps.StampSrcOverMaskedRow   (row of mask × solid color)
```

`StampSrcOverMaskedRowAvx2` implemented a **Drawpile-style fast path** for rows where all destination alphas were 255:

- RGB: linear lerp `(src×sa + dst×(255−sa)) >> 8`
- Alpha: forced to **255** via `Avx2.Or(packed, s_alphaByteSet)`

That is correct for **alpha-preserving paint onto an already-opaque canvas**, not for **accumulating semi-transparent pixels into unpremul layer tiles**. At soft mask edges it could leave pixels with non-zero alpha but RGB too dark (near black). The compositor then src-over blends those onto white → classic dark halo.

Wrong formula (AVX, opaque-dst fast path):

```csharp
// BlendConstSrcKernel — NOT Porter-Duff for unpremul layer storage
result_ch = (srcCh * sa + dstCh * (255 - sa)) >> 8
packed = Avx2.Or(packed, s_alphaByteSet);  // force opaque
```

Correct formula (scalar path, now used for normal src-over):

```csharp
// StampSrcOverMaskedRowScalar — premul intermediate, unpremul result
pcB = (dstB * da + 127) / 255;
outPcB = (pcB * invSa + srcB * sa + 127) / 255;
outA = (da * invSa + 255 * sa + 127) / 255;
dstB = (outPcB * recip255[outA] + 128) >> 8;
```

Same math as `StampSrcOver` / `AlphaLockPixelOps.ApplySrcOver`.

## Fix (keep this)

**File:** `src/Floss.App/Canvas/Engine/SimdPixelOps.cs`

| Mode | Path |
|------|------|
| Normal src-over (`alphaLocked == false`) | **Always** `StampSrcOverMaskedRowScalar` (associated-alpha). **No AVX.** |
| Alpha lock | AVX2 allowed (`StampSrcOverMaskedRowAvx2`); linear rgb blend, preserve dst alpha. |

Entry point:

```csharp
if (alphaLocked && Avx2.IsSupported && Sse41.IsSupported && count >= 8)
    StampSrcOverMaskedRowAvx2(..., alphaLocked: true);
else
    StampSrcOverMaskedRowScalar(...);
```

## Call sites (do not reintroduce wrong blend here)

| Caller | File |
|--------|------|
| Cached dab per-tile stamp | `BrushEngine.ApplyCachedDabToTile` (~2702) |
| Tile-major cached raster | `BrushEngine.TryRasterizeCachedDabsTileMajor` |

Stroke mask composite (`ApplyStrokeMaskToLayer`) uses per-pixel `StampSrcOver` — was already correct.

## Regression tests

`tests/Floss.App.Tests/StampSrcOverMaskedRowTests.cs`

- Soft mask edge on transparent dst → full brush RGB, not `(0,0,0,α)`.
- Second stamp over semi-transparent dst → associated-alpha buildup.

## Rules for future SIMD brush work

1. **Layer tiles = unpremul BGRA.** Any fast path must match `StampSrcOver` / `StampSrcOverMaskedRowScalar`, not Drawpile premul tile ops or linear lerp + forced opaque alpha.
2. **`BlendTilePremultiplied` / `BlendConstSrcKernel`** are for premul buffers only — not wired to layer writes today; do not use for `StampSrcOverMaskedRow` without a format change.
3. Image-tip premul fixes are **orthogonal**; round-brush bugs → check `StampSrcOverMaskedRow` first.
4. Symptom on screen but “premul audit” unchanged → inspect **CPU stamp SIMD**, not only Skia tip bitmaps.

## Related files

- `src/Floss.App/Canvas/Engine/SimdPixelOps.cs` — fix lives here
- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — `ApplyCachedDabToTile`, stroke mask paths
- `notes/premul-alpha-audit.md` — image/graph tip premul issues (separate)
