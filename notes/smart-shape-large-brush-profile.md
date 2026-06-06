# Smart shape + large brush freeze (live profile)

**Trace:** `bench/traces/live-floss.nettrace` / `live-floss.speedscope.json`  
**Process:** `/home/neckles/projects/floss/src/Floss.App/bin/Debug/net10.0/Floss` (PID 1387073)  
**Duration:** 15s `dotnet trace collect --process-id … --profile dotnet-sampled-thread-time`

## Symptom

Smart-shape adjust/gizmo with a large brush freezes the app (UI stops responding during pointer move).

## Top exclusive time (Floss, ~15s window)

| ms (self) | Frame |
|----------:|-------|
| 11492 | `SmartShapeBrushOutput.Preview` |
| 10427 | `SmartShapeStrokePreview.Update` |
| 11278 | `BrushEngine.RasterizeSegments(/Core)` |
| 11267 | `BrushEngine.RenderCurrentStamps` |
| 10215 | `BrushEngine.TryRasterizeCachedDabsTileMajor` |
| 9811 | `BrushEngine.ApplyCachedDabsLightenToTile` |
| 7603 | `CachedDab.get_IsScaled` (hot loop — scaled large dab slow path) |
| 4029 | `SmartShapeBrushInputProcess.UpdateAdjustment` |
| 4033 | `SmartShapeBrushInputProcess.PointerMove` |

## Call chain (inclusive)

```
Workspace_OnPointerMoved
  → SmartShapeBrushInputProcess.PointerMove / UpdateAdjustment
  → CompositeTool.InvalidateSmartShapeUi
  → SmartShapeBrushOutput.Preview
  → SmartShapeStrokePreview.Update          ← synchronous on UI thread
      → BrushEngine.RasterizeSegments       ← full stroke, large-brush cached tile path
      → TiledPixelBuffer.Capture + WriteableBitmap + Marshal.Copy
```

## Root cause

Every pointer move during **Adjusting** re-rasterizes the entire fitted shape with the active brush **on the UI thread** inside `SmartShapeStrokePreview.Update`. Large brushes hit `TryRasterizeCachedDabsTileMajor` / `ApplyCachedDabsLightenToTile` (see `notes/large-stamp-cached-raster.md`). ~10s of sampled time in one 15s trace is this path.

Bitmap alloc/copy is minor (~80ms) vs raster (~10s+).

## Fix direction

Move smart-shape stroke preview raster off the UI thread (coalesce pending updates, apply latest bitmap on `Dispatcher.UIThread`), matching the async slice model used by `DirectDrawOutput`.

## Files

- `src/Floss.App/SmartShape/SmartShapeStrokePreview.cs` — preview raster
- `src/Floss.App/Processes/Output/SmartShapeBrushOutput.cs` — calls `Update` from `Preview`
- `src/Floss.App/Processes/Input/SmartShapeBrushInputProcess.cs` — `UpdateAdjustment` → `InvalidateUi`
