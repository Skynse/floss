# Krita round brush engine reference (for Floss)

Research date: 2026-06-05. Source: `/home/neckles/projects/krita`.

Related: `notes/krita-brush-spacing-reference.md` (spacing only).

## Architecture overview

Round “auto” brushes in Krita are **procedural mask brushes**, not texture pyramids.

```
KisAutoBrush
  └── KisMaskGenerator (shape + hardness model)
        ├── KisCircleMaskGenerator        (Default / “hard” circle)
        ├── KisCurveCircleMaskGenerator   (Soft — cubic curve falloff)
        └── KisGaussCircleMaskGenerator   (Gaussian erf edge)
              └── KisBrushMaskApplicator  (scalar or SIMD vector)
                    └── fills KisFixedPaintDevice dab per stamp
```

**Key files**

| Area | Path |
|------|------|
| Auto brush + dab generation | `libs/brush/kis_auto_brush.cpp` |
| Brush factory (circle vs rect) | `libs/brush/kis_auto_brush_factory.cpp` |
| Mask generator base | `libs/image/kis_base_mask_generator.{h,cpp}` |
| Default circle | `libs/image/kis_circle_mask_generator.{h,cpp}` |
| Soft circle (curve) | `libs/image/kis_curve_circle_mask_generator.cpp` |
| Gaussian circle | `libs/image/kis_gauss_circle_mask_generator.cpp` |
| Per-pixel dab fill | `libs/image/kis_brush_mask_scalar_applicator.h` |
| SIMD large-dab path | `libs/image/kis_brush_mask_vector_applicator.h` |
| Dab dimensions / scale | `libs/brush/kis_qimage_pyramid.cpp` (`imageSize`) |
| Dab cache | `plugins/paintops/libpaintop/kis_dab_cache_base.cpp` |
| Spacing | `libs/image/brushengine/kis_paintop_utils.{h,cpp}` |

## Brush size: no base-mask cap

- `KisMaskGenerator` stores **`diameter` in pixels** (`kis_base_mask_generator.cpp`).
- `KisAutoBrush::setUserEffectiveSize()` calls `shape->setDiameter(value)`.
- Each dab’s bitmap size comes from `maskWidth()` / `maskHeight()` → `KisQImagePyramid::imageSize()` at the current `KisDabShape` scale/rotation.
- **There is no 512px (or similar) base resolution** that gets upscaled for large brushes. A 1024px brush produces a ~1024px dab (plus sub-pixel margin via pyramid math).

Contrast with Floss (pre-fix): `BaseMaskSize` capped at 512, then baked/upscaled → blocky/square edges at high hardness.

## Mask generator parameters (circle)

From `KisAutoBrushFactory::createBrush()` / `MaskGenerator` XML:

| Param | Meaning |
|-------|---------|
| `diameter` | Brush size in pixels |
| `ratio` | Aspect ratio (`height = diameter * ratio` when `spikes == 2`) |
| `hfade`, `vfade` | Horizontal / vertical fade (hardness edge width); stored as `fh = 0.5 * hfade` internally |
| `spikes` | `2` = smooth circle/ellipse; `>2` = polygon star |
| `antialiasEdges` | Enables supersampling for tiny dabs |
| `id` | `default` / `soft` / `gauss` — picks generator class |

Example built-in default circle XML (from `kis_brush_based_paintop.cpp`):

```xml
<MaskGenerator spikes="2" hfade="1" ratio="1" diameter="40"
               id="default" type="circle" antialiasEdges="1" vfade="1"/>
```

## Default circle alpha model (`KisCircleMaskGenerator::valueAt`)

Coordinates are **brush-local pixels** relative to dab center (applicator rotates first).

```cpp
// kis_circle_mask_generator.cpp — inverted mask convention
qreal xr = x, yr = qAbs(y);
fixRotation(xr, yr);  // spikes > 2 only

qreal n = norme(xr * d->xcoef, yr * d->ycoef);  // normalized ellipse distance²
if (n > 1.0) return 255;                         // outside → transparent

// optional +1px AA bias when antialiasEdges
qreal nf = norme(xr * d->transformedFadeX, yr * d->transformedFadeY);
if (nf < 1.0) return 0;                          // hard core → opaque

return 255 * n * (nf - 1.0) / (nf - n);           // soft edge ramp
```

**Convention:** `valueAt` returns **0 = opaque, 255 = transparent**. Applicator converts:

```cpp
// kis_brush_mask_scalar_applicator.h
alphaValue = quint8((OPACITY_OPAQUE_U8 - value) * random);
```

Coefficients update on `setScale()` / `setSoftness()`:

```cpp
d->xcoef = 2.0 / effectiveSrcWidth();
d->ycoef = 2.0 / effectiveSrcHeight();
d->xfadecoef = horizontalFade() == 0 ? 1 : 2.0 / (horizontalFade() * effectiveSrcWidth());
// softness: safeSoftnessCoeff = 1 / max(0.01, softness)
d->transformedFadeX = d->xfadecoef * d->safeSoftnessCoeff;
```

`setSoftness(softnessFactor)` is called **per dab** from `KisAutoBrush::generateMaskAndApplyMaskOrCreateDab()` (dynamics / paintop can vary it per stamp).

## Soft and Gaussian variants

**Soft (`KisCurveCircleMaskGenerator`)** — hardness from a **cubic curve LUT** whose resolution scales with brush size:

