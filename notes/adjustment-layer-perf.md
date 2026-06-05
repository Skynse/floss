# Adjustment layer compositing

## Processor (`AdjustmentLayerProcessor`)

- **Per-pixel** `TransformRgb` on the composited buffer (no 33³ RGB cube LUT).
- **Identity fast-path**: `IsEffectivelyIdentity` skips work when sliders are neutral (e.g. HSL 0/0/0).
- **Transparent pixels** (`alpha == 0`) are not transformed (avoids corrupting unused RGB).
- **Layer mask**: `ApplyWithMask` walks mask tiles, same transform as unmasked path.
- **Clipping layer**: `ApplyClipped` in `LayerProjectionPlane` — adjustment only where the clip base has alpha.

## Why the RGB cube LUT was removed

The 33³ nearest-neighbor LUT returned `transform(lattice color)` instead of `transform(actual pixel)`, which caused severe banding and wrong colors even at HSL 0/0/0. Parallel tile compositing could also race on `LutCache.Ensure` during rebuild.

## Performance

| Kind | Hot-path cost |
|------|----------------|
| HSL / BC / levels / etc. | Per-pixel math in dirty tiles |
| Tone curve / gradient map | Per-pixel eval (channel curves unchanged) |

Stroke split still recomposites layers above the paint layer each frame; that is expected.

## Key files

- `src/Floss.App/Canvas/Compositing/AdjustmentLayerProcessor.cs`
- `src/Floss.App/Canvas/Compositing/LayerProjectionPlane.cs`
- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs`
