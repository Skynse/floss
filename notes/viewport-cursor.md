# Viewport cursor behavior

## Symptoms (fixed)

1. **Invisible cursor on startup** over viewport: `_workspaceViewport.Cursor = None` hid the OS cursor before `_isPointerOver` was set (no move/enter yet), and the custom preview only drew inside `DrawingCanvas` bounds (not the full viewport).
2. **OS cursor visible while drawing**: `ResetCursor()` cleared `MainWindow.Cursor` on stroke end; `canvasFrame.PointerExited` restored arrow; `SetCursorNone` only targeted the window, not the full viewport stack; modifier/temp-tool strokes skipped `SetCursorNone`.

## Architecture

| Piece | Role |
|-------|------|
| `ViewportCursorOverlay` | Full-viewport, non-hit-test layer; draws tool cursor in **viewport pixels** |
| `DrawingCanvas.RenderToolCursorInViewportSpace` | Shared preview drawing (outline, dot, brush shape, eyedropper swatch) |
| `DrawingCanvas.TrackViewportPointer` | Stores viewport + canvas positions; sets `_isPointerOver` |
| `MainWindow.SyncViewportCursor` | Hides OS cursor on viewport, canvas, frame, host when custom preview active |
| `CanvasInputRouter.EnterRunning` | Hides OS cursor for all non-viewport-tool runs |

## Pointer lag (latency)

| Piece | Role |
|-------|------|
| `RenderToolCursorOnCanvas` | Cursor drawn on canvas every paint (reliable) + `ViewportCursorOverlay` over full viewport |
| `NotifyCursorPreviewChanged` | Only invalidates overlay (no full canvas repaint per move) |
| `StrokeInput.RawLead` + `PaintPreviewTail` | Revert tail tiles, paint last permanent → raw pen; no spacing desync |
| `Stabilization <= 0` | Raw samples go straight to smoothed list (no deque average) |

See also `notes/stroke-latency-cursor.md`.

## Unchanged

- `CheckerboardOverlay` dot pattern (viewport background only)

## Key files

- `src/Floss.App/Canvas/ViewportCursorOverlay.cs`
- `src/Floss.App/Canvas/DrawingCanvas.cs`
- `src/Floss.App/MainWindow/MainWindow.Viewport.Cursor.cs`
- `src/Floss.App/MainWindow/MainWindow.Viewport.cs`
- `src/Floss.App/Input/CanvasInputRouter.cs`
