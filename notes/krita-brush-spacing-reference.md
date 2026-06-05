# Krita brush spacing reference (for Floss)

Research date: 2026-06-04. Source: `/home/neckles/projects/krita`.

## Krita model

### UI (`libs/widgets/kis_spacing_selection_widget.cpp`)

Single slider + Auto checkbox. The slider **always** drives spacing:

| Mode | Slider controls | Stored values |
|------|-----------------|---------------|
| **Auto ON** | `autoSpacingCoeff` | `spacing()` returns `0.1` placeholder; coeff = slider value |
| **Auto OFF** | `spacing` fraction | `autoSpacingCoeff()` returns `1.0`; spacing = slider value |

Toggling Auto resets slider (auto → 1.0, off → restores previous manual value).

### Pixel distance (`libs/image/brushengine/kis_paintop_utils.cpp`)

```cpp
// Manual (auto OFF, isotropic):
significantDimension = qMax(dabWidth, dabHeight) * spacingVal;

// Auto (auto ON, isotropic):
significantDimension = calcAutoSpacing(significantDimension, autoSpacingCoeff);
// calcAutoSpacing(value, coeff) = coeff * (value < 1 ? value : sqrt(value))
```

- `spacingVal` / `autoSpacingCoeff` come from brush resource (`kis_brush.cpp`).
- Min pixel gap: `MIN_DISTANCE_SPACING = 0.5` (`kis_distance_information.cpp`).
- **Flow does not scale spacing** in Krita.
- Dynamics: optional `KisSpacingOption` multiplies spacing via `extraScale` (`kis_paintop_plugin_utils.h`).

### Dab placement (`kis_distance_information.cpp`)

- Accumulates distance along segment (`accumDistance`).
- Places dab when `accumDistance >= spacing`.
- `paintLine()` loops `getNextPointPosition()` until no more dabs fit in segment.
- No “collapse to single stamp if move < brush size” shortcut in distance logic.

## Floss model (current HEAD after reset)

### `BrushSpacing.EffectiveDistance` (`src/Floss.App/Brushes/BrushSpacing.cs`)

| Condition | Formula |
|-----------|---------|
| Auto ON (default) | `CalcAutoSpacing(stampSize, AutoSpacingCoeff) * spacingMul` |
| Auto OFF | `stampSize * GapFraction(GapMode, Spacing) * spacingMul` |
| Always | `*= sqrt(flow)` |

`GapFraction`:
- `Fixed` → `Spacing` slider value
- `Normal` → **0.25** (ignores slider)
- `Narrow` → **0.12**
- `Wide` → **0.40**

Most built-in presets use `GapMode.Normal` (`BrushPreset.cs`).

### UI wiring (`MainWindow.axaml.cs`)

```csharp
WireBrushSlider(_spacingSlider, p => p with { Spacing = _spacingSlider.Value });
```

- Slider only updates `Spacing`.
- **`AutoSpacingCoeff` is never updated from UI** (only serialized default 1.0).
- Auto toggle only sets `AutoSpacingActive`.

### Result: spacing slider is effectively dead

Typical user state:
- `AutoSpacingActive = true` (default)
- `GapMode = Normal` (preset default)
- User moves spacing slider → updates `Spacing` field
- Engine uses `CalcAutoSpacing(size, AutoSpacingCoeff=1.0)` → **slider ignored**
- Toggling Auto off still uses `GapMode.Normal` → fixed 25% → **slider still ignored**

Only works if user manually has `GapMode.Fixed` **and** `AutoSpacingActive=false` — neither is exposed/set by UI.

### Tests are inconsistent with runtime

`tests/Floss.App.Tests/BrushSpacingTests.cs` assumes CSP GapMode behavior but does not set `AutoSpacingActive = false`. All 3 tests **fail** on current HEAD:

- `NormalGap_UsesQuarterDiameter`: expects 100px, gets 20px (`sqrt(400)` with auto coeff 1.0)
- `NormalGap_IsWiderThanFixedNarrow`: both paths use auto → no difference
- `BrushEngine_NormalGapUsesFewerStampsThanFixedNarrow`: same stamp counts

