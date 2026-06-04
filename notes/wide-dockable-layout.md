# Wide dockable layout (Photoshop / Clip Studio style)

## Reference (Photoshop / CSP)

- **Fixed tool rail** on the far left (~48px): vertical icons, color well at bottom.
- **Wide dock columns** on left and/or right for brushes, color, layers (resizable, not capped at ~440px).
- **Canvas** maximized in the center; no floating tool pill over the viewport.
- **Viewport background**: `CheckerboardOverlay` dot pattern — do not change.

## Previous Floss layout problems

| Issue | Detail |
|-------|--------|
| Floating tool strip | `BuildFloatingToolStrip()` overlay on canvas reduced canvas economy. |
| Narrow right dock | `RightPanelWidth` default 250, max 440. |
| Tools hidden | `HiddenPanelIds` included `"tools"`; tools only in floating strip. |
| Default stack | Brush + tool-properties stacked on the right with color/layers. |

## Target root grid columns

| Index | Role | Width |
|-------|------|-------|
| 0 | Tool rail | 48px fixed (hidden when Tools docker toggled off) |
| 1 | Left dock | 0 or `LeftRailWidth` (200–480px) when left panels visible |
| 2 | Left splitter | 3px |
| 3 | Canvas | `*` |
| 4 | Right splitter | 3px when right dock visible |
| 5 | Right dock | `RightPanelWidth` (240–600px) |

`WorkspaceLayout.LeftRailWidth` stores **left dock** width (not the 48px tool rail).

## Default panel zones (`PanelRegistry`)

- `brush`, `tool-properties` → `left`
- `color`, `color-slider`, `layer-properties`, `layers` → `right-0`
- `tools` → fixed rail only (not placed in `LeftColumn` by default)

## Default `WorkspaceLayout` (v2)

```text
LayoutVersion = 2
LeftColumn.PanelIds = ["brush", "tool-properties"]
RightColumns[0].PanelIds = ["color", "color-slider", "layer-properties", "layers"]
HiddenPanelIds = ["color-slider"]  // optional compact; user can show via Window → Dockers
LeftRailWidth = 280
RightPanelWidth = 320
```

## Migration (`LayoutVersion < 2`)

Only auto-rearrange when layout matches the old factory default (left empty, brush on right). Custom layouts are left intact; widths are still bumped (`RightPanelWidth` min 300, `LeftRailWidth` 280 if left has panels and width ≤ 56).

## Files

- `src/Floss.App/Docking/WorkspaceLayout.cs` — defaults, `LayoutVersion`, migration
- `src/Floss.App/Docking/PanelRegistry.cs` — default zones
- `src/Floss.App/MainWindow/MainWindow.axaml.cs` — root grid, rail, width helpers
- `src/Floss.App/MainWindow/MainWindow.Viewport.cs` — canvas-only column index
- `src/Floss.App/MainWindow/MainWindow.ToolRail.cs` — `BuildToolsContent` uses vertical rail

## Explicitly unchanged

- `src/Floss.App/Canvas/CheckerboardOverlay.cs` — dot spacing, colors, radius
