# Standard-canvas stroke UI budget

Profiles on 1920×1080 (not large-cell path): long zigzag and 600px brush choke until pen-up.

## Cause

`DirectDrawOutput.Preview` calls `ProcessQueuedSync(drainAll: true)` — every pointer event processes the **entire** sample queue on the UI thread. When `RasterizeSegments` cannot keep up, the queue grows and each event takes longer (backlog spiral). Pen-up stops input and the queue drains.

Large-cell overlay/consolidation does **not** apply below 32 MB/cell.

## Fix

Time-slice preview when stroke is "heavy":

- Brush diameter ≥ 128 px, or
- Unprocessed segment backlog > 16, or
- Learned avg segment time > 4 ms with backlog > 4

Heavy preview: 8 ms `RenderSliceBudgetMs` per call, max 4 segments/slice, 16 ms `NotifyChanged` throttle. Continue backlog on `DispatcherPriority.Render` with same mode. Pen-up / finalize still `drainAll: true`.

## File

- `src/Floss.App/Processes/Output/DirectDrawOutput.cs`
