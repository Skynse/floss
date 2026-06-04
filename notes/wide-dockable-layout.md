# Wide dockable layout (Photoshop / Clip Studio style)

## Reference (Photoshop / CSP)

- **Tools** dockable on the left (tab or stacked row), not a hardcoded root column.
- **Wide dock columns** on left and/or right for tools, brushes, color, layers (resizable, not capped at ~440px).
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
| 0 | Left dock | 0 or `LeftRailWidth` (200–720px) when left panels visible |
| 1 | Left splitter | 3px |
| 2 | Canvas | `*` |
| 3 | Right splitter | 3px when right dock visible |
| 4 | Right dock | `RightPanelWidth` (240–600px) |

`WorkspaceLayout.LeftRailWidth` stores left dock width (includes Tools when docked there).

## Default panel zones (`PanelRegistry`)

- `tools`, `brush`, `tool-properties` → `left` (default tab `tab:left`; horizontal via drag — `notes/horizontal-dock-drag.md`)
- `color`, `color-slider`, `layer-properties`, `layers` → `right-0`

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
- `src/Floss.App/MainWindow/MainWindow.axaml.cs` — root grid, width helpers
- `src/Floss.App/MainWindow/MainWindow.Viewport.cs` — canvas-only column index
- `src/Floss.App/MainWindow/MainWindow.ToolRail.cs` — `BuildToolsContent` panel body
- `notes/tools-panel-dockable.md` — tools no longer hardcoded

## Explicitly unchanged

- `src/Floss.App/Canvas/CheckerboardOverlay.cs` — dot spacing, colors, radius
