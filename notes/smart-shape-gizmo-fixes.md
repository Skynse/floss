# Smart shape gizmo fixes

## Symptoms

1. **Modifier keys / zoom abort gizmo** — `PushTemporaryPreset` calls `CommitActiveTool()` when `HasPendingOperation`; smart-shape gizmo counts as pending. `ResetTransientInputState` clears router state on focus loss.
2. **Ragged curves + handle spam** — `ClassifyOpen` runs `FitBezierCurves` on dense raw tablet samples → dozens of cubic segments; gizmo draws P0–P3 per segment; overlay tessellates each segment to 64-point polyline.

## Fixes

| Fix | Location |
|-----|----------|
| `IsSmartShapeEditActive` — block tool swap; viewport pan via overlay only | `DrawingCanvas`, `PushTemporaryPreset`, `ResetTransientInputState` |
| Fit beziers on RDP-simplified points, higher error, max 4 segments | `SmartShapeAnalyzer.ClassifyOpen` |
| Draw curves as native Bézier paths | `SmartShapeOverlay` |
| Dedupe gizmo handles at shared positions | `SmartShapeGizmo` |
| `AbortLiveStroke` waits for direct draw idle | `SmartShapeBrushOutput` |

## Key files

- `src/Floss.App/SmartShape/SmartShapeAnalyzer.cs`
- `src/Floss.App/SmartShape/SmartShapeOverlay.cs`
- `src/Floss.App/SmartShape/SmartShapeGizmo.cs`
- `src/Floss.App/MainWindow/MainWindow.axaml.cs` (`PushTemporaryPreset`)
- `src/Floss.App/MainWindow/MainWindow.Viewport.cs` (`ResetTransientInputState`)
