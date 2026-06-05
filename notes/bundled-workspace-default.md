# Bundled workspace default layout

## Shipped file

- `src/Floss.App/Assets/workspace-default.json` — Avalonia resource (`avares://Floss/Assets/workspace-default.json`)
- Loaded by `WorkspaceLayout.CreateDefault()` via `BundledWorkspaceLayouts.TryLoad`
- Seeded as Workspace preset **`default`** on first run (`AppConfig.EnsureBundledWorkspacePresets`)
- **Workspace → Reset Layout** restores that preset (or `CreateDefault()` if missing)

## Updating the bundled layout

1. Arrange dockers in the app.
2. **Workspace → Save Preset…** → name `default` (or export from `~/.local/share/Floss/config.json` key `WorkspacePresets.default`).
3. Copy JSON into `src/Floss.App/Assets/workspace-default.json`.
4. Rebuild.

```bash
jq '.WorkspacePresets.default' ~/.local/share/Floss/config.json > src/Floss.App/Assets/workspace-default.json
```

## Normalize

`DockTabStacks.Compact` is **not** applied to columns that use the explicit `Rows` model, so shipped row/tab placement is preserved.
