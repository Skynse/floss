# Smart shape preview — performance model (from Ctrl+T)

## Why Ctrl+T is fast (`SelectionTransformOperation`)

| Technique | Detail |
|-----------|--------|
| **No layer writes during drag** | Pixels lifted once into `_combinedPixels` float buffer; layer cleared |
| **Overlay = `WriteableBitmap`** | `RenderOverlay` only calls `dc.DrawImage` + bbox handles |
| **No compositor churn** | No `NotifyChanged` / stroke-suspend during interactive edit |
| **Single commit** | `CommitCurrent()` stamps pixels back once |

## What made smart-shape preview slow (wrong approach)

- Restore all captured tiles → full `BrushEngine.RasterizeSegments` on **live layer** every pointer move
- `NotifyStrokeSuspendBegin` + `NotifyChanged` every frame → background compositor + LOD artifacts
- Write-lock leak when `LiveStroke` skipped `ExitPixelWriteLock`

## Correct smart-shape preview (this implementation)

`SmartShapeStrokePreview` mirrors Ctrl+T:

1. Rasterize fitted shape into a **scratch `DrawingLayer`** (never the document layer)
2. Copy scratch → `byte[]` → `WriteableBitmap` when shape/brush changes (throttled 16ms)
3. `RenderOverlay` draws bitmap at document rect + gizmo vector handles
4. **Commit only** via `SmartShapeBrushOutput.CommitShape` on OK/outside-bbox

## Files

- `SmartShape/SmartShapeStrokePreview.cs` — offscreen raster + bitmap
- `Processes/Output/SmartShapeBrushOutput.cs` — owns preview, `UpdatePreview` / `DrawPreview`
- `Processes/Input/SmartShapeBrushInputProcess.cs` — restored gizmo state machine
- `SmartShape/SmartShapeOverlay.cs` — handles/bbox only during gizmo (stroke = bitmap)
