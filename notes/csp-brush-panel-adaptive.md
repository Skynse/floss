# Clip Studio adaptive brush preset panel (reference)

Source: user screenshots + Floss brush dock (`MainWindow.BrushLibrary.cs`).

## CSP behavior

| Panel width | Layout |
|-------------|--------|
| Narrow | Single column; list rows (preview + name). |
| Wider | Multi-column **grid**; tiles wrap. Preview bitmap size stays stable; cell chrome flexes slightly. |
| Toolbar strip | Horizontal row of sub-tools when docked wide (separate surface). |

Goal: avoid re-rendering stroke previews on every dock resize — only re-render when brush parameters change.

## Floss implementation

| Piece | Approach |
|-------|----------|
| Tool icons (`MainWindow.ToolRail.cs`) | `WrapPanel`; 40×36 rail buttons; wrap to more columns when tools docker/rail is wide; width synced to scroll viewport. |
| Categories | `WrapPanel`; tabs size to label (no star-column ellipsis); wrap to new rows; width synced to preset viewport. |
| Presets | `WrapPanel` width clamped to scroll **viewport** (fixes infinite-width single-row clip). Column count from viewport; `ItemWidth` divides space evenly; tile chrome stretches, preview centered at 128×44. |
| Preview | `BrushStrokePreview` with `FixedRenderWidth` / `FixedRenderHeight` (128×44). |
| Cache key | `BuildBrushPreviewKey` — unchanged. |

## Key files

- `src/Floss.App/MainWindow/MainWindow.BrushLibrary.cs`
- `src/Floss.App/Controls/BrushStrokePreview.cs`
