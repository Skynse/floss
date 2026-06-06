# Smart Shape — Simple flow (no manual edit)

## UX

1. Draw stroke (normal brush preview on overlay only until hold).
2. Hold still → auto-fit shape.
3. **While still holding:** drag away from center to scale, around center to rotate (Shift → perfect circle/square/regular polygon).
4. Release pen → commit fitted stroke with **double undo** (fitted → raw → clear).

Esc during preview (pen still down after fit) → cancel.

## State machine

```
Idle → Drawing → Preview → (pointer up) → commit → Idle
         │                      │
         └ pointer up, no fit └→ Idle (discard)
```

Removed phases: `Adjusting`, `Launcher`, `Gizmo`.

## Files

| File | Change |
|------|--------|
| `SmartShapeModel.cs` | `SmartShapePhase.Preview` only |
| `SmartShapeBrushInputProcess.cs` | Hold → fit → preview; up → commit |
| `SmartShapeBrushOutput.cs` | Preview phase only for stroke preview |
| `CompositeTool.cs` | Draw preview in `Preview`; no `CanCommitFromClick` for old phases |
| `DrawingCanvas.cs` | `IsSmartShapeEditActive` = Preview; cancel helper |
| `MainWindow.SmartShape.cs` | Launcher bar always hidden |
| `MainWindow.axaml.cs` | Esc/deselect cancel preview, not commit |

Double undo unchanged: `SmartShapeCommitRasterizer` + `PushLayerTileHistoryPatches`.

Reference before edits: `notes/smart-shapes.md` (historical CSP design).
