# Floss Development Agent Notes

## Critical Bugs & Fixes

### Viewport Tools (Hand/Rotate/Zoom) Not Working Initially
**Root cause:** `DrawingCanvas.SetViewport()` was only called on `Window.Loaded` and `SizeChanged`. When `SwitchToTab()` creates/opens a document, it swaps in a **brand new `DrawingCanvas`** that never gets its viewport set. `ToolContext.Viewport` is null, so `HandOutput`/`RotateOutput`/`ZoomOutput` silently bail.

**Fix:** Added `SyncCanvasViewport()` call at the end of `SwitchToTab()` in `MainWindow.Tabs.cs`.

**Why resize fixed it:** Resizing fires `SizeChanged` → `SyncCanvasViewport()` → viewport is finally set.

**File:** `src/Floss.App/MainWindow/MainWindow.Tabs.cs`

## Architecture

### Viewport Coordinate System
- Canvas frame uses `TransformGroup` with `ScaleTransform` (flip), `ScaleTransform` (zoom), `RotateTransform`, `TranslateTransform` (pan)
- `RenderTransformOrigin = RelativePoint(0.5, 0.5)` — transforms are centered on the canvas
- Viewport tools (Hand/Rotate/Zoom) receive **raw viewport pixel coordinates** via `HandleViewportPointerInput()` to avoid feedback loops when zoom/pan change during drag
- Drawing tools receive **canvas-local coordinates** via `HandlePointerInput()`
- `IViewportController` lives on `MainWindow`, manipulated by output processes
- **Viewport state sync:** `SyncViewportStateToCanvas()` helper syncs all transform state (pan, zoom, rotation, flip) from MainWindow to DrawingCanvas in one place

### Modifier Key System
- Replaces old `GestureMode` pen-gesture system (fully removed)
- `ModifierKeySettings.Resolve(inputType, outputType, heldKey, mods)` maps key combos to actions
- `PushTemporaryPreset()` / `PopTemporaryPreset()` switches tools temporarily (e.g. Alt→Eyedropper)
- `PopTemporaryPreset()` must commit pending operations before switching back
- CSP-like defaults: Space=Hand, Ctrl+Space=ZoomIn, Alt=Eyedropper

### Tool Dispatch
- `Workspace_OnPointerPressed/Moved/Released` handles all pointer events
- `IsViewportTool()` routes to `HandleViewportPointerInput()` for viewport tools, `HandlePointerInput()` for drawing tools
- Middle-button pan works via `_isMiddleButtonPanning` flag (previously broken by early return)
- `CompositeTool` wires `IInputProcess` → `IOutputProcess`
- `DragInputProcess` produces `DragInput` for viewport tools

### Brush Clipping to Document Bounds
- `HandlePointerInput` no longer clamps coordinates to `[0, docW] × [0, docH]` — instead passes raw coordinates through
- `BrushEngine` clips `dirty` region to `layer.Width × layer.Height` before rendering via `RenderWithSkia`
- Stamps outside the document are still generated (for spacing continuity) but their dirty region is clipped, so no tiles are created outside bounds
- This prevents the hard edge line that appeared when clamping coordinates

## Important Files

- `MainWindow.Viewport.cs` — viewport transforms, pointer event handlers, `IViewportController`, `SyncViewportStateToCanvas()`
- `MainWindow.Tabs.cs` — tab switching, canvas swapping, **must call `SyncCanvasViewport()`**
- `Canvas/DrawingCanvas.cs` — `HandlePointerInput`, `HandleViewportPointerInput`, `Render`
- `Processes/Output/HandOutput.cs` — raw viewport pixel deltas
- `Processes/Output/RotateOutput.cs` — rotation around viewport center
- `Processes/Output/ZoomOutput.cs` — zoom around cursor start position
- `Processes/CompositeTool.cs` — tool dispatch (`Preview`/`Execute`)
- `Tools/Core/ToolController.cs` — active tool management, alternate tool switching
- `Canvas/LayerCompositor.cs` — tiled compositing, viewport culling

## Avalonia Gotchas

- `TransformGroup` does **not** propagate child transform change notifications to parent visual before first full render cycle
- `_canvasFrame?.InvalidateVisual()` must be called alongside `_checkerboardOverlay?.InvalidateVisual()`
- `_canvasFrame.IsVisible = false` at startup to avoid rendering empty canvas
- **Centralized invalidation:** `InvalidateViewport()` helper ensures ALL viewport mutations (pan, zoom, rotate, reset) consistently invalidate frame, canvas, and overlays. Prevents the classic refactor bug where one `InvalidateVisual()` call gets dropped and tiles go stale.

