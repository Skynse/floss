# UI visual refresh

## Chrome palette (2026)

Photoshop-style neutral dark tokens — see `notes/photoshop-ui-reference.md`.  
Single source: `Config/AppColors.cs` + `Styles/Theme.axaml` + `App.axaml` control styles.

## Cursor (viewport)

- `ViewportCursorOverlay` must **stretch** to fill the workspace grid cell or it renders at 0×0.
- Cursor position: `GetToolCursorViewportPosition` translates `_pointerPos` from canvas → viewport (transform-aware).
- Fallback: draw cursor in `DrawingCanvas.Render` again for the canvas area.
- Hide OS cursor on **TopLevel** + capture target on press/stroke (`ApplyViewportOsCursorHidden`).
- `Workspace_OnPointerPressed` syncs pointer before router dispatch.

## Brush preset rows

- Layout: preview row (stretch) + name row below (no overlay on stroke).
- Selection: 2px accent border, `RenderOptions` aliased.
- Categories: horizontal tab strip above preset list (not left sidebar).

## Docker tabs (layout v3)

- Left: tab group `brush` | `Brush` (tool-properties)
- Right: tab group `Color` (color, sliders, layer color) | `Layers`

EOF
