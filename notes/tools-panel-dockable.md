# Tools panel — dockable (not hardcoded)

## Before

- `tools` was registered in `PanelRegistry` but `WorkspaceLayout.Normalize` stripped it from dock columns.
- A **fixed 52px column** on the root grid (`RootColToolRail`) always hosted `BuildLeftRail()`.
- Toggling Window → Dockers → Tools only showed/hid that column.

## After

- **No root tool rail column** — root grid: left dock | splitter | canvas | splitter | right dock.
- `tools` is a normal panel: float, hide, move between left/right/bottom via dock drag (`MainWindow.DockDrag.cs`).
- Default: left tab `tab:left` contains `tools`, `brush`, `tool-properties` (brush tab active).
- Side-by-side: drag panel header to **left/right edge** of another panel (`notes/horizontal-dock-drag.md`).

## Key files

- `src/Floss.App/Docking/WorkspaceLayout.cs` — `MigrateToDockableToolsV4`, default tab ids
- `src/Floss.App/MainWindow/MainWindow.axaml.cs` — root grid columns
- `src/Floss.App/MainWindow/MainWindow.ToolRail.cs` — `BuildToolsContent()` panel body
