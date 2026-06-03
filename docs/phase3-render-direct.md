# Phase 3: Eliminate Scratch Buffer Double-Write

## Current Pipeline (what we're fixing)

```
RenderStampsViaScratch (Skia path):
  Allocate SKBitmap _scratch (region-sized, up to 4096×4096)
  SKCanvas clear + RenderPreparedStamps → per-stamp Skia DrawBitmap → scratch
  Grain loop (if grain > 0) → reads/modifies scratch
  CompositeScratchBgraOntoLayer → reads scratch pixel-by-pixel, writes to tiles
                                  via WriteCompositeStamp → CompositeBrushPixel

RenderColorMixStampsViaScratch (CPU color-mix path):
  Allocate SKBitmap _scratch (region-sized)
  Zero-initialize scratch
  Per-stamp pixel loop → writes premultiplied RGBA to scratch
  CompositeScratchBgraOntoLayer → same as above
```

**Problem**: Every painted pixel is written twice:
1. First to the scratch `SKBitmap` (either via Skia canvas or manual pixel write)
2. Then from scratch to tiles via `CompositeScratchBgraOntoLayer`'s per-pixel loop

This is roughly 2×(region_width×region_height×4) bytes read/written per batch.

## Krita's Approach (reference)

Krita does NOT use a region-sized scratch buffer. Instead:

1. Each dab is generated into a small `KisFixedPaintDevice` (dab-sized, ~32×32 to 256×256)
2. Color is pre-applied to the mask (`fillGrayBrushWithColor`) — produces premultiplied RGBA
3. `postProcessDab()` applies texture/sharpness to the same dab device
4. Dabs are split into non-overlapping regions by `splitDabsIntoRects()`
5. For each region, dabs intersecting that region are composited directly onto tiles:
   - `dstIt->rawData()` points to tile memory (destination)
   - `dab.device->constData()` points to dab memory (source)
   - `colorSpace->bitBlt()` composites source onto destination in one pass

**Key insight**: The dab buffer IS both the rendered stamp AND the compositing source. No intermediate buffer between stamp rendering and tile compositing.

## Target Pipeline

```
Unified render pipeline:
  For each stamp in batch:
    1. Render stamp to a stamp-sized byte[] buffer (Bgra8888 premultiplied)
       - Skia path: small per-stamp SKBitmap + SKCanvas.DrawBitmap
       - CPU path: manual pixel iteration (already exists in ColorMix path)
    2. Apply grain/soften to the stamp buffer (same as before, but stamp-sized)
    3. Composite stamp buffer directly onto tiles:
       - Find tiles intersecting stamp bounds
       - For each tile, blend stamp pixels onto tile bytes
       - Uses existing SIMD ops (StampSrcOver, StampSrcOverMaskedRow)
```

### Benefits
- Eliminates region-sized scratch allocation (was up to 4096×4096×4 = 64MB)
- Eliminates per-pixel read/write pass over entire region
- Stamp-sized buffers (typically 32×32 to 256×256) stay in L1/L2 cache
- Tile data stays in cache between stamps (no eviction by huge scratch)
- Each pixel written exactly once (to tile memory)

## Implementation Steps

### Step 1: Per-stamp scratch buffer helper

Add fields to BrushEngine:
```csharp
private byte[]? _stampBuffer;    // pre-allocated, grows as needed
private SKBitmap? _stampScratch; // tiny, stamp-sized
```

Helper method:
```csharp
// Renders one stamp mask + color to a stamp-sized BGRA buffer.
// Returns (buffer, width, height, stride).
private (byte* ptr, int w, int h, int stride) RenderStampToBuffer(
    ActiveStroke stroke, Stamp stamp, int stampIndex)
```

For Skia stamps: create `_stampScratch` at stamp size, render via `SKCanvas.DrawBitmap`, pin the pixels.
For CPU stamps: write premultiplied RGBA directly to `_stampBuffer`.

### Step 2: Composite stamp buffer directly onto tiles

