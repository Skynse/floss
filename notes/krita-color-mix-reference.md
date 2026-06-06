# Krita color smudge / mixing reference (for Floss)

Research date: 2026-06-05. Source: `/home/neckles/projects/krita`.

Related: `notes/krita-round-brush-reference.md`, Floss `BrushEngine.cs` color-mix paths.

## Plugin entry

| File | Role |
|------|------|
| `plugins/paintops/colorsmudge/kis_colorsmudgeop.cpp` | Paint op: spacing, first-dab skip, dispatches to strategy |
| `plugins/paintops/colorsmudge/KisColorSmudgeStrategy*.cpp` | Strategy variants (Mask, Stamp, Lightness, Legacy) |
| `plugins/paintops/colorsmudge/KisColorSmudgeStrategyBase.cpp` | Core `blendBrush`, smear/dull compositing |
| `plugins/paintops/colorsmudge/KisColorSmudgeSampleUtils.h` | Halton dulling sampler |
| `plugins/paintops/colorsmudge/KisColorSmudgeSource.cpp` | Read pixels from layer/image overlay |
| `libs/image/KisOverlayPaintDeviceWrapper.{h,cpp}` | U16 overlay for precise blending |
| `libs/pigment/KoMixColorsOpImpl.h` | Weighted color averaging (dulling pickup) |
| `libs/image/kis_painter.cc` | `bltFixedWithFixedSelection` — mask-stamp final composite |

## Two smudge-length modes

`KisSmudgeLengthOptionData::Mode` (`KisSmudgeLengthOptionData.h`):

| Mode | Krita UI | Floss `SmudgeMode` |
|------|----------|-------------------|
| `SMEARING_MODE` | Smearing | `Smear` |
| `DULLING_MODE` | Dulling | `Smudge` (closest) |

`Blend` in Floss has no direct Krita equivalent — Krita always runs smudge-length on every dab; Floss `Blend` is batch pigment prep without spatial offset.

## Per-dab pipeline (new engine, alpha-mask brush)

From `KisColorSmudgeOp::paintAt` + `KisColorSmudgeStrategyWithOverlay::paintDab`:

```
1. updateMask()           → alpha8 brush mask dab
2. srcDabRect = dstDabRect translated by (lastCenter - newCenter)
3. skip first dab         → same as Floss SmearFirstDabPending
4. readRects(src + dst)   → preload overlay from layer
5. blendBrush()           → build m_blendDevice (full dab color buffer)
6. bltFixedWithFixedSelection() → stamp blendDevice onto overlay using mask
7. writeRects()           → flush overlay back to layer
```

**Critical:** intermediate color math happens on a **full dab buffer** (`m_blendDevice`), not per-pixel writes to the live layer. Final brush mask is applied **once** at the end.

## blendBrush() internals (`KisColorSmudgeStrategyBase.cpp`)

### Opacity formulas

```cpp
colorRateOpacity  = colorRateValue * colorRateValue * opacity;   // squared
dullingRateOpacity = 0.8 * smudgeRateValue * opacity;
smearRateOpacity   = smudgeRateValue * opacity;
```

Floss matches squared color rate in `ComputeEffectivePaintAmount` (`amount² × colorLoad`).

### Smearing path (`!m_useDullingMode`)

`blendInBackgroundWithSmearing(dst, src, srcRect, dstRect, smudgeRateOpacity)`:

1. Read current dst pixels into `m_blendDevice` at `dstRect`
2. Read **offset** source (`srcRect`) into temp device
3. `m_smearOp->composite(dst, temp, smudgeRateOpacity)`

`smearCompositeOp(smearAlpha)` (new engine):

- `smearAlpha == true`  → `COMPOSITE_COPY` (copy smeared pixels including alpha)
- `smearAlpha == false` → `COMPOSITE_OVER`

At full smudge rate + COPY: direct `src->readBytes(dst, srcRect)` (no composite).

### Dulling path (`m_useDullingMode`)

1. `sampleDullingColor()` — Halton-weighted pickup (see below)
2. `blendInBackgroundWithDulling()` — composite prepared dulling color over dst footprint via `m_smearOp`

### Color rate (new paint deposition)

`colorRateOp` = painter's composite op (typically `COMPOSITE_OVER` / Normal).

`DabColoringStrategyMask::blendInColorRate` composites solid `currentPaintColor` over blend device at `colorRateOpacity`.

### Final stamp

```cpp
m_finalPainter.setCompositeOpId(COMPOSITE_COPY);  // new engine
m_finalPainter.bltFixedWithFixedSelection(dstRect, m_blendDevice, maskDab, ...);
```

Mask multiplies dab opacity via `KisPainter::bltFixedWithFixedSelection` → `bitBlt` with mask row as opacity channel.

## Spatial smear offset (matches Floss)