## Root cause of user report

“10% spacing looks the same as 0%” — expected with current wiring:
1. Auto on → spacing is `sqrt(brushSize)` regardless of slider.
2. Auto off → Normal gap mode → always 25% of diameter regardless of slider.
3. Changing slider 0.02 → 0.10 does not reach `EffectiveDistance` in normal use.

## Recommended fix (Krita-aligned, minimal scope)

### 1. `BrushSpacing.EffectiveDistance`

Match Krita’s two-mode formula:

```csharp
// Floss UI labels spacing as % of diameter (not Krita raw auto coeff).
if (brush.AutoSpacingActive)
{
    var autoCoeff = spacingVal * Sqrt(brush.Size);
    baseSpacing = CalcAutoSpacing(stampSize, autoCoeff) * spacingMultiplier;
}
else
    baseSpacing = stampSize * spacingVal * spacingMultiplier;
```

At `stampSize == brush.Size`, auto and manual both yield `spacingVal * brush.Size` (e.g. 2% @ 1024px → ~20px, not 0.64px).

- Use **`Spacing` as the single user-facing value** (manual fraction or auto coeff depending on toggle).
- Drop runtime dependence on `GapMode` (keep enum only for preset import/defaults).
- Consider removing `sqrt(flow)` spacing scale unless there is a documented Floss-specific reason (not in Krita).

### 2. UI wiring

Mirror Krita widget semantics:

- Spacing slider always writes `Spacing`.
- Auto toggle writes `AutoSpacingActive` only (coeff already in `Spacing`).
- Optional: save/restore manual spacing on auto toggle (Krita UX).

Deprecate or stop persisting separate `AutoSpacingCoeff` unless needed for backward-compatible brush files (migrate: if `AutoSpacingActive`, treat `AutoSpacingCoeff` as fallback when `Spacing` unset).

### 3. Preset migration

When loading presets with `GapMode != Fixed`, map to `Spacing` once:

| GapMode | Initial Spacing |
|---------|-----------------|
| Normal | 0.25 |
| Narrow | 0.12 |
| Wide | 0.40 |
| Fixed | existing `Spacing` |

Then runtime ignores `GapMode`.

### 4. Tests

Rewrite `BrushSpacingTests` for Krita semantics:

- Auto on: `sqrt(size) * coeff`
- Auto off: `size * spacing`
- Engine integration: low spacing → more stamps than high spacing (same brush, auto off)

### 5. Out of scope for spacing fix

- Large brush square mask / performance — separate Krita research needed before touching `BrushEngine` raster paths again.
- `ShouldCollapseToSingleStamp` — Floss-specific; review separately (can hide spacing differences on short segments).

## Key Krita file locations

| File | Purpose |
|------|---------|
| `libs/image/brushengine/kis_paintop_utils.cpp` | `effectiveSpacing`, `calcAutoSpacing` |
| `libs/widgets/kis_spacing_selection_widget.cpp` | Slider ↔ auto/manual mapping |
| `libs/image/kis_distance_information.cpp` | Distance accumulator, dab placement |
| `libs/brush/kis_brush.cpp` | `spacing()`, `autoSpacingCoeff()` storage |
| `plugins/paintops/libpaintop/kis_brush_based_paintop.cpp` | Calls `effectiveSpacing` with brush values |

## Floss file locations

| File | Purpose |
|------|---------|
| `src/Floss.App/Brushes/BrushSpacing.cs` | `EffectiveDistance` — **fix here** |
| `src/Floss.App/Brushes/Engine/BrushEngine.cs` | Stamp loop uses `StampSpacing` → `EffectiveDistance` |
| `src/Floss.App/MainWindow/MainWindow.axaml.cs` | Spacing slider wiring |
| `src/Floss.App/Brushes/BrushPreset.cs` | `AutoSpacingActive`, `GapMode` defaults |
| `tests/Floss.App.Tests/BrushSpacingTests.cs` | Update after fix |
