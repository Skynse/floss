# Krita brush stabilizer reference (for Floss)

Research date: 2026-06-05. Source: `/home/neckles/projects/krita`.

Related: `notes/krita-brush-resize-stabilizer.md`.

## Model chosen: **Stabilizer** (not Simple/Weighted/Pixel)

Krita `KisSmoothingOptions::STABILIZER` in `kis_tool_freehand_helper.cpp`.

User asked for one mode that works — Floss maps brush **Smoothing** slider (0–1) to this.

## Algorithm

### Stroke start (`stabilizerStart`)

```776:801:../krita/libs/ui/tool/kis_tool_freehand_helper.cpp
int sampleSize = qMax(3, qRound(effectiveSmoothnessDistance(speed)));
// Prefill deque with first sample repeated sampleSize times
m_stabilizerPollTimer.start(cfg.stabilizerSampleSize()); // 15ms default
m_stabilizedSampler.addEvent(firstPaintInfo);
```

- Deque length = `max(3, round(smoothnessDistance))`
- Default `lineSmoothingDistanceMax` = **50** (`kis_config.cc`)
- Poll interval resamples buffered events (`kis_stabilized_events_sampler.cpp`)

### Each paint step (`stabilizerPollAndPaint`)

1. Iterator over time-interpolated samples since last poll
2. `newInfo = getStabilizedPaintInfo(deque, sampledInfo)`
3. `paintLine(previous, newInfo)`
4. `deque.dequeue(); deque.enqueue(sampledInfo)`

### Uniform position average (`getStabilizedPaintInfo`)

```822:846:../krita/libs/ui/tool/kis_tool_freehand_helper.cpp
// Start from latest sample; mix each older deque entry:
// k = (i-1)/i; result = mix(result, *it, k)  [position only unless stabilizeSensors]
```

Incremental form equivalent to uniform average over deque + latest.

### Speed-adaptive window (`effectiveSmoothnessDistance`)

```465:478:../krita/libs/ui/tool/kis_tool_freehand_helper.cpp
return zoomCoeff * ((1-speed)*distanceMax + speed*distanceMin);
// Stabilizer inverts scalable-distance flag vs other smoothers
```

Fast stroke → smaller deque (less lag). Slow → larger (more stabilization).

## Floss bugs fixed

| Bug | Effect |
|-----|--------|
| `ApplyBrushToStrokeInputs` only updated `_brushTool` / `_eraserTool` | Active `SmartShapeBrushInputProcess` kept `Stabilization=0` — **no visible stabilization** |
| `minDist` gate (0.05–0.15px) on smoothed output | Averaged point moves too little per event → **most moves dropped** |
| `maxCount=24`, batch mean over growing list | Weaker than Krita; wrong mix order vs deque+latest |

## Floss implementation

- `BrushStrokeInputProcess`: Krita deque + `getStabilizedPaintInfo`, `max(3, round(smoothing*50))`, no minDist when smoothing > 0
- `DrawingCanvas.ApplyBrushToStrokeInputs`: apply to **active** tool + alternate eyedropper input

## Key files

| Krita | Floss |
|-------|-------|
| `kis_tool_freehand_helper.cpp` | `Processes/Input/BrushStrokeInputProcess.cs` |
| `kis_stabilized_events_sampler.cpp` | (not ported — per-move instead of poll timer) |
| `kis_smoothing_options.cpp` | `BrushPreset.Smoothing`, `SpeedAdaptiveStabilizer` |
