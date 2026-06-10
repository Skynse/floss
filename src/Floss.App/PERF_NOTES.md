# Performance Notes

## Remaining per-pixel dict-lookup hotspots

### TiledPixelBuffer.CopyFromBgra (region overload)
Fixed: the `PixelRegion` overload now copies tile-by-tile via `ForEachTile` +
`Buffer.BlockCopy` instead of per-pixel `WritePixel`. KRA import uses
`ImportTile` / `Restore` for the same pattern.

### PsdFormat.cs GetPixel
`PsdFormat` calls `layer.Pixels.GetPixel(x, y, ...)` per pixel when writing PSD channel
data. One-time export operation — not a hot path.
