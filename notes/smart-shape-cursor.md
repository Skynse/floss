# Smart shape + viewport cursor

## Symptoms

1. **Frozen brush cursor on Ctrl+N** — `ViewportCursorOverlay.Canvas` was not updated in `SwitchToTab`; overlay kept painting the old tab's frozen `_viewportPointerPos`.
2. **No brush cursor while drawing normally** — fixed via `PreferViewportToolCursor`. Normal brush strokes during `Drawing` phase use `DirectDrawOutput` preview; overlay-only applies only during `Adjusting`/`Gizmo`.

## Architecture

| Piece | Role |
|-------|------|
| `ViewportCursorOverlay` | Single painted cursor path during strokes (`PreferViewportToolCursor`) |
| `SwitchToTab` | `_viewportCursorOverlay.Canvas = _canvas`, `ClearViewportPointer()` on departing tab |
| `SmartShapeBrushOutput.Preview` | No `DirectDrawOutput` during `Drawing`/`Adjusting` when smart shapes enabled (overlay-only scribble per `notes/smart-shapes.md`) |
| `CompositeTool.PointerMove` | `InvalidateRender` + `InvalidateToolCursor` when smart-shape phase has no layer preview |
| `ToolContext.InvalidateToolCursor` | Light overlay repaint via `NotifyCursorPreviewChanged` |

## Key files

- `src/Floss.App/Canvas/DrawingCanvas.cs` — `PreferViewportToolCursor`, `InvalidateToolCursor`
- `src/Floss.App/Canvas/ViewportCursorOverlay.cs`
- `src/Floss.App/MainWindow/MainWindow.Tabs.cs`
- `src/Floss.App/Processes/Output/SmartShapeBrushOutput.cs`
- `src/Floss.App/Processes/CompositeTool.cs`
