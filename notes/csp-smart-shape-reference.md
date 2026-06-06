# CSP Smart Shape — Reference & Floss implementation status

Sources:
- [CSP Tips #11934](https://tips.clip-studio.com/en-us/articles/11934)
- User screenshots (2026-06)
- Port: `krita-smart-shape/smartshape/`

---

## CSP flow

```
Draw (real brush preview)
  │ hold still (pen down)
  ▼
Adjusting — thin outline; drag scale/rotate; Shift → perfect circle/square/regular polygon
  │ pointer up
  ▼
Launcher — outline + bar (detected type + quick picks + … overflow)
  │ tap Edit / tap type to refit / tap bar
  ▼
Gizmo — red outline, blue bbox, anchors + transform handles
  │ tap outside bbox
  ▼
Committed — brush-engine rasterize, single undo, selection clip
```

---

## Floss files

| Area | Path |
|------|------|
| Fit kinds (CSP launcher types) | `SmartShapeFitKind.cs` |
| Auto + forced fitting | `SmartShapeAnalyzer.cs`, `SmartShapeFitter.cs` |
| Shift regular constraint | `SmartShapeRegular.cs` |
| State machine | `SmartShapeBrushInputProcess.cs` |
| Transform gizmo | `SmartShapeGizmo.cs` |
| Overlay | `SmartShapeOverlay.cs` |
| Launcher UI | `MainWindow.SmartShape.cs` |
| Commit + selection clip | `SmartShapeBrushOutput.cs` |
| Tests | `tests/Floss.App.Tests/SmartShape/` |

---

## CSP launcher shape types (all implemented)

| CSP label | `SmartShapeFitKind` |
|-----------|---------------------|
| Straight line | `StraightLine` |
| Polyline | `Polyline` |
| Curve | `Curve` (≤4 Bézier segments) |
| Continuous curve | `ContinuousCurve` (≤12 segments) |
| Triangle | `Triangle` |
| Equilateral triangle | `EquilateralTriangle` |
| Quadrilateral | `Quadrilateral` |
| Rectangle | `Rectangle` |
| Square | `Square` |
| Ellipse | `Ellipse` |
| Circle | `Circle` |
| Polygon | `Polygon` |
| Regular polygon | `RegularPolygon` |

Primary quick picks (open stroke): Curve · Polyline · Line  
Primary quick picks (closed stroke): Circle · Ellipse · Rectangle  
Overflow `…` menu: remaining types.

Refit always uses stored `_rawStroke` samples (not current gizmo transform).

---

## Implementation checklist

- [x] Full CSP shape-type fitting via `SmartShapeFitter.Fit`
- [x] Launcher with primary slots + overflow context menu
- [x] Clickable launcher (tunnel router bypass + `IsOverCanvasUi`)
- [x] Launcher anchored under fitted shape (updates on pan/zoom)
- [x] Launcher → Gizmo via Edit / tap detected type
- [x] Dual gizmo on curves (anchors + transform bbox)
- [x] Point-only gizmo (no Bézier tangent handles); neighbor angle influence on polyline/polygon vertices
- [x] Transform-aligned gizmo frame (oriented bbox, local hit-test, rotated overlay; Ctrl = free aspect, default uniform scale)
- [x] Two-step undo on commit (smart shape → raw stroke → clear) via offscreen `SmartShapeCommitRasterizer` + `PushLayerTileHistoryPatches`
- [x] Polyline shape + vertex editing
- [x] Shift constrain while adjusting and gizmo drag
- [x] Outside-bbox tap commits
- [x] Modifier keys don't abort session (`IsSmartShapeEditActive`)
- [x] Selection clip on commit
- [x] Native vector overlay (ellipse/rect/Bézier)
- [ ] Starting/ending taper on commit (brush preset dynamics)
- [ ] Vector layer output (CSP only; Floss raster-only)

Reference this file before changing smart-shape behavior.