New method:
```csharp
// Composites a stamp-sized BGRA buffer onto tiles.
// Only touches tiles intersecting the stamp bounds.
private unsafe void CompositeStampOntoLayer(
    TiledPixelBuffer pixels, byte* stampPtr, int stampW, int stampH, int stampStride,
    PixelRegion stampBounds, bool alphaLocked, BlendStampFunc blend)
```

This replaces the region-wide `CompositeScratchBgraOntoLayer`. Instead of iterating all pixels in the dirty region, it only iterates the stamp's bounding box. Tile lookup is per-stamp, but since stamps are typically clustered, the same tile will be accessed repeatedly (cache-friendly).

### Step 3: Unify render paths

- `RenderStampsViaScratch`: replace with per-stamp loop calling Step 1 + Step 2
- `RenderColorMixStampsViaScratch`: change target from scratch to `_stampBuffer`, then composite via Step 2
- Remove `_scratch`, `CompositeScratchBgraOntoLayer`, the grain loop over scratch

### Step 4: Remove scratch-related code

Delete:
- `_scratch` field
- `CompositeScratchBgraOntoLayer` method
- `WriteCompositeStamp` method (replaced by direct SIMD ops in composite path)
- Scratch allocation/guard code in `RenderCurrentStamps`

## Edge Cases

### Grain
Currently grain is applied to the scratch after all stamps are rendered (post-composite into the scratch). In the per-stamp model, grain must be applied to each stamp buffer before compositing. Since grain modulates alpha per-pixel, this is equivalent — just done per-stamp instead of on the accumulated result.

### Overlapping stamps
When stamps overlap, the current scratch approach automatically blends them via Skia's canvas compositing. In the per-stamp model, each stamp is composited onto tiles independently. The final result is identical because compositing is order-independent for opaque operations and order-preserving for SrcOver — we composite stamps in the same order they would have been drawn.

### Alpha lock
Currently `CompositeScratchBgraOntoLayer` passes `alphaLocked` to `WriteCompositeStamp`. The per-stamp composite method must do the same — skip pixels where the destination is transparent when alpha lock is enabled. This is already handled by `CompositeBrushPixel` → `ApplySrcOver(alphaLocked: true)`.

### Blend modes
The scratch path is only used when `blendMode == SrcOver` (see `useScratch` guard). Non-SrcOver brushes go through `RenderWithSkiaOnLayer` which creates per-tile Skia canvases. The per-stamp path should also only apply to SrcOver. The `RenderWithSkiaOnLayer` fallback remains for other blend modes.

### Large stamps (> 4096×4096 guard)
Currently the guard prevents scratch allocation for regions > 4096×4096. In the per-stamp model, this guard is irrelevant — each stamp buffer is stamp-sized (max brush size, typically ≤ 512×512). No guard needed.

## Expected Performance Impact

| Metric | Before | After |
|--------|--------|-------|
| Scratch allocation per batch | region-sized (up to 64MB) | per-stamp (up to 1MB for 512×512 stamp) |
| Pixel writes per batch | 2× (scratch + tiles) | 1× (tiles only) |
| Pixel reads per batch | 1× (scratch read) | 0 additional reads |
| Tile cache pressure | Scratch evicts tiles from cache | Small stamp buffers fit in L2 |
| Grain pass | Iterate entire scratch | Iterate stamp-sized buffer per stamp |

For a 1000×1000 region with 50 stamps of 64×64 each:
- Before: 4MB scratch allocated, ~8MB pixel read/write (2 passes)
- After: 16KB per-stamp buffer × 50 = 800KB total, ~4MB pixel writes (1 pass to tiles)
- Tile data stays in cache between stamps (40KB per tile, 10–15 tiles touched)

## Risk Assessment

- **Identical visual output**: Each stamp composited individually produces identical result to batch compositing via scratch (SrcOver is associative for premultiplied alpha)
- **Grain ordering**: Currently grain is applied to accumulated result; per-stamp grain is mathematically equivalent (grain is a per-pixel alpha multiplier, distributes over SrcOver accumulation)
- **Regression test coverage**: 383 passing tests; any visual difference would be caught by pixel-comparison tests (e.g., `DirectDraw_KeepsLongBrushSegmentsIntact`)
