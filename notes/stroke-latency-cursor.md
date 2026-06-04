# Stroke latency, cursor, and dab spacing

## Symptoms

1. Custom brush cursor invisible; OS cursor hidden.
2. Ink lags pen even with stabilization 0.
3. Beaded stroke (compressions/rarefactions) on round/procedural brushes.

## Root causes

| Issue | Cause |
|-------|--------|
| Invisible cursor | Cursor removed from `DrawingCanvas.Render`; overlay not invalidated by `InvalidateViewport()`; `NotifyCursorPreviewChanged` only invalidated heavy canvas repaint |
| Ink lag (stabilized) | Smoothed trail behind `RawLead`; tail painted without tile revert → spacing desync |
| Ink lag (stab 0) | Full canvas `InvalidateVisual` per pointer move; compositor/display path behind sync layer writes |
| Beads (general) | Preview tail advanced `StrokeState` while tiles were reverted; spacing accumulator drifted from pixels |
| Beads (large round pen) | `ShouldCollapseToSingleStamp`: `distance <= 0.75 * size` → one dab per short stabilizer segment on big brushes |
| Beads (curved ink) | Catmull-Rom between sparse stabilized points instead of chord polyline for circle tips |

## Fixes (this pass)

1. **Cursor**: Draw on canvas again in canvas space; invalidate `ViewportCursorOverlay` from `InvalidateViewport`; stop invalidating full canvas on cursor-only updates; do not set `TopLevel.Cursor = None`.
2. **Preview tail**: Revert tail tiles from snapshot, paint `lastPermanent → RawLead`, snapshot tiles for next revert; process only `NextSegmentIndex < PermanentQueuedCount`.
3. **Spacing**: Circle/ellipse procedural tips use linear chord stamping; fast segments too.
4. **Collapse rule**: Only collapse when `distance <= spacing * 0.85`, not `distance <= 0.75 * brushSize`.
5. **Preview tail removed**: Per-frame tile capture/revert on large brushes caused multi-second freezes and rectangular artifacts.
6. **Stabilization**: No forced 0.3 default; speed-adaptive no longer drops to 1 sample; slider updates `ApplyBrushToStrokeInputs`.
7. **Brush resize cursor**: `TransformToVisual` for locked center; overlay skipped when locked; canvas repaints on gesture/zoom.
8. **Curve defaults**: Two endpoints in editor; `PowerGamma` LUT for ink presets; legacy 9-point auto curves collapsed.

## Key files

- `src/Floss.App/Canvas/DrawingCanvas.cs` — `RenderToolCursorOnCanvas`, `NotifyCursorPreviewChanged`
- `src/Floss.App/MainWindow/MainWindow.Viewport.Cursor.cs`, `MainWindow.Viewport.cs`
- `src/Floss.App/Processes/Output/DirectDrawOutput.cs` — preview tail revert/paint
- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — fast linear stamping

## Krita reference behavior

- Pointer samples drive display cursor in screen/viewport space.
- Live stroke reaches the pen; stabilization smooths committed points, not the visible tail.
- Dab spacing along a segment is uniform (distance-based), not reset by preview passes.
