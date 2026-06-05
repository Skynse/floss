# Drawpile compositor comparison (for Floss big-file editing)

Research date: 2026-06-04. Source: `../Drawpile` checkout.

## Goal

Understand how Drawpile stays fast on large canvases during **editing and navigation** (not import), and what Floss should adopt.

---

## Drawpile architecture (relevant paths)

| Piece | Path | Role |
|-------|------|------|
| Tile size | `src/drawdance/libengine/dpengine/pixels.h` | `DP_TILE_SIZE = 64`, `DP_TILE_LENGTH = 4096` |
| Layer pixels | `src/drawdance/libengine/dpengine/layer_content.c` | Layers store sparse `DP_Tile[]` (64×64) |
| Tile diff | `src/drawdance/libengine/dpengine/canvas_diff.c` | `bool *tile_changes` — which composite tiles need re-flatten |
| Flatten job | `src/drawdance/libengine/dpengine/renderer.c` | `handle_tile_job` → `DP_canvas_state_flatten_tile_to` merges layer stack for **one** tile |
| Render queue | `renderer.c` | Worker threads; `queue_high` (in-view) + `queue_low` (outside view) |
| Paint engine API | `src/drawdance/libengine/dpengine/paint_engine.c` | `DP_paint_engine_tick`, `render_continuous`, `change_bounds`, `render_everything` |
| Display cache | `src/libclient/canvas/tilecache.cpp` | `GlCanvasImpl`: flat `DP_Pixel8` buffer; per-tile dirty bits |
| GL upload | `src/desktop/view/glcanvas.cpp` | `glTexSubImage2D` only for **dirty** tiles intersecting `canvasViewTileArea` |
| Viewport → engine | `src/libclient/canvas/paintengine.cpp` | `setCanvasViewTileArea` → `DP_paint_engine_change_bounds` |
| View rect | `src/libclient/view/canvascontrollerbase.cpp` | `emitViewRectChanged` computes tile-area from visible canvas rect |

---

## Drawpile pipeline (one sentence)

**Document mutation marks tile diffs → background workers flatten only changed tiles → flat tile cache → GPU uploads only dirty visible tiles; pan/zoom is a shader transform over existing textures.**

### 1. Composite (CPU): flatten one 64×64 tile

```124:155:../Drawpile/src/drawdance/libengine/dpengine/renderer.c
static void handle_tile_job(DP_Renderer *renderer, DP_RenderContext *rc,
                            DP_RendererTileJob *job)
{
    DP_TransientTile *tt = rc->tt;
    DP_CanvasState *cs = job->cs;
    // ... background tile ...
    DP_canvas_state_flatten_tile_to(cs, job->tile_index, tt, true,
                                    &renderer->selection_color, &vmf);
    // ... optional checker merge ...
    DP_pixels15_to_8_tile(pixel_buffer, DP_transient_tile_pixels(tt));
    renderer->fn.tile(renderer->fn.user, job->tile_x, job->tile_y, pixel_buffer);
}
```

- Same conceptual work as Floss `LayerCompositor.CompositeCore` per tile.
- Difference: **only tiles in `DP_CanvasDiff`** are queued, not “every tile in viewport that lacks scratch”.

### 2. Scheduling: viewport priority, no fixed 32-tile frame cap

`DP_renderer_apply` (`renderer.c` ~686–801):

- Tiles inside `view_tile_bounds` → `queue_high`.
- Tiles outside view → `queue_low` (background fill).
- `DP_RENDERER_VIEW_BOUNDS_CHANGED` (pan): **reprioritizes** already-queued low tiles to high; may block briefly so new view doesn’t “stumble” incomplete tiles.
- `DP_RENDERER_CONTINUOUS`: keeps workers draining the queue without the view-change unlock dance.
- Multiple worker threads (`DP_renderer_new(thread_count, ...)`); each has its own `DP_TransientTile` scratch.

**No `DirtyTileBudget = 32`** in the display path. Workers run until the queue for the current diff is empty.

### 3. Display cache: flat buffer + dirty flags

`TileCache::GlCanvasImpl` (`tilecache.cpp`):

- One contiguous `DP_Pixel8[m_pixels]` sized `tileTotal * DP_TILE_LENGTH`.
- `render(tileX, tileY, src)` → `memcpy` into slot; sets `m_dirtyTiles[i]`.
- `eachDirtyTileReset(tileArea, fn)` → callback only for dirty tiles in area, clears dirty bit.

`PixmapGrid` splits huge canvases into ≤32000px cells (same motivation as Floss `MaxCellDim = 16384`).

### 4. GPU path: sub-rect upload, clean pan draw

`glcanvas.cpp`:

- **`renderCanvasDirty`**: `eachDirtyTileReset(visibleTileRect, glTexSubImage2D)` — upload changed pixels only.
- **`renderCanvasClean`**: bind existing textures + draw (no upload, no re-flatten).
- Pan sets `dirty.texture` but upload loop only touches tiles still marked dirty in `TileCache`; unchanged tiles are no-ops.
- Optional **mipmaps** (`glGenerateMipmap`) for smooth zoom — display-side LOD, not a lower-res composite pass.

