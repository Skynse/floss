# Live profile (2026-06-05)

Capture: `dotnet-trace collect -p <Floss PID> --profile dotnet-sampled-thread-time --format speedscope --duration 00:00:20`

| Run | File | Drawing? |
|-----|------|----------|
| 1 | `tmp/profile/live-20260605-132157.speedscope.speedscope.json` | Light |
| 2 | `tmp/profile/live-20260605-132450.speedscope.speedscope.json` | Light (~167ms `RasterizeSegments`) |

Open in [speedscope.app](https://www.speedscope.app/)

Build: Debug (`src/Floss.App/bin/Debug/net10.0/Floss`)

## Summary

20s wall clock, **42.3s aggregate CPU** across 24 threads. Workload during capture was **light drawing** (not sustained large-brush stress).

| Area | Inclusive CPU | Notes |
|------|---------------|-------|
| `RenderThreadPool.RunLoop` | 1.8s (4.3%) | Mostly **blocked** on `BlockingCollection.TryTake` / `SemaphoreSlim.Wait` |
| UI thread (978012) | 15.1s total inclusive | Avalonia dispatcher + input routing |
| `Workspace_OnPointerMoved` → draw pipeline | ~427ms | Pointer input on UI thread |
| `DirectDrawOutput` chain | ~247ms | Preview + segment batching |
| `BrushEngine.RasterizeSegments` | ~167ms | Engine raster (small slice of window) |
| `TryAccumulateCachedDabsToStrokeMask` | ~107ms | Stroke-mask lighten path |
| `SampleMaskAlpha` | ~67ms | Mask sampling (low in this capture) |
| `LayerCompositor` | <50ms | Not dominant in this session |

## UI thread draw stack (Thread 978012)

```
Workspace_OnPointerMoved
  → CanvasInputRouter → DrawingCanvas → ToolController
  → CompositeTool.PointerMove
  → DirectDrawOutput.Preview / ProcessQueuedSync
  → BrushEngine.RasterizeSegments → RenderCurrentStamps
  → TryAccumulateCachedDabsToStrokeMask → SampleMaskAlpha
```

## Render pool

Worker threads spend most time **idle waiting** for compositor jobs (`SemaphoreSlim`, `BlockingCollection`). Compositor work is not saturating the pool in this capture.

## Terminal errors during session

`LayerCompositor.PublishTileDisplayImage` — `HashSet.Add` concurrent corruption on render threads. Worth fixing before trusting compositor profiles under heavy parallel composite.

## Re-profile for large-brush lag

Repeat with intentional workload:

1. Rebuild app after changes.
2. Large circle brush (500–900px), draw a long stroke for full 20s.
3. Same command:

```bash
dotnet-trace collect -p $(pgrep -x Floss) \
  --profile dotnet-sampled-thread-time \
  --format speedscope \
  -o tmp/profile/live-$(date +%Y%m%d-%H%M%S).speedscope.json \
  --duration 00:00:20
```

Compare `BrushEngine` path name via logging or hotspot shift from `SampleMaskAlpha` / `TryRasterizeCachedDabsLightenScratch` vs `CachedTileMajorLighten`.
