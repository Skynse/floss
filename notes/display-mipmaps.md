# Display mipmaps (Avalonia / SkiaSharp)

Research date: 2026-06-04. Reference: `notes/drawpile-compositor-comparison.md` Tier C item 8.

## Goal

When the canvas is zoomed out (`CanvasZoom < 1`), draw compositor cell images with GPU-generated mipmaps and linear mip sampling — same idea as Drawpile's optional `glGenerateMipmap` on display textures. This is **presentation-only**; composite resolution stays full (`SelectLod` remains 0).

Does **not** fix live-stroke compositor jank at 1:1 zoom; it improves zoom-out quality and can reduce aliasing shimmer when the viewport scale shrinks the cell bitmaps.

## Current Floss display path

| Step | Location |
|------|----------|
| Merge 64×64 tiles into 16384×16384 cell `SKBitmap` | `LayerCompositor.CopyTileToCell` |
| Snapshot on revision change | `EnsureCellDisplayImage` → `SKImage.FromBitmap(bmp)` |
| Draw | `DrawTiles` → `SkiaTileDrawOp.Render` → `DrawImage(..., Nearest, None)` |

Source: `src/Floss.App/Canvas/Compositing/LayerCompositor.cs:253-268`, `:978-986`

`DrawingCanvas.Render` sets `BitmapInterpolationMode.HighQuality` when `CanvasZoom < 1`, but custom draw ops bypass Avalonia's bitmap interpolation — only Skia sampling options matter.

## APIs (SkiaSharp 3.119.4 / Avalonia 12.0.2)

- `ISkiaSharpApiLease.GrContext` — GPU context during `ICustomDrawOperation.Render`
  Source: Avalonia `ISkiaSharpApiLeaseFeature.cs`
- `SKImage.ToTextureImage(GRContext context, bool mipmapped)` — upload + `glGenerateMipmap` equivalent
  Source: SkiaSharp XML `M:SkiaSharp.SKImage.ToTextureImage(SkiaSharp.GRContext,System.Boolean)`
- Sampling: `new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)` when drawing mipmapped GPU image
- Fallback (no `GrContext`, software renderer): CPU `SKImage` + `Linear` + `SKMipmapMode.None`

`SkiaOptions.MaxGpuResourceSizeBytes = 512MB` already set in `Program.cs`.

## Implementation plan

1. Pass `CanvasZoom` into `DrawTiles(..., double zoom)`.
2. Batch visible cells into one `CompositorCellsDrawOp` (one lease per frame, not one per cell).
3. In `Render`, under `CompositeGate`:
   - `zoom >= 1`: draw CPU `_cellImages[ci]` with `Nearest` / `None` (unchanged 1:1 behavior).
   - `zoom < 1` + `GrContext`: `EnsureCellGpuImage(ci, ctx)` — if `_cellGpuImageRevision[ci] != _cellRevision[ci]`, `cpu.ToTextureImage(ctx, mipmapped: true)`; draw with `Linear` / `Linear`.
   - `zoom < 1` + no GPU: CPU image with `Linear` / `None`.
4. Cache `_cellGpuImages[]` / `_cellGpuImageRevision[]` parallel to CPU images; dispose via `_delayedDispose` on revision change and `InvalidateCells`.

## Drawpile reference (display only)

Drawpile does not lower merge resolution for zoom. It uploads dirty tile rects to GL textures and optionally generates mipmaps for smooth minification.

Source: `notes/drawpile-compositor-comparison.md` lines 81, 195-196.

## Files to change

- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs` — GPU cache, `CompositorCellsDrawOp`, remove per-cell `SkiaTileDrawOp` loop
- `src/Floss.App/Canvas/DrawingCanvas.cs` — `_compositor.DrawTiles(..., zoom)`