### 5. Viewport wiring

```2613:2627:../Drawpile/src/libclient/view/canvascontrollerbase.cpp
void CanvasControllerBase::emitViewRectChanged()
{
    // ...
    m_canvasViewTileArea = QRect(
        QPoint(area.left() / DP_TILE_SIZE, area.top() / DP_TILE_SIZE),
        QPoint(area.right() / DP_TILE_SIZE, area.bottom() / DP_TILE_SIZE));
    m_canvasModel->paintEngine()->setCanvasViewTileArea(m_canvasViewTileArea);
}
```

```878:887:../Drawpile/src/libclient/canvas/paintengine.cpp
void PaintEngine::setCanvasViewTileArea(const QRect &canvasViewTileArea)
{
    m_canvasViewTileArea = canvasViewTileArea;
    DP_paint_engine_change_bounds(..., toDpRect(m_canvasViewTileArea), ...);
    DP_SEMAPHORE_MUST_WAIT(m_viewSem);
    emit tileCacheDirtyCheckNeeded();
}
```

Pan notifies the engine to prioritize tiles for the **new** view; it does **not** invalidate the whole display cache.

---

## Floss architecture (current)

| Piece | Path | Notes |
|-------|------|-------|
| Compositor | `src/Floss.App/Canvas/Compositing/LayerCompositor.cs` | Drawpile-inspired comments; 64×64 `CmpTileSize`, 16384 cells |
| UI render | `src/Floss.App/Canvas/DrawingCanvas.cs` | `Render` → `DrawTiles`; `BackgroundCompositePass` on thread pool |
| Display cells | `LayerCompositor` | `SKBitmap` per cell; `SKImage` snapshot on revision change |
| Scratch cache | `_tileScratch[]` | Per-tile merged result; `TrimCompositeCache` evicts outside viewport |
| Budgets | `DirtyTileBudget=32`, `StrokeSuspendTileBudget=256`, `MaxMissingTilesPerFrame=96` | Caps work per `Composite()` call |
| LOD | `SelectLod` → always `0` | No display LOD (tests enforce) |
| Stroke optimization | `_strokeBelowCache` | Layers-below cache **only** during `BeginStrokeSuspend` |

### Floss `CompositeCore` hot loop (simplified)

1. Merge `_dirtyRegion` into `_pendingComposite`.
2. If viewport has tiles with `_tileScratch[idx] == null`, add to `missing`.
3. `SelectPendingTiles` with **budget 32** (256 during stroke).
4. For each tile: full sibling-stack merge → `CopyTileToCell` → `EnsureCellDisplayImage` on revision change.
5. `TrimCompositeCache`: if scratch count > 8192, evict farthest from viewport center (except protected viewport tiles).

### Where big-file pain comes from (profiles + code)

| Symptom | Floss cause | Drawpile avoids by |
|---------|-------------|-------------------|
| 3–5 GB RSS, compositor ~2% inclusive | Full layer-stack merge per tile; many tiles; SKBitmap/SKImage per cell | Same merge cost, but **fewer merges** (diff-only) + GPU resident cache |
| 28× >200 MB RSS spikes on edit | Cell `SKImage` snapshots; cache churn | `memcpy` into flat buffer; GPU upload sub-rects |
| Slow pan on huge docs | `TrimCompositeCache` drops scratch → `missing` list refills viewport → 32 tiles/frame catch-up | Display cache retained; pan = texture draw + background priority fill |
| Multi-frame “shimmer” | `DirtyTileBudget` + `InvalidateVisual` loop in `BackgroundCompositePass` | Continuous worker queue; view-bounds reprioritize |

Drawing path is already fast (separate issue, fixed). **Remaining gap is display/compositor on large layer stacks and navigation.**

---

## Side-by-side

| Concern | Drawpile | Floss today |
|---------|----------|-------------|
| Tile size | 64×64 | 64×64 (`CmpTileSize`) |
| Layer storage | Tiled `DP_Tile` | Tiled document tiles |
| Merge granularity | Per tile, per diff | Per tile, per dirty + missing viewport |
| What triggers merge | `DP_CanvasDiff` tile flags | Any layer dirty rect + viewport holes after trim |
| Merge parallelism | N worker threads, unbounded queue drain | `RenderThreadPool` + per-call tile budget |
| Merged result cache | `TileCache` (full canvas, flat) | `_tileScratch` (trimmed to ~8192, viewport-only survival) |
| Display | OpenGL textures, `glTexSubImage2D` | Avalonia `DrawTiles` / `SKImage` cells |
| Pan/zoom cost | Shader transform + filter update; optional mipmap | `SkiaTileDrawOp.Equals` → false → repaint; may trigger re-merge for missing scratch |
| Zoom quality | GPU mipmaps (optional) | `SelectLod` stub; interpolation in `Render` |
| Stroke fast path | Incremental diff on painted tiles only | `_strokeBelowCache` during stroke suspend only |
| Navigator | Separate dirty navigator tile list | Not comparable here |

---

## What to adopt (ordered by impact / fit)

### Tier A — Match Drawpile semantics on CPU (no GPU rewrite)

