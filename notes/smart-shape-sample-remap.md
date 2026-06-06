# Smart Shape — preserve original stroke dynamics on fit

## Problem
Fitted shapes used `AvgPressure` for every raster sample (`SmartShapePolyline.ToDocumentSamples`), flattening light-to-heavy variation and other pen state.

## Approach
Remap the stored raw `CanvasInputSample` chain onto the fitted path by **arc-length fraction**:

1. Build cumulative arc length for raw stroke and fitted polyline.
2. For closed strokes, phase-align raw arc zero to the raw sample closest to the shape path start.
3. Resample the fitted path at the same count as the raw stroke (uniform arc-length fractions), not only at sparse shape vertices.
4. At each resample point, interpolate the raw sample at the matching arc position (pressure, tilt, twist, time, pointer metadata).
5. Replace only X/Y with the fitted geometry (layer-local for rasterization).

Brush engine already lerps those fields between consecutive samples in `RasterizeSegments`.

## Files
- `SmartShapeSampleRemap.cs` — arc tables + sampling
- `SmartShapePolyline.cs` — `ToDocumentSamples(shape, layer, rawSamples, strokeClosed)`
- `SmartShapeBrushInputProcess.cs` — store full `CanvasInputSample` list
- `SmartShapeCommitInput.RawSamples` replaces `RawStroke` / `RawPressures` / `AvgPressure`
