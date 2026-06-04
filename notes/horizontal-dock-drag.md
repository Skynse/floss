# Dock columns beside the canvas (left / right stacks)

## What users want

Multiple **vertical dock columns** beside the canvas (Photoshop / CSP style), not panels squashed side-by-side inside one column.

Example left side:

```text
[ outer column: tools ] | [ inner column: brushes ] | [ canvas ]
```

The red drop zone in screenshots is the **inner** edge: between the existing left stack and the canvas.

## Model

- `WorkspaceLayout.LeftColumns` — list of `DockColumnLayout`, same idea as `RightColumns`.
- Column index: `DockColumnIndices.Left(i)` → `-(i+1)`; right `0,1,…`; bottom `DockColumnIndices.Bottom` (`-100`).
- `LeftDockSplit` — fraction between two left columns (like `RightDockSplit`).

## UI

- `BuildLeftDockColumn()` builds `_leftDockerHostGrid` with star columns + splitters (mirrors `BuildRightPanel()`).
- `LeftRailWidth` — total width of all left columns.

## Drag-drop (`MainWindow.DockDrag.cs`)

| Target | Effect |
|--------|--------|
| Strip left of canvas | `InsertDockColumn` at `LeftColumns.Count` (new column next to canvas) |
| Strip right of canvas (before right dock) | `InsertDockColumn` at right index `0` |
| Left/right edge of panel (~35% width) | New column at `leftIndex` or `leftIndex+1` |
| Between left column splitters | Insert column at split index |
| Top/bottom of panel | `InsertRow` within that column (unchanged) |
| Tab strip | `MergeTab` (unchanged) |

`DockLayoutOps.ApplyInsertDockColumn` inserts a new column with the dragged panel as its first row.

## Not in scope

- Within-row horizontal `DockOrientation.Horizontal` is no longer created by drag (layout may still render old saved rows).

## Files

- `Docking/DockColumnIndices.cs`
- `Docking/WorkspaceLayout.cs` — `LeftColumns`, `LeftColumnImport`, `LeftDockSplit`
- `Docking/DockLayoutOps.cs` — `ApplyInsertDockColumn`
- `MainWindow/MainWindow.axaml.cs` — multi-column left build
- `MainWindow/MainWindow.DockDrag.cs` — column edge targets
