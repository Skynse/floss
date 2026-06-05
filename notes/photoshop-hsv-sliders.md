# Photoshop-style HSV channel sliders

Reference: Photoshop Color panel H/S/B rows (user screenshot 2026).

## Layout per row

| Column | Width | Content |
|--------|-------|---------|
| Label | 18px | `H`, `S`, `B` — muted, right-aligned, 10px gap before track |
| Track | `*` | 11px gradient bar + marker below, 10px gap before value field |
| Value | 58px | Dark `TextBox`, suffix `°` or `%` |

Rows stacked with 10px vertical spacing; panel margin 10px.

## Interaction

- **Click** anywhere on track or marker band → value jumps to that position.
- **Drag** on same hit area while captured.
- **Type** in value box (Enter / lost focus applies).

## Marker

- White filled triangle, tip up, touching bottom of gradient bar.
- Dark stroke for contrast on light gradients.

## Implementation

- `Controls/ScrubSlider.cs` — shared track + marker (see `notes/scrub-slider.md`)
- `Controls/HsvSliderRow.cs` — label + scrub + numeric field; gradients via `TrackBackground`
- `MainWindow/MainWindow.ColorSliders.cs` — three rows, dynamic gradients via `HsvToRgb` / `RgbToHsv` in `MainWindow.Color.cs`

## Gradients

| Channel | Track gradient |
|---------|----------------|
| H | Rainbow 0°–360° |
| S | Gray (s=0) → full color at current H,V |
| B | Black → full color at current H,S |
