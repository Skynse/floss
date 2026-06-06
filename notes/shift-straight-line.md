# Shift straight line (CSP point-to-point)

## Flow
1. Click/draw — anchor set at stroke end (`BrushStrokeInputProcess.FinishStroke`).
2. Hold Shift — `ToolAuxOperationType.StraightLine` via modifier keys.
3. Shift+click — `GetImmediateResult()` returns anchor→click segment; endpoint becomes new anchor.

## Bug (fixed)
`SmartShapeBrushInputProcess` wraps all DirectDraw tools but did not forward `GetImmediateResult()` to inner `_brush`, so shift-click lines never committed when smart shape was enabled.

## Disable
`AppConfig.ShiftStraightLineEnabled` + Settings checkbox. `DrawingCanvas.SetToolAuxMode` ignores StraightLine when off.

## Files
- `SmartShapeBrushInputProcess.cs` — forward `GetImmediateResult`
- `BrushStrokeInputProcess.cs` — shift-click does not start extra micro-stroke
- `AppConfig.cs`, `SettingsWindow.cs`, `DrawingCanvas.cs`
