# Always-tabbed dock model

## Intent

Vertical dock rows behave like Photoshop/Krita: every panel sits in a **tab strip**, even when alone. Drag-drop should **merge into tabs** when over a row’s body, not only a thin header band, and should not prefer **new canvas columns** when the pointer is over an existing dock column.

## Key files

| File | Role |
|------|------|
| `Docking/WorkspaceLayout.cs` | `DockRowLayout.IsTabGroup` = vertical + `PanelIds.Count >= 1` |
| `Docking/DockLayoutOps.cs` | `CompactTabGroups` no-op (do not collapse single tabs) |
| `MainWindow/MainWindow.axaml.cs` | `BuildDockColumn` always `DockTabGroup` for vertical rows |
| `MainWindow/MainWindow.DockDrag.cs` | Metrics + `ResolveDropTarget` priority |

## Cross-side drag (left ↔ right)

- Layout still stores `LeftColumns` / `RightColumns`, but drops use encoded `DockColumnIndices` (left negative, right ≥ 0).
- While dragging, **window-level** `PointerMoved` / `PointerReleased` handlers track the pointer across rails (tab capture alone does not reach the opposite side).
- `DeduplicateColumns` keeps **right** over left over bottom when a panel is duplicated — so a brush dragged to the right rail is not pulled back on `Normalize`.

## Drag rules (`ResolveDropTarget`)

1. **Row hit** (vertical tab group): middle ~76% height → `MergeTab`; top/bottom slivers → `InsertRow`.
2. **Panel hit** (horizontal split rows only): unchanged.
3. **Column edge** (new/split column): only when pointer is **not** inside any dock column bounds.
4. **Fallback**: insert row at column top/bottom edge.

## UI

- Solo vertical panels: `BuildTabGroupRow` + `WireDockTabGroupDrag` (no full-width `PanelSection` header).
- Horizontal rows: side-by-side sections with headers (unchanged).
