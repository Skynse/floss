# Canvas input policy

## Problem

Transform and smart-shape edit are **transient edit sessions**: the user stays in a modal tool until explicit commit/cancel. Modifier keys during these sessions must update `ToolContext.CurrentModifiers` (e.g. Ctrl constrains transform aspect ratio) without triggering brush-specific shortcuts (Ctrl → move layer) or committing the session.

Previously, behavior was patched with scattered `IsTransformActive` / `IsSmartShapeEditActive` checks. Modifier resolution still used `_activeToolGroup.ActivePreset` types while `TransformTool` was active, so Ctrl resolved as **move layer** and `PushTemporaryPreset` **committed** the transform.

## Model

`CanvasInputPolicy` (`src/Floss.App/Input/CanvasInputPolicy.cs`) is the single contract:

| Mode | Modifier resolve types | Temporary presets | Commit on push/pop |
|------|------------------------|-------------------|--------------------|
| **Normal** | Active preset input/output | All configured | Yes (modal tools) |
| **Transient edit** | General table only `(0,0)` | Viewport nav only (Space pan/zoom/rotate) | No |

Transient edit is active when:

- `DrawingCanvas.IsTransformActive`
- `DrawingCanvas.IsSmartShapeEditActive`

**Filter preview** uses `CanvasInputPolicy.FilterPreview` (same modifier/viewport-nav rules, plus `BlocksPrimaryToolPointer` so clicks do not start brush strokes). Enabled by `EnterFilterPreviewSession()` for the lifetime of a non-modal filter window (`ShowFilterDialog`).

## Consumers

| Component | Uses policy for |
|-----------|-----------------|
| `CanvasInputRouter.ReevaluateModifierState` | Modifier tool types + allowed actions |
| `MainWindow.PushTemporaryPreset` / `PopTemporaryPreset` | Viewport overlay vs commit |
| `CanvasInputRouter.DeactivateAlternateIfNeeded` | Skip commit during transient edit |
| `DrawingCanvas.InputPolicy` | Source of truth from canvas state |

## OS cursor ownership

Two cursor modes, owned exclusively by `MainWindow.Viewport.Cursor.SyncViewportOsCursor`:

| Mode | `ShouldShowToolCursor` | OS cursor | Visual |
|------|------------------------|-----------|--------|
| Brush / painted preview | true | Hidden (`CursorNone`) on viewport + canvas | `ViewportCursorOverlay` draws ring |
| Transform, smart-shape, etc. | false | Native cursor on viewport **and** canvas | Gizmo `CursorFor` (resize, move, …) |

**Regression:** `DrawingCanvas` used to set `Cursor = CursorNone` in every pointer handler (for painted brush). Tunnel handlers on the workspace synced the transform cursor first; canvas `OnPointerMoved` ran after and forced `CursorNone` over the document — invisible mouse over the gizmo.

`DrawingCanvas` must not set `Cursor` directly; only `SyncViewportOsCursor` applies cursor state. `EnterRunning` hides the OS cursor only when `HidesOsCursorForPaintedPreview` is true (active stroke), not during transform drag.

## Tool-level modifiers

Tools that consume modifiers internally implement `ITool.ConsumesModifier` (transform: Control for aspect lock). Router policy is the primary gate; `ConsumesModifier` documents tool ownership of keys.

## Files

- `src/Floss.App/Input/CanvasInputPolicy.cs`
- `src/Floss.App/Input/CanvasInputRouter.cs`
- `src/Floss.App/Canvas/DrawingCanvas.cs` — `InputPolicy` property
- `src/Floss.App/MainWindow/MainWindow.Viewport.cs` — host `GetInputPolicy`
- `src/Floss.App/MainWindow/MainWindow.axaml.cs` — temporary preset push/pop
