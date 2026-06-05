# Photoshop 2023 dark UI reference (Floss chrome)

Source: Photoshop 2023 macOS dark workspace (user screenshot). Used for **component style only** — not dock layout.

## Principles

- **Flat panels**: depth from gray steps + 1px borders, not shadows or heavy gradients.
- **Muted accent**: blue only for focus rings, primary buttons, and thin active indicators — not full-row fills.
- **Tabs**: active tab matches panel body; inactive tabs sit on a slightly darker strip; no loud uppercase labels.
- **Selection**: light gray row highlight (`#454545`), optional thin accent edge — not solid `#0078f2` blocks.

## Token map (Floss `AppColors`)

| Role | Hex | Usage |
|------|-----|--------|
| Bg0 | `#1e1e1e` | Window chrome, canvas surround |
| Bg1 | `#323232` | Panel body, active tab, docker content |
| Bg2 | `#2b2b2b` | Tab strip, recessed areas, inputs |
| Bg3 | `#404040` | Hover, pressed overlay |
| BgSidebar | `#2a2a2a` | Tool rail |
| Stroke | `#474747` | Panel dividers, control borders |
| TextPrimary | `#e8e8e8` | Active labels |
| TextSecondary | `#b8b8b8` | Body UI text |
| TextMuted | `#8a8a8a` | Tab inactive, hints |
| Accent | `#3d9eff` | Focus, primary actions, thin indicators |
| AccentSoft | `#3d3d3d` | List/tab selection fill |
| SelectionBg | `#454545` | Active list row |
| SelectionBorder | `#5a9fd8` | Active row left edge |

## Files updated for chrome

- `Config/AppColors.cs`, `Styles/Theme.axaml`, `App.axaml`
- `Docking/DockTabGroup.cs`
- `MainWindow/MainWindow.BrushLibrary.cs`, `MainWindow.LayerPanel.cs`, `MainWindow.axaml.cs`
- `Controls/DockDropOverlay.cs`

## Refresh bundled layout

Unrelated to colors — see `notes/bundled-workspace-default.md`.
