# Krita-Style Per-Tile Render (Replace Cell Bitmaps)

## Problem (Verified)

Current Floss pipeline: composited 64×64 tiles are `memcpy`'d into 16384×16384 cell `SKBitmap`s, then `SKImage.FromBitmap()` copies the ENTIRE cell (~1GB) because bitmaps are mutable.

Source: `SkiaSharp docs`: *"If the bitmap is marked immutable, and its pixel memory is shareable, it may be shared instead of copied."*

Since cell bitmaps are never `SetImmutable()`, every `FlushDirtyCellImages()` call triggers a 1GB pixel copy per affected cell.

## Krita's Approach (Verified)

Krita splits the projection into ~256×256 `GL_TEXTURE_2D` tiles. Only dirty sub-rects uploaded via `glTexSubImage2D`. Draw renders each tile as a 6-vertex quad. No giant bitmaps, no full-frame copies.

Sources:
- Tile: `libs/ui/opengl/kis_texture_tile.h:40-101`
- Upload: `libs/ui/opengl/kis_texture_tile.cpp:154-382`
- Draw: `libs/ui/opengl/KisOpenGLCanvasRenderer.cpp:871-985`

## Target Architecture for Floss

Replace cell bitmaps with per-tile drawing. Floss already composites layers into 64×64 scratch `SKBitmap`s stored in `_tileScratch[]`. Instead of copying those into cells:

1. After compositing a tile, mark the scratch bitmap immutable → `SKImage.FromBitmap` shares the buffer (zero copy)
2. Store `SKImage?[]` per tile alongside `_tileScratch[]`
3. At draw time: single `ICustomDrawOperation` iterates visible tiles, draws each tile's `SKImage` at `(tileX*64, tileY*64)`
4. Remove all cell infrastructure: `_cellBitmaps`, `_cellImages`, `_cellDirty`, `CopyTileToCell`, `FlushDirtyCellImages`, `EnsureCells`, `InvalidateCells`, `CopyAllTilesToCells`

## Files to Change

### `LayerCompositor.cs`
- **Remove**: `_cellBitmaps`, `_cellImages`, `_cellDirty`, `_cellCols`, `_cellRows`, `MaxCellDim`
- **Remove**: `CopyTileToCell`, `FlushDirtyCellImages`, `EnsureCells`, `InvalidateCells`, `CopyAllTilesToCells`, `SkiaTileDrawOp`
- **Add**: `_tileImages: SKImage?[]` — one `SKImage` per composited tile
- **Modify**: After compositing a scratch tile, mark bitmap immutable and create `SKImage`
- **Modify**: `DrawTiles()` → single `ICustomDrawOperation` that draws visible tiles

### `CompositorConfig.cs`
- **Remove**: `MaxCellDimension`, `DelayedDisposeFrames` (no more cell images to delay-dispose)

## Draw Strategy

Single `ICustomDrawOperation` per frame (replaces one-per-cell):

```csharp
context.Custom(new TileGridDrawOp(
    _tileImages, _tileScratch,
    _xtiles, _ytiles, CmpTileSize,
    canvasOrigin, viewportRect));
```

`TileGridDrawOp.Render()`:
- Acquires `ISkiaSharpApiLeaseFeature`
- Computes visible tile range from viewport rect
- Iterates only visible tiles
- For each visible tile with a `_tileImages[ti]`:
  - `lease.SkCanvas.DrawImage(img, destRect, Nearest)`

## Edge Cases

### Scratch bitmap reuse
Currently `_tileScratch` bitmaps are reused across frames (erased and re-composited). With immutable bitmaps, `SKImage.FromBitmap` shares the buffer. But then `Erase` would modify a buffer shared with an `SKImage`.

**Fix**: Double-buffer or create new scratch bitmap when compositing a dirty tile. Since scratch bitmaps are 64×64 (16KB), allocation is cheap. Dispose old bitmap + SKImage, create new one, composite, SetImmutable, FromBitmap.

### Undrawn tiles
Tiles outside the viewport don't need `SKImage`s. Only create images when compositing a tile that's visible (or will become visible). Tiles scrolled off-screen can keep their images or defer disposal.

### Canvas resize
When canvas size changes, `SetSize()` currently calls `InvalidateCells()`. Instead, resize `_tileScratch`/`_tileImages` arrays, dispose old images, create new ones.
