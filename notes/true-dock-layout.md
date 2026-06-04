# True dock layout (unbounded columns / rows)

## Target model

```text
[ LeftColumns* ] | canvas stack | [ RightColumns* ]
                  |  viewport    |
                  | [ BottomColumns* ]  (under viewport, in center)
```

- **Left / right / bottom**: each side is `List<DockColumnLayout>` — unbounded column count.
- **Rows** inside a column: unbounded via `Rows` list (`ApplyInsertRow`).
- **Tabs**: vertical multi-panel rows (`MergeTab`).

## Column index encoding (`DockColumnIndices`)

| Zone | Index |
|------|--------|
| Left i | `-(i+1)` → -1 … -9999 |
| Right i | `i` → 0, 1, … |
| Bottom i | `BottomBase - i` → -10000, -10001, … |

`BottomBase = -10000` — no collision with left indices.

## Persistence

- `ColumnProportions["left-0"]` — relative width/height share when N columns on a side (normalized on save).
- `PanelProportions[id]` — row heights inside a column (unchanged).
- `LeftRailWidth`, `RightPanelWidth`, `BottomDockHeight` — total side size.

## Drag-drop (`InsertDockColumn`)

| Zone | Edge |
|------|------|
| Left | Canvas-left strip, panel L/R, column splitters |
| Right | Canvas-right strip, panel L/R, splitters |
| Bottom | Viewport-bottom strip, panel L/R (new bottom column), splitters |

## Node graph

Moved from bespoke `AttachNodeGraphDockToCenter` rows into `BottomColumns[0]` like any panel; visibility via `HiddenPanelIds` + bottom host rebuild.

## Files

- `Docking/DockZone.cs`, `DockColumnIndices.cs`
- `Docking/WorkspaceLayout.cs` — `BottomColumns`, `ColumnProportions`, v6 migration
- `Docking/DockLayoutOps.cs` — zone-aware `GetColumn`, `ApplyInsertDockColumn`
- `MainWindow/MainWindow.DockHost.cs` — shared multi-column host builder
- `MainWindow/MainWindow.BottomDock.cs` — bottom host in center grid
- `MainWindow/MainWindow.NodeGraphDock.cs` — use standard bottom dock
- `MainWindow/MainWindow.DockDrag.cs` — bottom metrics + edges
- `MainWindow/MainWindow.axaml.cs` — wire rebuild, proportions, remove left 720 cap