```cpp
// kis_colorsmudgeop.cpp
QRect srcDabRect = m_dstDabRect.translated((m_lastPaintPos - newCenterPos).toPoint());
m_lastPaintPos = newCenterPos;
```

Uses **dab rect center** (not scattered cursor) — same rationale as Floss `LastSmearCenterX/Y`.

Subpixel precision disabled in smearing mode (bug 327235).

## Dulling color pickup (`KisColorSmudgeSampleUtils.h`)

Halton sequence sampler, converges when `differenceA <= 2`.

**Weighted** (`WeightedSampleWrapper`, new Mask engine):

```cpp
const qint16 opacity = maskDab[px, py];           // brush mask weight
mixer->accumulate(pixelPtr, &opacity, opacity, 1);
```

**Averaged** (`AveragedSampleWrapper`, Legacy engine): uniform weights, ignores mask.

Restart with bigger radius if `mixer->currentWeightsSum() < 128` (all samples masked out).

### KoMixColorsOp mixing (`KoMixColorsOpImpl.h`)

**No LCh / perceptual path.** Native color-space channels:

```cpp
alphaTimesWeight = pixel[alpha] * maskWeight;
totals[channel] += pixel[channel] * alphaTimesWeight;
totalAlpha += alphaTimesWeight;
// result:
dst[channel] = safeDivideWithRound(totals[channel], totalAlpha);  // +divisor/2 for integers
dst[alpha]   = safeDivideWithRound(totalAlpha, normalizeFactor);
```

Alpha-premultiplied weighted average. Integer division uses **rounded** divide (`safeDivideWithRound`).

Floss `SampleHaltonDullingColor` mirrors this structure (`weight * a/255` for RGB, separate alpha average) but runs in float32, then truncates to `byte`.

## Overlay / precision (`KisOverlayPaintDeviceWrapper`)

Color smudge wraps the paint layer in `LazyPreciseMode`:

- U8 layer → blend in **RGBA16 overlay**
- `readRect` before dab, `writeRect` after
- Avoids precision loss when many low-opacity compositing passes stack

Floss smear writes **directly to U8 tiles** with integer `AlphaLockPixelOps` — no elevated-precision accumulator.

## Legacy vs new engine differences

| | New (`useNewEngine`) | Legacy |
|--|---------------------|--------|
| Dulling sample | Mask-weighted Halton | Uniform average |
| Smear composite | COPY or OVER | Always COPY |
| Final composite | COPY (opacity 1) | COPY or OVER |
| Final painter opacity | 1.0 | `smudgeRate * opacity` |
| Dulling rate | `0.8 * smudgeRate * opacity` | 1.0 |
| Smear rate | `smudgeRate * opacity` | 1.0 |

## Floss mapping

| Concept | Krita | Floss (`BrushEngine.cs`) |
|---------|-------|--------------------------|
| Smearing | `blendInBackgroundWithSmearing` on dab buffer | `TryRenderSpatialSmearStamp` per-pixel src-over on live tiles |
| Dulling | `sampleDullingColor` + `blendInBackgroundWithDulling` | `SampleHaltonDullingColor` + `PrepareOneStampColor` |
| Blend batch | N/A (always per-dab) | `RasterizeColorMixBatch` + scratch buffer |
| Color pickup mix | `KoMixColorsOp` (premul RGBA) | `MixColors` / `MixRgb` — optional **LCh perceptual** (`MixingMode.Perceptual`) |
| Smear mask application | Once at `bltFixedWithFixedSelection` | Per-pixel `maskA * smearRate` in composite alpha |
| Precision | RGBA16 overlay | U8 throughout |
| Snapshot / no self-read | Overlay read before dab build | Tile snapshots at dab start (`_smearSnapshots`) for smear; `PixelSampler` for batch |
| First dab | Skipped | `SmearFirstDabPending` |
| Color rate | `colorRate² × opacity` | `ComputeEffectivePaintAmount` = `amount² × colorLoad` |
| Smear rate | `smudgeRate × opacity` | `stampOpacity × (0.2 + 0.8 × colorStretch)` |

## Translucent pixels (user-reported trigger)

Krita **never** mixes raw unpremultiplied RGB for smear/dulling. Every path respects source alpha:

### KoCompositeOpCopy2 (smear, partial opacity)

```69:87:../krita/libs/pigment/compositeops/KoCompositeOpCopy2.h
// premultiply → lerp → unpremultiply
dstMult = mul(dst[i], dstAlpha);
srcMult = mul(src[i], srcAlpha);
blendedValue = lerp(dstMult, srcMult, opacity);
dst[i] = divide(blendedValue, newAlpha);
newAlpha = lerp(dstAlpha, srcAlpha, opacity);
```

### KoMixColorsOp (dulling pickup)

