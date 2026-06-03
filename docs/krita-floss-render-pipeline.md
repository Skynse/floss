# Krita vs Floss Render Pipeline (Verified)

## Krita Canvas Render Pipeline

Source: `/home/neckles/projects/krita/`

### Layer Compositing (CPU)
- All layers composited on CPU into a single projection (`KisImage::projection()`)
- `KoCompositeOp` applies blend modes, SIMD-accelerated
- Dirty rect: `KisImage::sigImageUpdated(QRect)`

### Tile → GPU Upload
- Projection split into fixed-size tiles (typically 256×256px)
- Each tile = one `KisTextureTile` wrapping `GL_TEXTURE_2D`
  Source: `libs/ui/opengl/kis_texture_tile.h:40-101`
- Only dirty sub-rects uploaded: `glTexSubImage2D` for partial, `glTexImage2D` for full
  Source: `libs/ui/opengl/kis_texture_tile.cpp:154-382`
- Pixel data read from projection device, color-converted on CPU
  Source: `libs/ui/opengl/KisOpenGLUpdateInfoBuilder.cpp:47-165`
- Dirty rects deduplicated by `KisCanvasUpdatesCompressor`
  Source: `libs/ui/canvas/kis_canvas_updates_compressor.cpp:9-39`
- NO GPU layer blending. CPU composites layers into projection, then tiles are uploaded.

### GPU Draw
- `paintGL()` → `renderCanvasGL()` → `drawImageTiles()`
  Source: `libs/ui/opengl/kis_opengl_canvas2.cpp:235-283`
- Each tile drawn as 6-vertex quad (`glDrawArrays(GL_TRIANGLES, 0, 6)`)
  Source: `libs/ui/opengl/KisOpenGLCanvasRenderer.cpp:871-985`
- Vertex/texcoord buffers pre-uploaded once
  Source: `libs/ui/opengl/kis_opengl_image_textures.cpp:231-254`

## Floss Canvas Render Pipeline

Source: `/home/neckles/projects/floss/src/Floss.App/`

### Layer Compositing (CPU)
- `LayerCompositor.CompositeCore()` composites layers into 64×64 scratch `SKBitmap` tiles
  Source: `Canvas/Compositing/LayerCompositor.cs:366-515`
- Per-pixel blend math in `LayerCompositorPixelOps.cs` (SIMD for Normal blend)
- Dirty tiles queued, budget-limited: 32 dirty + 96 missing per frame
  Source: `Canvas/Compositing/LayerCompositor.cs:37-38`
- Tile selection: picks closest to viewport center by Manhattan distance
  Source: `Canvas/Compositing/LayerCompositor.cs:599-663`

### Tile → Cell Bitmap
- Each 64×64 composited tile `memcpy`'d row-by-row into appropriate sub-rect of 16384×16384 cell `SKBitmap`
  Source: `Canvas/Compositing/LayerCompositor.cs:278-297` (`CopyTileToCell`)
- Cell bitmaps are mutable, created in `EnsureCells()`
  Source: `Canvas/Compositing/LayerCompositor.cs:244-276`
- `MaxCellDim = 16384` (4096×4). A single 16384×16384 cell bitmap is ~1GB (16384×16384×4 bytes).
  Source: `Canvas/Compositing/LayerCompositor.cs:41`

### SKImage.FromBitmap → PIXEL DATA COPY
- **Verified**: `SKImage.FromBitmap()` copies pixel data when bitmap is NOT immutable
  Source: SkiaSharp XML docs at NuGet cache `skiasharp/3.119.4-preview.1.1/lib`
  > "If the bitmap is marked immutable, and its pixel memory is shareable, it may be shared instead of copied."
- **Verified**: Cell bitmaps are NEVER marked immutable
  - `SetImmutable` / `SetIsImmutable` / `.IsImmutable` — zero matches in entire codebase
- **Result**: Every `FlushDirtyCellImages()` call copies full cell bitmap (~1GB per 16384×16384 cell)
  Source: `Canvas/Compositing/LayerCompositor.cs:309-320`
- Old `SKImage` disposed with 8-frame delay (`DelayedDisposeFrames = 8`)
  Source: `Canvas/Compositing/CompositorConfig.cs:28`

### Draw
- `DrawingCanvas.Render()` calls `compositor.DrawTiles()`
  Source: `Canvas/DrawingCanvas.cs:1394`
- Each cell drawn as `SkiaTileDrawOp` → `lease.SkCanvas.DrawImage(SKImage, Nearest)`
  Source: `Canvas/Compositing/LayerCompositor.cs:810-837`
- Viewport culling skips off-screen cells

### GPU Context
- **Verified**: Zero `GRContext`, `GrDirectContext`, `OpenGL`, `Vulkan` anywhere in Floss
- Avalonia auto-selects renderer via `UsePlatformDetect()` (Avalonia.Desktop 12.0.2)
  Source: `Floss.App/Program.cs:100`
- `SkiaOptions { MaxGpuResourceSizeBytes = 512 * 1024 * 1024 }` is set but no GPU context created
  Source: `Floss.App/Program.cs:100`

### Invalidation
- `ProjectionUpdateScheduler` coalesces dirty rects, posts `InvalidateVisual()` at `DispatcherPriority.Render`
  Source: `Canvas/Compositing/ProjectionUpdateScheduler.cs:30-70`
- 1ms throttle on preview notifies during active stroke
  Source: `Processes/Output/DirectDrawOutput.cs:336`
- Only one `InvalidateVisual` post in-flight at a time (gated by `_queued`)
  Source: `Canvas/Compositing/ProjectionUpdateScheduler.cs:18`

## Performance Bottleneck (Verified)

| Step | Cost | Evidence |
|------|------|----------|
| `SKImage.FromBitmap()` per dirty cell | ~1GB pixel copy | Bitmap is mutable (not immutable), SkiaSharp docs confirm copy behavior |
| 32 tile/frame budget | Large strokes spread across N frames | `DirtyTileBudget = 32`, line 37 |
| Layer compositing per tile | CPU blend of all layers for each 64×64 tile | `CompositeCore()` line 366+ |
| `CopyTileToCell` | Row-by-row `Buffer.MemoryCopy` of 64×64 into cell | Line 278 |

## Open Questions

- Q1: Is `SKImage.FromBitmap()` callable with immutable bitmaps? If cell bitmaps are made immutable after `CopyTileToCell`, would `SKImage.FromBitmap` share instead of copy?
  - Note: making bitmap immutable means it can't be modified. Would need a new copy-then-immutable cycle.
- Q2: Could cell bitmaps be replaced with individual per-tile SKImages, uploaded directly (avoiding the cell bitmap entirely)?
- Q3: Is the 32 tile/frame budget appropriate for typical brush strokes?
