# Scrub slider (app-wide)

Replaces Avalonia `Slider` for property panels. Reference: Photoshop channel sliders (pointer under track, click-to-jump + drag).

## Problems with Avalonia Slider

- `Track` splits into `PART_DecreaseButton` / thumb / `PART_IncreaseButton`; Fluent theme can show spacer/grip chrome on repeat buttons.
- Square thumb is a small hit target; clicking the track does not reliably jump the value.

## ScrubSlider (`Controls/ScrubSlider.cs`)

- Subclasses `RangeBase` (`Minimum`, `Maximum`, `Value`) — drop-in for existing `MkSlider` / `WireSlider` code.
- **Track**: 11px bar, optional `TrackBackground` brush (solid or gradient).
- **Fill**: optional left-to-value tint (`ShowValueFill`, default on for tool sliders).
- **Marker**: white triangle below track, dark stroke.
- **Hit area**: full track + marker band; click jumps, drag updates, pointer capture.

## Factory

`ScrubSliderFactory.Create(min, max, value, tip?)` — shared by MainWindow, ToolPropertiesWindow, DynamicsPopupWindow.

## HSV rows

`HsvSliderRow` composes `ScrubSlider` + label + numeric field; gradients set via `TrackBackground`.

## Files to migrate off `Slider`

- `MainWindow.axaml.cs`, `MainWindow.*.cs`
- `ToolPropertiesWindow.cs`, `DynamicsPopupWindow.cs`
- `SettingsWindow.cs`, `AngleDynamicsPopupWindow.cs`, `ExportWizardDialog.cs`, `TimelapseExportDialog.cs`
- `AdjustmentLayerDialog.cs`, `MainWindow.Filters.cs`, `NodeGraphEditorPanel.cs`, `MainWindow.Color.cs`

`App.axaml` horizontal `Slider` style can remain for any stragglers but is unused once migration completes.

## Performance

Resize jank was caused by per-slider `SizeChanged` handlers allocating new `StreamGeometry` and relayouting fill `Width` on every dock resize frame.

`ScrubSlider` now:

- Builds marker geometry **once**; position via `TranslateTransform`
- Fill uses `ScaleTransform` (no per-frame width layout)
- Fill stretches inside the track; **never** sets `Width` during layout (avoids infinite layout loops)
- `OnSizeChanged` updates transforms when the control width changes
- Pointer drag caches track origin/width (no `TranslatePoint` per move)
- `ScrubCompleted` fires on release; heavy handlers (`UpdateCurrentBrush`, `Commit`, graph `DoCommit`) defer to that event; live scrub uses lightweight preview where needed
