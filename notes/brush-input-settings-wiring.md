# Brush input settings wiring

## Source of truth

**`BrushPreset`** holds stroke-input fields used by the engine:

| Field | UI (Brush Settings) | Runtime |
|-------|----------------------|---------|
| `Smoothing` | Stabilization slider | `BrushStrokeInputProcess.Stabilization` |
| `SpeedAdaptiveStabilizer` | Adjust by speed checkbox | `BrushStrokeInputProcess.SpeedAdaptiveStabilizer` |

Other brush fields (size, opacity, spacing, …) follow the same path.

## Data flow

```
UI change (ToolPropertiesWindow.Commit or MainWindow sliders)
  → UpdateCurrentBrush / UpdateCurrentBrushFromToolProperties
  → _activePreset (BrushPreset)
  → ApplyBrushSettings → DrawingCanvas.SetBrush
  → ApplyBrushToStrokeInputs (syncs input process)
  → ToolPreset.CaptureFromBrushPreset → BrushOverride (persisted in tool-groups JSON)
```

Re-opening **Brush Settings** calls `SyncFromPreset(_activePreset)` — UI reads the preset, not a separate window field.

## Persistence layers

1. **Per tool preset** — `ToolPreset.BrushOverride` via `CaptureFromBrushPreset` / `BrushPresetOverrideDocument`
2. **Per brush asset** — `.flbr` file via `BrushFileFormat` v14+ (`SpeedAdaptiveStabilizer` bool after parameter graphs)

## Do not

- Store parallel flags on `MainWindow` or `ToolPropertiesWindow` for preset-backed settings
- Call canvas-only setters that skip updating `BrushPreset` (settings will reset on window reopen)

## UI surfaces (brush)

| Surface | Brush scalar changes | Tool-preset changes |
|---------|---------------------|---------------------|
| Brush Settings window | `Commit` → `UpdateCurrentBrushFromToolProperties` | `CommitTool` → `_onChange` + `App.ToolGroups.Save` |
| Main toolbar sliders | `SliderChanged` → `UpdateCurrentBrush` | — |
| Tool property docker (brush) | Shared toolbar sliders **or** `_activePreset` + `UpdateCurrentBrush` for bools | `UpdateActiveToolPreset` (save + rebind tool) |

After Brush Settings edits, `SyncBrushScalarControls` keeps toolbar/docker sliders aligned with `_activePreset`.

## Tool property docker

Non-brush controls must use `UpdateActiveToolPreset`, not `preset.Field = value` alone (misses save + live tool sync). Magic wand / liquify / selection already did; flood fill, lasso stabilization, stroke, gradient were fixed to match.

## Pinning (BRUSH docker panel)

- Visibility stored in `AppConfig.ToolPropertyDockerVisibility` (persisted in `config.json`).
- Eye buttons in Brush Settings call `App.Config.ToggleToolPropertyDockerVisible(id)`.
- The docked **BRUSH** panel must use the **same** `GetToolPropertiesContent()` instance as `RefreshToolProperties()` — building the section twice left the dock showing a stale tree while updates targeted a second copy (`_toolPropertyPanel` field overwrite).

## Files

- `Brushes/BrushPreset.cs` — model
- `Brushes/BrushPresetDocument.cs` / `BrushPresetOverrideDocument.cs` — serialization
- `Windows/ToolPropertiesWindow.cs` — floating brush settings UI
- `MainWindow/MainWindow.BrushLibrary.cs` — `ApplyBrushSettings`, `OpenToolProperties`
- `Canvas/DrawingCanvas.cs` — `SetBrush`, `ApplyBrushToStrokeInputs`
- `Processes/Input/BrushStrokeInputProcess.cs` — stabilizer implementation
