# Layer mask (CSP-style)

## Reference behavior (Clip Studio Paint)

- Layer row shows a **second thumbnail only when a mask exists** (content | mask). No placeholder button on the row.
- **Toolbar** mask button (stacked-rect icon): click = create mask or toggle mask edit; **right-click** = delete / enable / apply.
- Click **mask thumbnail** (green outline when active) → paint/erase edits the mask, not pixels.
- Mask is **grayscale stored in alpha** (white = full effect, black = hidden).

## Floss files

| File | Role |
|------|------|
| `Document/DrawingLayer.cs` | `MaskPixels`, `ActivePixels`, `CreateMask`, thumbnails |
| `Document/DrawingDocument.cs` | `CreateLayerMask`, `ToggleLayerMaskEditing`, history |
| `Brushes/Engine/BrushEngine.cs` | Strokes must target `ActivePixels`; mask stamp = alpha channel |
| `Processes/Output/DirectDrawOutput.cs` | Live stroke / capture on paint buffer |
| `Canvas/Compositing/LayerCompositorPixelOps.cs` | `hasMask` → multiply layer alpha by mask A |
| `Canvas/Compositing/AdjustmentLayerProcessor.cs` | `ApplyWithLayer` gates adjustment by mask |
| `Canvas/Compositing/LayerProjectionPlane.cs` | Call `ApplyWithLayer` for adjustment layers |
| `Icons.cs` | `LayerMask` toolbar icon |
| `MainWindow/MainWindow.LayerPanel.cs` | Mask thumb when `HasMask`; toolbar + context menu |

## Implementation notes

- `DrawingLayer.ActivePixels` → mask buffer when `IsMaskEditing`.
- Brush writes use `ActivePixels`; after composite, mask pixels sync RGB from alpha for thumbnail preview.
- Adjustment layers: LUT in-place on dst, gated by mask tile alpha × layer opacity (`ApplyWithMask`).
- **Save/load**: `layers/layer{N}.mask.bgra` in `.floss` manifest (`maskPath`, `isMaskVisible`).
- **History**: mask strokes use `LayerTileHistoryState` with `MaskMutation`; create/delete use `LayerMaskTilesHistoryState`.
