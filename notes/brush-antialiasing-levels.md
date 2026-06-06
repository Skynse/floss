# Brush antialiasing levels

Research date: 2026-06-05. Sources: `ToolPropertiesWindow.cs` (pre-fix UI labels), `BrushEngine.cs`, `notes/krita-round-brush-reference.md`.

## UI levels (four)

| Label | Enum | Engine behavior |
|-------|------|-----------------|
| Pixel Art | `BrushQuality.PixelArt` | No Skia AA; stamp centers snapped to pixel grid |
| Low | `BrushQuality.Low` | `IsAntialias=true`, `SKFilterQuality.Low` |
| Medium | `BrushQuality.Medium` | `IsAntialias=true`, `SKFilterQuality.Medium` |
| High | `BrushQuality.High` | `IsAntialias=true`, `SKFilterQuality.High` |

Hardness remains a separate brush parameter (edge falloff width). Antialiasing controls sampling/filter quality only.

## File compatibility

Brush files v8–14 stored the old 2-value enum (`Low=0`, `High=1`). v15+ stores the 4-value enum. Load remaps `0→Low`, `1→High`.

## Key files

- `src/Floss.App/Brushes/BrushPreset.cs` — `BrushQuality` enum
- `src/Floss.App/Brushes/BrushQualityPolicy.cs` — display names + Skia policy
- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — `CreateStamp` pixel snap, `ActiveStroke` paint
- `src/Floss.App/Windows/ToolPropertiesWindow.cs` — Anti-aliasing section combo
- `src/Floss.App/MainWindow/MainWindow.ToolProperty.cs` — pinned docker combo
