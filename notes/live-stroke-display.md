# Live stroke display (compositor path)

Research: `docs/krita-floss-render-pipeline.md`, `notes/large-canvas-stroke-perf.md`, `DrawingDocument` stroke-suspend API.

## Correct model (Krita / Floss compositor)

1. Brush writes **document layer tiles** (`TiledPixelBuffer`, `LiveStroke` skips write lock).
2. `NotifyChanged(dirtyRegion)` marks **compositor tile diff** dirty (64×64), coalesced by `ProjectionUpdateScheduler`.
3. During stroke: `BeginStrokeSuspend` → `_strokeBelowCache` merges layers **below** paint layer once per tile.
4. `CompositeCore` stroke path re-merges **only paint layer + above** onto below cache → `CopyTileToCell` → `DrawTiles`.
5. Display is **cell bitmaps** — seamless, correct clip/blend/opacity. No separate overlay bitmap.

Drawpile equivalent: document diff marks tiles; flatten job merges stack for changed tiles only (`notes/drawpile-compositor-comparison.md`).

## What was wrong (overlay hack)

Skipping `NotifyChanged` and drawing a `WriteableBitmap` / per-tile `SKImage` overlay bypasses the compositor — fast but wrong (tile seams, no layers-above, not the document pipeline).

## Performance without overlay

| Technique | Source |
|-----------|--------|
| Stroke-below cache | `LayerCompositor` `_strokeBelowCache` |
| `StrokeSuspendTileBudget = 256` | `LayerCompositor.cs` |
| Coalesced dirty rects | `ProjectionUpdateScheduler` |
| Sync composite after pointer batch | `ToolContext.FlushStrokeCompositorPreview` — drain pending stroke tiles on UI thread before return (no 1-frame bg lag) |
| Idle skip `ScheduleBackgroundComposite` | Only when `PendingDirtyTileCount == 0 && PendingCount == 0` |

## Key files

- `DirectDrawOutput.FlushPreviewDirty` — `NotifyChanged` + `FlushStrokeCompositorPreview`
- `DrawingCanvas.FlushStrokeCompositorPreview` — `ApplyPending` + sync `Composite` loop
- `LayerCompositor.CompositeCore` — stroke split + below cache

## Snippet

```csharp
// FlushPreviewDirty
tx.Ctx.Document.NotifyStrokeSuspendExtend(tx.PendingPreviewDirty);
tx.Ctx.Document.NotifyChanged(tx.PendingPreviewDirty, tx.LayerIndex);
tx.Ctx.FlushStrokeCompositorPreview?.Invoke();
```
