# Pressure / pen-down lag (large brushes)

## Symptom

Noticeable hitch when the pen first touches the tablet, especially with pressure-linked size on large brushes (256–1024px).

## Not the main cause

- **Pressure curve math** (`CurveOption.Compute`, `BrushParameterGraph.Evaluate`) is cheap for typical graphs (few nodes, cubic LUT).
- `BrushParameterGraph.Evaluate` did allocate a node dictionary per call — minor; fixed with a cached index.

## Actual causes

1. **Dab cache miss → `TryBakeLargeCircleDab` / `TryBakeLargeMaskDabCpu`**  
   `CachedDabKey` used **per-stamp `stamp.Size`**. Pressure size dynamics change diameter every event → new key → full LUT bake (~1M+ work for 1024px).

2. **Pressure ramp on first move**  
   Tablet drivers often send pressure `0` on down (`TabletInput` floor `0.01`), then real pressure on the first move → size jumps across several quantized buckets → **multiple bakes in the first ~50ms**.

3. **First segment work** (on first move, not bare down)  
   `CaptureTiles` + `StrokeMaskCached` full-tile recomposite for a huge dirty rect.

4. **Pen-down had no dab**  
   Only `Mouse` duplicated the first queued sample; pen waited for move before any segment ran.

## Fix

1. Large-brush dab cache keyed on **quantized `stamp.Size`** (32/64/128px buckets), not raw per-event size — avoids bake churn without mismatched mask placement.
2. **Prewarm** dab in `BeginStroke` so first paint avoids bake on the UI path.
3. Coarser **hardness quantize** (32 steps) on large dab keys when hardness dynamics are enabled.
4. **Initial dab on zero-length segment** + duplicate first sample for pen/tablet on down.
5. Cache `BrushParameterGraph` node index across `Evaluate` calls.

## Files

- `src/Floss.App/Brushes/Engine/BrushEngine.cs` — cache key, placement, prewarm, pen-down dab
- `src/Floss.App/Brushes/Curves/BrushParameterGraph.cs` — eval index cache
- `src/Floss.App/Processes/Output/DirectDrawOutput.cs` — pen down sample duplicate