1. **Persistent display tile cache (don’t evict on pan)**  
   - Stop treating “outside viewport” scratch tiles as disposable during navigation.  
   - Drawpile keeps the full flattened cache; only **content diffs** drive recomposite.  
   - Floss: split “display cache” (cells/`_tileScratch` committed to cells) from “pending merge work”; trim policy should LRU by **memory**, not “outside current viewport”.

2. **Tile-level document diff for composite**  
   - When a layer tile changes, mark composite tile indices dirty only for that region (Drawpile `DP_layer_content_diff_mark` → `DP_CanvasDiff`).  
   - Do **not** add viewport-missing tiles to the same queue as content dirties unless explicitly prefetching.

3. **Separate navigation prefetch budget**  
   - Drawpile: high-priority queue for `view_tile_bounds`, continuous workers.  
   - Floss: keep a small budget for **content** dirty (32 ok for fairness), but allow **unbounded or high cap** for viewport `missing` tiles during pan/zoom (e.g. 512–2048/frame or dedicated navigation mode).

4. **Generalize stroke-below-cache**  
   - Drawpile effectively never re-merges unchanged tiles.  
   - Floss `_strokeBelowCache` is the right idea; extend to “cached merge below active edit layer” or “cached full flatten per tile until layer stack fingerprint changes”.

### Tier B — Display path (medium effort)

5. **Sub-rect upload / revision-based display refresh**  
   - Already partially done (`EnsureCellDisplayImage`). Extend to per-tile dirty in cells, not whole-cell snapshot when one 64×64 tile changed.

6. **Calm `BackgroundCompositePass` invalidate loop**  
   - Only `InvalidateVisual` when compositor actually produced new cell revisions (Drawpile: `tileCacheDirtyCheckNeeded` only when cache reports dirty).

### Tier C — Structural (Drawpile parity)

7. **GPU canvas** (Drawpile `GlCanvas`)  
   - Flat tile buffer → textures → pan as matrix. Largest win for zoom/pan smoothness; aligns with existing Drawpile-inspired cell layout.

8. **Display mipmaps** instead of composite LOD  
   - Drawpile uses mipmaps for zoom-out smoothness without maintaining separate merged LOD bitmaps. Fits if Floss stays on GPU for presentation.

---

## Recommended decision

**Short term (best ROI): Tier A items 1–3.**  
They mirror Drawpile’s core trick: **the merged canvas is a cache; pan reads the cache; only document edits invalidate tiles.** Floss already has the pieces (`_tileScratch`, cells, stroke cache) but undermines them with viewport eviction and 32-tile navigation catch-up.

**Medium term:** Tier A.4 + B.5–6.

**Long term:** Tier C if Avalonia/Skia presentation remains the bottleneck after cache semantics are fixed.

Do **not** start with composite LOD (`SelectLod`) — Drawpile doesn’t lower merge resolution; it uses full-res tiles + GPU filtering/mipmaps.

---

## Key snippets to re-use when implementing

### Drawpile: dirty-only GPU upload in view

```720:748:../Drawpile/src/desktop/view/glcanvas.cpp
void drawCanvasDirtyTexture(..., canvas::TileCache &tileCache, ...)
{
    QRect visibleTileRect = tileRect.intersected(controller->canvasViewTileArea());
    tileCache.eachDirtyTileReset(visibleTileRect, [&](const QRect &pixelRect, const void *pixels) {
        f->glTexSubImage2D(GL_TEXTURE_2D, 0, ... pixels);
    });
    drawCanvasShader(f, rect, inOutFilter);
}
```

### Drawpile: clean draw path (no content change)

```895:920:../Drawpile/src/desktop/view/glcanvas.cpp
void renderCanvasClean(...) {
    for (int i = 0; i < textureCount; ++i) {
        f->glBindTexture(GL_TEXTURE_2D, canvasTextures[i]);
        drawCanvasShader(f, canvasRects[i], canvasFilters[i]);
    }
}
```

### Floss: viewport eviction (problematic for pan)

```553:569:src/Floss.App/Canvas/Compositing/LayerCompositor.cs
private void TrimCompositeCache(PixelRegion? vp)
{
    if (n <= MaxCompositeCacheTiles) return;
    // ... evict farthest from viewport center unless in prot ...
}
```

### Floss: per-frame tile budget

```410:413:src/Floss.App/Canvas/Compositing/LayerCompositor.cs
var tileBudget = _strokeSuspendDepth > 0 ? StrokeSuspendTileBudget : DirtyTileBudget;
var pending = SelectPendingTiles(vpClip, missing,
    unbounded ? int.MaxValue : tileBudget, ...);
```

---

## Open questions for product

1. **Memory ceiling**: Drawpile holds a full flattened tile cache in RAM (and GPU). On a 20k×20k doc that’s large but bounded and predictable. Is Floss willing to retain merged tiles for the whole canvas (compressed?) vs viewport-only scratch?
2. **GPU canvas**: Avalonia custom SKIA GL vs future path — affects Tier C timing.
3. **Prefetch**: Should tiles outside view be filled at low priority (Drawpile `queue_low`) for faster pan, or only on demand?
