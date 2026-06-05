# Brush alpha squares + size mismatch

## Symptoms

1. **Dark squares / vertical bands** at dab overlap and 64px tile seams (large SrcOver brushes).
2. **Faint tile-aligned square** regions with different opacity inside vs outside (mixed compositing).
3. **Stroke footprint ≠ cursor outline** at some sizes (pressure dynamics, quantized dab cache).

## Causes

### Alpha / dark squares

1. **`ApplyStrokeMaskToLayer` rewrote every `mask==0` pixel in the dirty rect to `beforeTile` each batch.** Tiles without a `beforeTiles` entry were cleared to transparent → tile-aligned holes/squares.
2. **`PreferStrokeMaskComposite` required `LiveStroke`**, so some large-brush batches fell through to **`CachedTileMajor` direct SrcOver** (one dab per time-slice) → cross-batch alpha compounding → dark bands.
3. **`perTileLighten` / scratch lighten** only ran when `_stamps.Count > 1` in a single batch, not across UI slices.

### Size mismatch

1. **Cursor uses `_brush.Size`**; stamps use `brush.Size * dynamics * parameter graphs`.
2. **Cached dab baked at `QuantizeLargeStampCacheSize(stamp.Size)`** but placed at full logical size without scaling to actual `stamp.Size`.

## Fix

1. Stroke mask: **init each layer tile once** from `beforeTiles`, then **only write `mask>0` pixels**; always rebuild from original `before` + max-alpha mask.
2. Use stroke mask whenever **`BeforeTiles` is bound** (not only `LiveStroke`).
3. **`PlaceCachedDab`**: center-crop/scaled bounds to `stamp.Size`; fast path only when `renderD == logicalWidth`.
4. **Cursor radius**: apply size dynamics from current pointer pressure.

## Files

- `src/Floss.App/Brushes/Engine/BrushEngine.cs`
- `src/Floss.App/Canvas/DrawingCanvas.cs`
