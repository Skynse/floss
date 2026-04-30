# Performance Notes

## Remaining per-pixel dict-lookup hotspots

### TiledPixelBuffer.CopyFromBgra (both overloads)
Both overloads iterate pixel-by-pixel and call `WritePixel` per non-transparent pixel,
which does a dict lookup + tile create per pixel.

Used in two places:
- `LayerCompositor.GetGroupProjection` — after compositing a group's children into a
  flat temp buffer, the result is written back into the group's projection cache via
  `cache.Buffer.CopyFromBgra(dirty, temp, dirty.Width * 4)`. This fires on every cache
  miss for a non-passthrough group layer. Fix: rewrite to iterate by tile, using
  `ForEachTile` + `Buffer.BlockCopy` row-by-row (same pattern as `Capture`/`Restore`),
  skipping fully-transparent tiles.
- `PsdImporter.CopyPixels` — one-time import, not a perf issue.

### PsdFormat.cs GetPixel
`PsdFormat` calls `layer.Pixels.GetPixel(x, y, ...)` per pixel when writing PSD channel
data. One-time export operation — not a hot path.
