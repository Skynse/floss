# Floss Avalonia UI Reference

## Layout structure
```
Shell: Grid rows="22,*"
├── MenuBar (row 0, 22px)
└── Root Grid (row 1): columns="Auto,*,Auto"
    ├── Left Panel (col 0, 48px): tool rail + color well (BgSidebar)
    ├── Center (col 1): rows="26,18,*,20"
    │   ├── TabBar (row 0, 26px)
    │   ├── StatusBar (row 1, 18px): canvas info (zoom/size)
    │   ├── Canvas Viewport (row 2, *): checkerboard, canvas frame
    │   └── Footer (row 3, 20px): active tool info + color swatch
    └── Right Panel (col 2, 250px, min 200, max 440)
        └── Dockable tabs: Layers | Color | Properties
        └── Brush size palette lives in Properties tab (egui port)
```

## AppColors
```
Bg0       = "#181a1f"  (deepest bg)
Bg1       = "#202227"  (panel/toolbar bg)
Bg2       = "#282a30"  (elevated surface)
Bg3       = "#343640"  (hover/active surface)
BgSidebar = "#1c1e23"  (sidebar rail)
Stroke    = "#363840"  (borders)
TextPrimary   = "#f0f2f5"
TextSecondary = "#d0d3d8"
TextMuted     = "#90959c"
Accent     = "#0078f2"
AccentSoft = "#0a4f9f"
Success    = "#3fb950"
Warning    = "#d29922"
Danger     = "#da3633"
```

## Layer panel (BuildLayersSection)
Grid rows="Auto, Auto, Auto, *, Auto"
├── Row 0: nameRow (DockPanel: kebab menu + TextBox)
├── Row 1: blendOpRow (Grid columns="*,4,*": BlendMode combo + Opacity slider)
├── Row 2: toggleRow (StackPanel horizontal): Lock, AlphaLock, Clip, Mask, Ref
├── Row 3: layerListBox (*) — virtualized ListBox
└── Row 4: ctrlRow (WrapPanel): Add, Folder, Dup, Up, Down, Delete

## LayerRow (BuildLayerRow)
Border: background (active=Accent, selected=#2f3a48, default=Bg2)
        border (active=Accent, selected=#485566, default=Stroke)
        corner=4, padding=3/2, indent margin=indent*8
Grid columns="3,16,20,48,*"
├── Col 0: clipStrip (3px) — pink if clipping
├── Col 1: disclosureBtn (16px) — chevron for groups
├── Col 2: visBtn (20px) — eye icon (on=#8aa6cc, off=#5b5b5b)
├── Col 3: previewHost (48px) — Image with checkerboard bg + mask badge "M"
└── Col 4: nameHost (*) — StackPanel: name text + status text
    Status text = "opacity% BlendMode Lock Alpha Ref Clip Paper"

## Layer status text format
- Group: "N layers" (count children)
- Adjustment: "opacity% Kind"
- Normal: "opacity% BlendMode Lock Alpha Ref Clip Paper"

## Color section
- StackPanel: HsvColorPicker (130px) + hex input + swatches
- Mode toggle via kebab menu: wheel / HSV / RGB

## Brush size palette
- 7-column Grid of size buttons with log-scale dot visuals

## Footer
- Border height=20, bg=Bg1: status text (tool info + brush size + color)