```cpp
// OVERSAMPLING = 4 in kis_base_mask_generator.h
d->curveResolution = qRound(qMax(width(), height()) * OVERSAMPLING);
d->curveData = curve.floatTransfer(d->curveResolution + 2);
```

**Gaussian (`KisGaussCircleMaskGenerator`)** — edge via `erf()`:

```cpp
quint8 ret = alphafactor * (erf(dist + center) - erf(dist - center));
return (quint8) 255 - ret;
```

## Dab generation pipeline (`KisAutoBrush::generateMaskAndApplyMaskOrCreateDab`)

1. `dstWidth` / `dstHeight` from `maskWidth(shape, subPixelX, subPixelY, info)`.
2. Hot spot = dab center: `QPointF(w/2, h/2)` (`kis_brush.cpp`).
3. `centerX/Y = hotSpot - 0.5 + subPixel` (sub-pixel dab placement).
4. `shape->setSoftness(softnessFactor)` then `shape->setScale(shape.scaleX(), shape.scaleY())`.
5. Fill dab color channel (if needed).
6. `applicator->initializeData(&data)` + `applicator->process(QRect(0,0,dstWidth,dstHeight))`.

Per pixel (scalar path):

```cpp
double x_ = x + sx * invss - centerX;
double y_ = y + sy * invss - centerY;
double maskX = cosa * x_ - sina * y_;
double maskY = sina * x_ + cosa * y_;
value += maskGenerator->valueAt(maskX, maskY);
// supersample average → invert → applyAlphaU8Mask
```

## Small vs large dab quality paths

| Condition | Behavior |
|-----------|----------|
| `antialiasEdges && effectiveSize < 10` | 3×3 supersampling |
| `effectiveSize < 1` | 6×6 supersampling |
| Circle, `spikes == 2`, not supersampling | **SIMD vector** row processor (`shouldVectorize()`) |
| Otherwise | Scalar `valueAt` loop |

No separate “large circle LUT” fast path — same analytic generator at all sizes; vectorization handles big dabs.

## Dab caching

- `KisDabCacheBase` keys on width, height, sub-pixel offsets, softness, angle, color, etc.
- `KisAutoBrush::supportsCaching()` → `density == 1 && randomness == 0`.
- Cache stores **full-resolution** generated dabs, not downsampled masks.
- Auto brush explicitly **skips image pyramid** (`notifyBrushIsGoingToBeClonedForStroke` no-op).

## Spacing (round brushes)

See `notes/krita-brush-spacing-reference.md`. Summary:

```cpp
// kis_paintop_utils.h
calcAutoSpacing(value, coeff) = coeff * (value < 1.0 ? value : sqrt(value));

// isotropic manual: qMax(dabWidth, dabHeight) * spacingVal
// isotropic auto:   calcAutoSpacing(significantDimension, autoSpacingCoeff)
```

`KisBrushBasedPaintOp::effectiveSpacing()` passes `characteristicSize(scale).width/height` as dab dimensions.

Min gap: `MIN_DISTANCE_SPACING = 0.5` px (`kis_distance_information.cpp`).

## Implications for Floss (all procedural / node shapes)

| Krita | Floss (`BrushTipMaskRasterization`) |
|-------|--------------------------------------|
| Analytic `valueAt` at full dab size | Graph `Evaluate` at full `baseSize`; plain Circle uses Drawpile LUT |
| No base-mask cap | `StrokeBaseMaskSize` up to 4096 |
| Single applicator path | Unified `TryBakeLargeMaskDab` for procedural, node, and image tips |
| Per-dab `setSoftness` | Per-stamp hardness via `MaskFor(stamp.Hardness)` |
| Rectangle / soft / gauss generators | Rectangle, Flat, Ellipse, Chalk, Bristle, Scatter via graph raster at full size |
| Inverted mask convention (0=opaque) | Floss Alpha8 (255=opaque) |

**Root lesson:** never upscale a small template mask. `UsesProceduralStampEvaluation` is false for all `ProceduralBrushTip` and `NodeBrushTip` except direct image samplers — same as Krita baking a full dab via mask generator + applicator, not UV-square per-pixel fallback.

## Snippets to re-use when porting

**Circle distance + fade (default)**

```71:92:../krita/libs/image/kis_circle_mask_generator.cpp
quint8 KisCircleMaskGenerator::valueAt(qreal x, qreal y) const
{
    if (isEmpty()) return 255;
    qreal xr = (x /*- m_xcenter*/);
    qreal yr = qAbs(y /*- m_ycenter*/);
    fixRotation(xr, yr);

    qreal n = norme(xr * d->xcoef, yr * d->ycoef);
    if (n > 1.0) return 255;
    // ...
    return 255 * n * (nf - 1.0) / (nf - n);
}
```

**Auto spacing**

```162:165:../krita/libs/image/brushengine/kis_paintop_utils.h
inline qreal calcAutoSpacing(qreal value, qreal coeff)
{
    return coeff * (value < 1.0 ? value : sqrt(value));
}
```

**Dab fill entry**

```326:348:../krita/libs/brush/kis_auto_brush.cpp
    d->shape->setSoftness(softnessFactor);
    d->shape->setScale(shape.scaleX(), shape.scaleY());
    // ...
    applicator->initializeData(&data);
    applicator->process(rect);
```