## Performance Optimizations Applied

### GPU Resource Cache
- **SkiaOptions:** `MaxGpuResourceSizeBytes = 512 * 1024 * 1024` (512 MB) for large image/tile caching
- Prevents texture re-upload stutter on large documents

### BitmapCache for Static Panels
Added `BitmapCache` to complex panels that change infrequently:
- **Tool rail** (`MainWindow.ToolRail.cs`) — icons, color well, buttons
- **Layer panel** (`MainWindow.LayerPanel.cs`) — layer list, controls
- **Brush panel** (`MainWindow.BrushLibrary.cs`) — brush presets, sliders

These panels are rasterized once and reused until explicitly invalidated.

### Hit Test Optimization
Set `IsHitTestVisible = false` on:
- Status bar and footer (purely decorative, no interaction)
- Already applied: checkerboard overlay, ruler overlay, resize overlay

This reduces visual tree walk during pointer events.

### Brush Tip Mask Caching
All brush tip types now cache generated masks:
- `ProceduralBrushTip` — caches by `(size, quantizedHardness)`
- `ImageBrushTip` — caches by `(size, quantizedHardness)`
- `CompoundBrushTip` — **newly added** cache by `(size, quantizedHardness)`

Masks are only regenerated when size or hardness changes significantly.

### Deferred UI Updates
Added `PostUpdateStatus()` and `PostUpdateTitle()` helpers that dispatch to `DispatcherPriority.Background`.
Used in high-frequency viewport operations:
- `PanBy` (Hand tool, middle-button pan)
- `SetZoom` (wheel zoom, zoom tool)
- `SetRotation` (rotate tool)
- `ResetView`

Status bar text updates no longer block the render thread during continuous viewport manipulation.

### Viewport State Sync Deduplication
Extracted `SyncViewportStateToCanvas()` helper to replace 13 scattered assignments:
- Before: `_canvas.PanOffsetX = _canvasPan.X; _canvas.PanOffsetY = _canvasPan.Y; _canvas.CanvasZoom = _zoom; ...` repeated across 6+ methods
- After: Single call to `SyncViewportStateToCanvas()`

Reduces maintenance burden and ensures consistency.

## Known Issues / Cleanup Needed

- [x] ~~Dead middle-button pan code~~ — Fixed by adding `_isMiddleButtonPanning` flag and updating early-return check
- [x] ~~HandOutput debug `Console.WriteLine("fuck")`~~ — Removed
- [x] ~~Console.WriteLine in FileIO.cs~~ — Removed
- [x] ~~Commented-out code in BrushEngine.cs line 164~~ — Removed
- [x] ~~Unused `CursorPan` field~~ — Removed
- [x] ~~Duplicate viewport state sync pattern~~ — Extracted into `SyncViewportStateToCanvas()`
- [x] ~~Scattered viewport invalidations~~ — Centralized into `InvalidateViewport()` helper
- [x] ~~Cursor flickering during drawing~~ — Workspace viewport cursor managed during captured strokes
- [x] ~~Brush drawing hard edge at canvas boundary~~ — Removed clamp from `HandlePointerInput`, added `ClipTo(layer.Width, layer.Height)` in `BrushEngine`
- [ ] `InvalidateCompositor()` added but may not be used everywhere needed
- [ ] `PaintInputSuspended` flag is set in multiple places, potential for desync
- [ ] `MainWindow.axaml.cs` is 2589 lines — consider further splitting

## Shortcuts
- Input/KeyBinding.cs — data model for key+modifiers, parsing, formatting, modifier-state helpers
- Input/ShortcutsConfig.cs — all app-wide shortcut definitions (Undo, Zoom, Brush, Layer...), JSON load/save
- MainWindow/MainWindow.axaml.cs — registers AddShortcut() linking config to actions, OnKeyDown for tool-group & Escape
- MainWindow/MainWindow.ToolRail.cs — tool-group shortcut recording/assignment, hint display on buttons
- Windows/SettingsWindow.cs — UI for reassigning shortcuts (key capture)
Modifier keys:
- Input/ModifierKeySettings.cs — modifier→action mappings, dictionary-based lookup, JSON load/save
- MainWindow/MainWindow.Viewport.cs — OnKeyDownTunnel / OnKeyUp, invokes modifier resolution
- Windows/ModifierKeySettingsWindow.cs — UI for editing modifier-key assignments
Shared:
- App.axaml.cs — exposes singleton ShortcutsConfig / ModifierKeySettings loaded from disk
- AppPaths.cs — file paths for both configs
- tests/Floss.App.Tests/Program.cs — KeyBindingTests covering parsing, modifiers, round-trip
