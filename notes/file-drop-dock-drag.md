# File drop & dock drag UX

## File drop (fixed)
- Use `DataFormat.File` + `DataTransfer.TryGetFiles()` (Avalonia 12), not in-process `FileNames` only.
- Fallback: `text/uri-list`, legacy `FileNames`, single path strings.
- Drop target: window shell + viewport (bubble/tunnel).
- Documents (`.floss`, `.psd`, `.kra`, `.clip`) → `OpenDocumentFromPathAsync` with busy overlay.
- Raster images → new document if no doc; else paste as layer.
- `e.Handled = true` on drag-over so OS shows copy cursor.

## Dock drag (fixed)
- `DockDropOverlay` on window — single parent for indicators (no reparent jitter).
- Row bounds cached at drag start from real control positions.
- Hysteresis on zone changes; thin insertion line + tab strip highlight.
- Hints: column name + action ("Dock below Layers", "Add to tab: Color").

## Files
- `Controls/DockDropOverlay.cs`
- `MainWindow/MainWindow.DockDrag.cs`
- `MainWindow/MainWindow.DragDrop.cs`
- `MainWindow/MainWindow.FileDrop.cs` (optional split)
