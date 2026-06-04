# Docking overhaul (reference)

## Problem
- `MainWindow` builds docks; `DesktopDocker` drag-drop is unused.
- `FindDockerPlacement` only searches top-level `PanelIds`, not tab members (`brush` inside `tab:left`).
- `Normalize()` stripped `tab:*` keys (fixed separately).
- `DockTabGroup` tabs only switch panels; no drag-out to new docker rows.

## Model
- **Row key** in `DockColumnLayout.PanelIds`: solo panel id OR `tab:*` placeholder.
- **Tab group**: `TabGroups[key].PanelIds` = member panels.
- **Resolved rows**: `DockColumnLayout.ResolvedRows()`.

## Code locations
| File | Role |
|------|------|
| `Docking/DockLayoutOps.cs` | Find placement, move, dock column, extract tab → row, apply drop |
| `Docking/DockTabGroup.cs` | Tab strip UI + tab drag |
| `MainWindow/MainWindow.DockDrag.cs` | Header/tab drag, drop preview, `ApplyDockDrop` |
| `MainWindow/MainWindow.axaml.cs` | Context menu → `DockLayoutOps`; `PanelSection` Tag + header drag |
| `Controls/ScrollHelper.cs` | Default `ScrollBarVisibility.Visible` |

## Drop kinds (`ApplyDockDrop`)
- `insertRow`: solo row at index in column
- `mergeTab`: add panel to tab group at row
- `splitHorizontal`: `Rows` horizontal split (context menu later)
