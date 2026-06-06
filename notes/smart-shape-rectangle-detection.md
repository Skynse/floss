# Smart Shape — rectangle vs hexagon misclassification

## Symptom
Closed rectangular strokes often fit to a regular hexagon instead of a rectangle.

## Root causes

### 1. `ClassifyClosed` uses RDP vertex count as corner count
File: `src/Floss.App/SmartShape/SmartShapeAnalyzer.cs`

```csharp
var corners = FindCorners(pts, simplified);
var nc = corners.Count; // == simplified.Count when no target
if (nc == 4) return FitObb(...);
return new PolygonShape(FindCorners(pts, simplified, Math.Min(nc, 8)));
```

`FindCorners` without `target` sets `n = simplified.Count`. It arc-length-samples points; it does **not** detect geometric corners. Tablet wobble on a rectangle keeps 5–8 RDP points → `PolygonShape` with that many vertices (often 6).

### 2. Auto regularize on closed strokes
File: `src/Floss.App/Processes/Input/SmartShapeBrushInputProcess.cs` `TryDetectHold`

```csharp
if (_strokeClosed)
    shape = SmartShapeRegular.Constrain(shape);
```

CSP: Shift constrains to circle/square/regular polygon. Auto `Constrain` on every closed stroke turns a 6-point polygon into a **regular hexagon**.

## Fix
1. Before polygon fallback, test oriented bounding box edge deviation; if stroke hugs the OBB, return `RectangleShape`.
2. Remove auto `Constrain` on fit; keep `ShiftConstrain` during preview drag only.
