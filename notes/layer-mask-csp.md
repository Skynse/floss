# Layer mask (CSP-style)

## Reference behavior (Clip Studio Paint)

- Layer row shows **two thumbnails**: layer content (left) and **mask** (right).
- Click **mask thumbnail** (blue outline when active) → paint/erase edits the mask, not pixels.
- Mask is **grayscale stored in alpha** (white = full effect, black = hidden). Erasing the mask reveals content below (e.g. adjustment on layers underneath).
- Toolbar mask control: create mask if missing, then toggle mask vs layer edit target.

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
| `MainWindow/MainWindow.LayerPanel.cs` | Dual thumbnails, mask toolbar button |

## Implementation notes

- `DrawingLayer.ActivePixels` → mask buffer when `IsMaskEditing`.
- Brush writes use `ActivePixels`; after composite, mask pixels sync RGB from alpha for thumbnail preview.
- Adjustment layers: apply to scratch, blend to dst with `maskAlpha * opacity`.
