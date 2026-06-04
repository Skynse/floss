# Adjustment layer performance vs LUTs

## User observation

Adjustment layers make painting/compositing very slow. LUTs were expected to fix this.

**Root cause for masked adjustment layers:** `ApplyWithLayer` copied the entire tile clip to scratch, ran the transform on every pixel, then blended back with `GetTileOrNull` **per document pixel** (not per mask tile). Unmasked HSL was already fixed via RGB cube LUT; masked path was the outlier.

## Three different “LUT” systems (not interchangeable)

| System | File | Purpose |
|--------|------|---------|
| Brush stamp LUTs | `Brushes/Engine/ClassicBrushLut.cs` | GIMP-style hardness masks for dab rasterization |
| Blend-mode LUTs | `Compositing/BlendMode.HasLut()` | 15 Photoshop-style layer blend modes (multiply, overlay, …) |
| Adjustment LUTs | `Compositing/AdjustmentLayerProcessor.cs` | **Only** tone curve + gradient map (256-entry tables) |

`ClassicBrushLut` and blend-mode LUTs never run inside `AdjustmentLayerProcessor`.

## What adjustment code actually does

`AdjustmentLayerProcessor.Apply` / `ApplyWithLayer` — per dirty tile region:

| Kind | LUT? | Hot-path cost |
|------|------|----------------|
| Brightness/Contrast | No | Float math per pixel |
| Hue/Sat/Luminosity | No | `RgbToHsl` + shifts + `HslToRgb` per pixel |
| Levels | No | `MathF.Pow` per channel per pixel |
| Tone curve | Yes | **Rebuilds 4× `BuildLut()` every call** |
| Gradient map | Yes | **Rebuilds RGB LUTs every call** |
| Color balance, posterize, binarize, invert | No | Per-pixel math |

Masked or clipping-mask adjustments: allocate `clip.W×clip.H×4` scratch, `MemoryCopy` full region, `Apply` at 100%, then per-pixel mask/base-alpha blend.

## Why strokes feel slow with an adjustment above the paint layer

`LayerCompositor` stroke split (`TryCreateStrokeSplitPlan`):

1. **Below** paint layer: composited once per tile into `_strokeBelowCache` (cached).
2. **Above** paint layer (includes adjustment layers): `CompositeStrokeAboveLayers` runs **every frame** on every dirty tile.

Each pointer move → dirty stamp tiles → full HSL/BC/levels/etc. on tile pixels again.

`DrawingDocument.LayerDirtyRegion`: adjustment **parameter** edits mark **full document** dirty (correct for global effect). Painting on a layer below still uses stamp-sized dirty regions, but above-stack adjustments still run on those tiles every frame.

## Broad fix (implemented)

### 1. RGB cube LUT (`AdjustmentLayerLutCache`)

- 33³ BGR cube baked from exact `TransformPixel` when parameters change (CSP-style 3D LUT).
- Hot path: `Lookup` + opacity blend only — no per-pixel HSL math while painting.
- Lives on `AdjustmentLayerData.LutCache`; signature hash invalidates on param change.

### 2. Masked adjustment (`ApplyWithMask`)

- No scratch buffer: LUT in-place on `dst`.
- Iterate mask **tiles** (one `GetTileOrNull` per tile), same pattern as `LayerCompositorPixelOps`.
- Integer alpha blend for `opacity × maskAlpha`.

## Key files

- `src/Floss.App/Canvas/Compositing/AdjustmentLayerProcessor.cs` — all adjustment math
- `src/Floss.App/Canvas/Compositing/LayerProjectionPlane.cs` — calls `ApplyWithLayer` in stack merge
- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs` — stroke below/above split
- `src/Floss.App/Document/DrawingDocument.cs` — `LayerDirtyRegion` full doc for adjustments
- `src/Floss.App/Brushes/Engine/ClassicBrushLut.cs` — brush only