```267:286:../krita/libs/pigment/KoMixColorsOpImpl.h
alphaTimesWeight = pixel[alpha] * maskWeight;
totals[channel] += pixel[channel] * alphaTimesWeight;
totalAlpha += alphaTimesWeight;
dst[channel] = safeDivideWithRound(totals[channel], totalAlpha);
```

### Floss gaps on translucent input

| Location | Bug |
|----------|-----|
| `MixColors` / `MixRgb` Standard | Linear RGB interp ignoring alpha → muddy/dark when inputs are semi-transparent |
| `DecayAlpha` | Scales alpha only, leaves RGB → invalid unpremultiplied carried pigment |
| `TryRenderSpatialSmearStamp` | `CompositeBrushPixel(cover=mask×rate)` on full `sb,sg,sr` without source alpha — not COPY premul |

Floss `SampleHaltonDullingColor` / `SamplePigmentUnderDab` already premul-average correctly (matches KoMixColorsOp).

## Likely causes of Floss dark artifacts (Krita comparison)

Previous fix (weight smear by source alpha) did **not** match Krita's model and did not help — Krita delegates alpha handling to `KoCompositeOp` on the **full dab buffer**, not `pickA = smearA * sa / 255` per pixel.

More plausible gaps vs Krita:

1. **Dab-buffer vs per-pixel smear** — Floss composites smeared pixels into the live layer one mask-weighted pixel at a time. Krita builds a full dab, smears at uniform `smudgeRateOpacity`, then applies the brush mask once. Order-of-operations differs when mask varies inside the dab.

2. **No U16 overlay** — many stacked low-opacity smear writes on U8 will darken/quantize differently than Krita's RGBA16 intermediate.

3. **Perceptual LCh mixing** — Krita never uses LCh in colorsmudge. Floss `MixingMode.Perceptual` on Smudge preset + `SampleHaltonDullingColor` / `MixColors` may produce out-of-gamut dark RGB. Smear preset uses `Standard`, but blur path still calls `MixColors` when `blur < 0.85`.

4. **Legacy COPY semantics** — Krita smear with `smearAlpha=true` uses `COMPOSITE_COPY` (replace pixel). Floss always src-over smeared RGB while separately updating alpha — different when picking up partial-alpha pixels.

5. **Scratch batch path** — `RenderColorMixStampsViaScratch` hand-rolled integer src-over (Blend mode / fallback) lacks Krita's composite-op rounding; separate from spatial smear but may cause similar blotches in other mix modes.

## Suggested investigation order

1. Reproduce with **Smear + Standard mixing + blur=0** — isolates spatial smear from LCh/blur `MixColors`.
2. Port Krita smear structure: build dab-sized buffer → `blendInBackgroundWithSmearing` equivalent → single masked stamp (like `bltFixedWithFixedSelection`).
3. Add U16 (or float) dab accumulator for color-mix writes; flush to U8 once per dab.
4. Replace Floss dulling `MixColors` perceptual path with `KoMixColorsOp`-style premultiplied RGBA average for Smudge/Dulling parity.
5. Align smear alpha semantics with `COMPOSITE_COPY` when smearing opaque strokes.

## Key snippets

### Krita smear composite

```264:283:../krita/plugins/paintops/colorsmudge/KisColorSmudgeStrategyBase.cpp
void KisColorSmudgeStrategyBase::blendInBackgroundWithSmearing(...)
{
    if (m_smearOp->id() == COMPOSITE_COPY && qFuzzyCompare(smudgeRateOpacity, OPACITY_OPAQUE_F)) {
        src->readBytes(dst->data(), srcRect);
    } else {
        src->readBytes(dst->data(), dstRect);
        // read offset srcRect into tempDevice
        m_smearOp->composite(dst->data(), ..., tempDevice.data(), ..., smudgeRateOpacity);
    }
}
```

### Krita final mask stamp

```249:259:../krita/plugins/paintops/colorsmudge/KisColorSmudgeStrategyBase.cpp
Q_FOREACH (KisPainter *dstPainter, dstPainters) {
    dstPainter->setOpacityF(finalPainterOpacity(opacity, smudgeRateValue));
    dstPainter->bltFixedWithFixedSelection(dstRect.x(), dstRect.y(),
                                           m_blendDevice, maskDab, ...);
}
```

### Krita KoMixColorsOp accumulate

```267:286:../krita/libs/pigment/KoMixColorsOpImpl.h
alphaTimesWeight = color[alpha_pos];
weightsWrapper.premultiplyAlphaWithWeight(alphaTimesWeight);
totals[i] += color[i] * alphaTimesWeight;  // i != alpha
totalAlpha += alphaTimesWeight;
```

### Floss spatial smear (current)

```3549:3557:src/Floss.App/Brushes/Engine/BrushEngine.cs
var smearA = (int)(maskA * smearRate + 0.5f);
AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a,
    sb, sg, sr, (byte)smearA, layer.IsAlphaLocked, SKBlendMode.SrcOver);
```
