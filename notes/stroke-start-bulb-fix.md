# Stroke start “bulb” fix

Research date: 2026-06-05.

## Symptom

Dark circular blob at stroke start, then a thinner/lighter line — see user screenshot 2026-06-05.

## Root cause

`TryPlaceStrokeOriginDab` + `DirectDrawOutput` zero-length segment duplicate placed an **extra dab on pen-down** before spacing ran.

That dab used **segment speed = 0** (`BuildStamps` zero-length path). Brushes with speed-linked size/opacity (`CurveOption` + `SensorType.Speed`, e.g. `InverseSpeedCurve`: slow → multiplier 1.0) therefore stamped at **maximum dynamics size** while later move dabs used higher speed → smaller multipliers.

Pen-down dab was added for large-brush lag (`notes/pressure-pen-down-lag.md`) but is the wrong tradeoff for stroke quality.

## Proper model (Krita / MyPaint)

1. **No dab on pen-down** — wait until the pointer moves.
2. **Distance accumulator** — first dab when `accumDistance >= spacing` along the first segment (`kis_distance_information.cpp`).
3. **Dynamics use motion context** — speed sensors read filtered/segment velocity, never an artificial zero-velocity pen-down sample.
4. **Optional prewarm** — bake dab cache in `BeginStroke` without painting (not implemented yet; avoids lag without a visible blob).

## Fix

- Remove `TryPlaceStrokeOriginDab` and the duplicate first sample in `DirectDrawOutput`.
- Zero-length segments only run continuous-spray (airbrush) path, not a placement dab.

## Files

- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — remove origin dab
- `src/Floss.App/Processes/Output/DirectDrawOutput.cs` — remove sample duplicate
- `notes/pressure-pen-down-lag.md` — superseded pen-down paint approach
