# Node graph raster paths

## Paths (by tip + brush size)

| Condition | Raster path |
|-----------|-------------|
| `NodeBrushTip` + fast-path graph + brush &lt; 128px | `RasterizeStampsDirect` (fused `BrushTipStampFastPath`) |
| `NodeBrushTip` + complex graph + brush ≥ 128px | `CachedTileMajor` (mask bake via `TryBakeLargeMaskDabCpu`) |
| `NodeBrushTip` + image sampler | Bake once (`UsesProceduralStampEvaluation` false), cached ≥ 128px |
| `ProceduralBrushTip` circle + brush ≥ 256px | `ClassicBrushLut` CPU dab + cached tile-major / stroke mask |

## Optimization applied

- **`NodeGraphCachedRasterMinDiameterPx = 128`**: non-image-sampler node graphs route through cached dab + tile-major at 128px+ instead of Skia fallback / per-pixel eval on medium brushes.

## Future

- Fused fast path for `Multiply(Circle, Noise)` chains (common textured round brushes).
- Prewarm node-graph dab bake in `BrushPreparationScheduler` for active preset.

## Files

- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — `PreferCachedTileMajorRaster`, `TryBakeLargeMaskDabCpu`
- `src/Floss.App/Brushes/Graph/BrushTipStampFastPath.cs` — fused eval for simple graphs
