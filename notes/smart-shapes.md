# Smart Shapes — Design Doc

Maps [krita-smart-shape](file:///home/neckles/projects/krita-smart-shape) (CSP Smart Shape v5+ behavior) onto Floss's `ToolController` / `CompositeTool` / `DrawingDocument` history.

**Reference files (krita-smart-shape):**

| File | Role |
|------|------|
| `smartshape/event_filter.py` | State machine: idle → drawing → adjusting → gizmo → commit |
| `smartshape/stroke_analyzer.py` | RDP, straightness, ellipse PCA, Schneider Béziers, corners |
| `smartshape/overlay.py` | Preview stroke + gizmo handles |
| `smartshape/layer_painter.py` | Commit via brush-engine `paintLine` or QPainter fallback |
| `smartshape/docker.py` | Hold time / hold radius settings |

**Floss files (integration targets):**

| Area | Path |
|------|------|
| Tool dispatch | `src/Floss.App/Tools/Core/ToolController.cs` |
| Input/output wiring | `src/Floss.App/Processes/CompositeTool.cs` |
| Brush stroke (stabilizer) | `src/Floss.App/Processes/Input/BrushStrokeInputProcess.cs` |
| Brush commit + tile undo | `src/Floss.App/Processes/Output/DirectDrawOutput.cs` |
| Shape raster commit | `src/Floss.App/Processes/Output/StrokeOutput.cs` |
| History | `src/Floss.App/Document/DrawingDocument.cs` (`CommitLayerTileMutation`, `CommitStroke`) |
| Preview-without-history pattern | `DrawingDocument.PreviewActiveLayerOpacity` / `CommitActiveLayerOpacityScrub` |
| Global app settings | `src/Floss.App/Config/AppConfig.cs`, `src/Floss.App/Windows/SettingsWindow.cs` |
| Overlay hook | `ITool.RenderOverlay` in `src/Floss.App/Tools/Core/ITool.cs` |

---

## CSP behavior (target UX)

1. User draws a rough stroke with the brush (pen down, moving).
2. User holds still within a radius for a configurable time.
3. Stroke is analyzed; a geometric shape is fitted and shown as an overlay.
4. While still holding (adjusting phase): drag away from center scales; drag around center rotates.
5. On release: gizmo handles appear (move, rotate, corner scale, curve anchors for Bézier shapes).
6. User edits via gizmo; commits with a button or double-click.
7. Final shape is rasterized onto the active layer using the **current brush** (texture, dynamics, pressure), not a flat vector stroke.

Krita's plugin approximates this but **snapshots layer pixels on press** and restores them before commit because Krita has no "defer stroke to layer" API. Floss can do better.

---

## Floss today (gaps)

| Capability | Status |
|------------|--------|
| Brush stabilizer | `BrushStrokeInputProcess` |
| Straight-line aux mode | `ToolAuxOperationType.StraightLine` + `StraightLineOverlay` (separate feature; not how CSP Smart Shape works) |
| Shape tools (rect/ellipse/line) | `RectInputProcess` + `StrokeOutput` |
| Polyline + Enter commit | `PolylineInputProcess`, `CompositeTool.CanCommitFromClick` |
| Real tile-based undo | `LayerTileHistoryState` via `CommitLayerTileMutation` |
| Stroke grouping for timelapse | `DrawingDocument.CommitStroke()` |
| Hold-to-detect | **missing** |
| Shape fitting / analyzer | **missing** |
| Gizmo overlay | **missing** (TransformTool overlay is layer transform, not shape edit) |
| Atomic smart-shape commit | **missing** |

---

## Recommended integration: global app feature

CSP Smart Shape is **not** a brush setting and **not** a keyboard/modifier aux mode. It is a **built-in application feature** that applies to every brush stroke regardless of which brush texture or preset is active. The user toggles it in app settings:

| CSP setting | Floss equivalent |
|-------------|------------------|
| **Hold to create figures** (checkbox) | `AppConfig.SmartShapeEnabled` |
| **Length of long-press: 0.70 seconds** | `AppConfig.SmartShapeHoldSeconds` (default `0.70`) |
| **Show Smart Shape launcher** (checkbox) | `AppConfig.SmartShapeShowLauncher` |

Store in `AppConfig`, expose in `SettingsWindow` (new "Drawing" or "Tools" page section), persisted to config JSON on save. **Not** on `BrushPreset`, `ToolPreset`, or `ToolAuxOperationType`.

**Why not a separate top-level tool:**
- CSP does not require switching tools. User draws normally; hold-still triggers shape detection.
- Final rasterization still uses whatever brush is currently active (texture, dynamics, size).

**Why not per-brush:**
- CSP applies uniformly across all brushes. A round brush and a texture brush both get smart-shape behavior when the global toggle is on.

### Wiring sketch

When `App.Config.SmartShapeEnabled` is true, all **DirectDraw** tools (pen, brush, eraser, smudge) use a hybrid input/output pair:

```
CompositeTool (any DirectDraw tool — unchanged tool selection)
  Input:  SmartShapeBrushInputProcess   // wraps normal stroke collection + hold detection
  Output: SmartShapeBrushOutput         // delegates to DirectDrawOutput until hold fires;
                                        // then overlay-only preview; commits via brush engine on confirm
```

When disabled, existing `BrushStrokeInputProcess` + `DirectDrawOutput` behavior is unchanged (zero state-machine overhead).

`SmartShapeBrushInputProcess` is the key piece: it **starts as a normal brush stroke** (samples, stabilizer), watches for hold-still, and only then **stops forwarding paint preview** and enters the smart-shape state machine. No modifier, no tool swap, no preset field.

Settings are read from `App.Config` (or injected via `ToolContext` if tests need overrides):

```csharp
// AppConfig.cs — new fields
public bool SmartShapeEnabled { get; set; } = true;
public double SmartShapeHoldSeconds { get; set; } = 0.70;
public bool SmartShapeShowLauncher { get; set; } = true;
public double SmartShapeHoldRadiusPx { get; set; } = 8;  // advanced; not shown in CSP screenshot
```

---

## State machine

Port of `event_filter.py` states:

```
Idle
  │ pointer down
  ▼
Drawing          collect samples; optional faint stroke preview on overlay only
  │ hold timer fires (still within hold_radius of last move)
  ▼
Adjusting        fitted shape overlay; drag scales/rotates from center
  │ pointer up
  ├─ SmartShapeShowLauncher → Launcher (shape-type bar) → Edit → Gizmo
  └─ launcher off → Gizmo directly
  ▼
Gizmo            handles: MOVE, ROT, TL/TR/BL/BR, TC/BC/ML/MR, curve anchors
  │ commit (Enter / double-click / toolbar button)
  ▼
Committed        rasterize shape with brush engine → single history entry
  │ cancel (Esc / click away)
  ▼
Idle
```

### Transitions (match reference)

| From | Event | To | Action |
|------|-------|-----|--------|
| Drawing | move ≥ hold_radius | Drawing | restart hold timer |
| Drawing | hold timer + ≥4 pts + analyze ok | Adjusting | fit shape, show overlay |
| Drawing | pointer up (no shape) | Idle | discard |
| Adjusting | pointer up | Gizmo (or Launcher if `SmartShapeShowLauncher`) | show handles / launcher bar |
| Gizmo | handle drag | Gizmo | update `current_shape` |
| Gizmo | commit | Idle | paint + push history |
| Gizmo | cancel | Idle | discard overlay |
| any | Esc | Idle | cancel |

### Settings (CSP screenshot → Floss)

**Settings window** (not brush properties, not a docker):

- `SmartShapeEnabled` — "Hold to create figures"
- `SmartShapeHoldSeconds` — "Length of long-press" (0.70 s default; scrub slider or numeric field)
- `SmartShapeShowLauncher` — "Show Smart Shape launcher"

Internal constants (not user-facing in v1):

- `HoldRadiusPx` (default ~8, document space)
- `MinPoints` (default 4)
- `RdpEpsilon` (default 4.0 doc px)

---

## Input process: `SmartShapeInputProcess`

**Responsibilities:**
- Collect `CanvasInputSample` list (position, pressure, time) in document/layer space.
- Run hold timer (UI-thread `DispatcherTimer` or elapsed time from `TimeMicros`).
- On hold: call `SmartShapeAnalyzer.Analyze(samples)` → `SmartShape?`.
- Track adjusting reference (center, ref distance, ref angle) for scale/rotate.
- Track active gizmo handle + drag start for handle edits.
- Expose `GetPreview()` → `SmartShapePreviewInput { RawStroke, FittedShape, Phase, Handles }`.
- Expose `GetResult()` on commit → `SmartShapeCommitInput { Shape, AvgPressure }`.
- `RenderOverlay` delegates to `SmartShapeOverlay.Draw`.

**Critical: do not emit `StrokeInput` to `DirectDrawOutput` during the gesture.** The scribble must never hit the layer. Preview is overlay-only (Avalonia `DrawingContext` or Skia offscreen — same approach as `StraightLineOverlay`).

This avoids Krita's `_snapshot_pixels` / restore hack entirely.

---

## Analyzer: `SmartShapeAnalyzer`

Direct port of `stroke_analyzer.py` to C# under `src/Floss.App/SmartShape/`:

| Function (Python) | C# equivalent |
|-------------------|---------------|
| `rdp_simplify` | `RdpSimplify` |
| `analyze_stroke` | `AnalyzeStroke` → `SmartShape?` |
| `transform_shape` | `TransformShape` |
| `move_shape` | `MoveShape` |
| `stretch_shape` | `StretchShape` |
| `compute_gizmo_handles` | `ComputeGizmoHandles` |

**Shape kinds (v1):** `Line`, `Curve`, `Circle`, `Ellipse`, `Rectangle`, `Triangle`, `Polygon`.

**Shape model:** discriminated record / class hierarchy holding type-specific params (segment endpoints, ellipse axes, Bézier curve list, polygon vertices). Keep the Python dict shape isomorphic for port fidelity.

**Tests:** golden tests from krita-smart-shape sample strokes (synthetic polylines → expected `type` + bounds). No external deps.

---

## Overlay: `SmartShapeOverlay`

Static or instance renderer called from `RenderOverlay`:

- **Drawing:** faint polyline of raw samples (optional; can skip for perf).
- **Adjusting / Gizmo:** fitted geometry outline, center cross, handle squares/circles.
- **Labels:** shape type name near bbox (reference: `overlay.py`).

Use document-space coordinates transformed by viewport zoom (same as `StraightLineOverlay`, `SelectionOutlineOverlay`).

Handle hit-testing in layer/document space on pointer-down when `Phase == Gizmo`.

---

## Output: `SmartShapeOutput`

**Responsibilities:**
- `Preview`: no-op (overlay handles visuals).
- `Execute`: rasterize committed shape onto active layer.

### Commit path (brush-engine, Krita `layer_painter.py` parity)

1. Decompose shape → polyline / segment list (`ShapeToPolyline`, ~64 pts per curve).
2. Build synthetic `CanvasInputSample` chain along polyline.
3. Use `BrushEngine` segment painting (same path as `DirectDrawOutput.ProcessSegmentBatch`) with:
   - current `ctx.Brush`
   - `ctx.PaintColor`
   - average pressure from original stroke
   - layer offset translation
4. Capture `beforeTiles` for dirty region **once** at commit start (not at pointer-down).
5. Paint all segments synchronously on UI thread (one-shot, not streaming).
6. Push **one** `LayerTileHistoryState` + `CommitStroke()`.

```csharp
// Pseudocode — single undo step
var beforeTiles = layer.CaptureTilesInRegion(estimatedDirty);
// ... brushEngine paint segments ...
ctx.Document.CommitLayerTileMutation(layerIndex, beforeTiles, dirtyRegion);
ctx.Document.CommitStroke();
ctx.Document.NotifyChanged(dirtyRegion, layerIndex);
```

### Why one history entry

User expectation: one undo removes the entire smart shape. Intermediate scribble never existed on the layer. Gizmo edits are preview-only until commit.

`CompositeHistoryState` is unnecessary unless we later add multi-layer effects.

### Contrast with `DirectDrawOutput`

| | DirectDrawOutput | SmartShapeOutput |
|--|------------------|------------------|
| Preview writes tiles | yes (`LiveStroke`) | no |
| BeforeTiles captured | first segment | commit time only |
| Undo steps per gesture | 1 (on finalize) | 1 (on commit) |
| `CommitStroke()` | yes | yes |

### Contrast with Krita plugin

```python
# event_filter.py — fragile
self._snapshot_pixels()   # on press
# ...
self._restore_pixels(snap)  # before commit
commit_shape_to_active_layer(...)
```

Floss defers all pixel mutation to commit → simpler, faster, no 250ms deferred timer.

---

## History state design

No new `IHistoryState` type required for v1. Reuse:

- `LayerTileHistoryState` — pixel before/after tiles for the fitted shape region.
- `CommitStroke()` — increments stroke counter / timelapse grouping.

If we later need to undo "create shape layer" or vector metadata, add `SmartShapeHistoryState`; out of scope for raster-only v1.

### Preview-without-history precedent

Same pattern as layer opacity scrub:

```csharp
// DrawingDocument.cs — preview mutates live state, commit pushes history
PreviewActiveLayerOpacity(opacity);   // during drag
CommitActiveLayerOpacityScrub();    // on release
```

Smart shape preview mutates only overlay state, not document pixels — even cleaner.

---

## `ToolController` / `CompositeTool` dispatch

No tool swap, no modifier gate. The active brush tool stays active throughout.

1. `ToolFactory` creates `SmartShapeBrushInputProcess` + `SmartShapeBrushOutput` for all `OutputProcessType.DirectDraw` presets when building tools (or always — input checks `App.Config.SmartShapeEnabled` at pointer-down).
2. `CompositeTool` dispatches pointer events as today; the hybrid input internally transitions Drawing → Adjusting → Gizmo.
3. After shape detection, gizmo persists until commit/cancel — **no key held**, matching CSP.

`ToolAuxOperationType.StraightLine` remains independent: user can still hold straight-line modifier during a stroke; smart-shape hold detection and straight-line are separate code paths (straight-line wins if aux is active at pointer-down, or define precedence in implementation).

### `HasPendingOperation`

Return true when phase is `Adjusting` or `Gizmo` so:
- Tab switch / tool change calls `Deactivate` → `Cancel` (clear overlay).
- Document close warns about pending edit.

### `CanCommitFromClick` / `Commit`

On the brush `CompositeTool`, delegate to hybrid input when phase is Gizmo:
- Enter key → `Commit`
- Double-click on gizmo → `Commit` (match `PolylineInputProcess` pattern)
- Launcher UI button (when `SmartShapeShowLauncher`) → pick shape type / commit

---

## Coordinate spaces

| Space | Use |
|-------|-----|
| Widget | pointer events from `DrawingCanvas` |
| Document | analyzer, overlay drawing, commit |
| Layer local | `BrushEngine` tile writes (`sample.X - layer.OffsetX`) |

Convert at input boundary: `CanvasInputSample` already carries document coords from `DrawingCanvas` — verify against `StraightLineOverlay` / `BrushStrokeInputProcess.ToLayerSample` in `DirectDrawOutput`.

---

## MVP phases

### Phase 1 — Core loop (no gizmo)

- [ ] `SmartShapeAnalyzer` with Line, Rectangle, Ellipse
- [ ] `SmartShapeBrushInputProcess` state machine through Adjusting
- [ ] Overlay preview
- [ ] `SmartShapeBrushOutput` commit via brush segments
- [ ] `AppConfig` fields + Settings window UI (CSP parity)
- [ ] Wire into `ToolFactory` for all DirectDraw tools
- [ ] Unit tests for analyzer

### Phase 2 — Gizmo

- [ ] `Gizmo` phase + handle hit-test/drag
- [ ] Curve / polygon / triangle shapes
- [ ] Bézier anchor handles (`A_*`, `C_*` from reference)

### Phase 3 — Polish

- [ ] Shape launcher UI (when `SmartShapeShowLauncher`)
- [ ] Hold radius in advanced settings
- [ ] Pressure-aware width along fitted path
- [ ] Selection clip on commit (respect active selection mask)

---

## Open questions

1. **Eraser / smudge:** CSP applies to all drawing tools. Include eraser/smudge DirectDraw tools in v1, or pen/brush only?
2. **Straight-line aux precedence:** if straight-line modifier is held at pointer-down, skip smart-shape detection for that stroke?
3. **Vector layer output:** CSP can commit to vector layer. Floss has no vector layers — raster only.
4. **Settings page placement:** new "Drawing" sidebar item in `SettingsWindow`, or subsection under "General"?

---

## File plan (new code)

```
src/Floss.App/SmartShape/
  SmartShapeKind.cs
  SmartShapeModel.cs          // fitted shape records
  SmartShapeAnalyzer.cs       // port of stroke_analyzer.py
  SmartShapeOverlay.cs
  SmartShapeGizmo.cs          // handles + hit test
src/Floss.App/Processes/Input/
  SmartShapeBrushInputProcess.cs   // hybrid: normal brush + hold detect + shape phases
src/Floss.App/Processes/Output/
  SmartShapeBrushOutput.cs         // delegates DirectDraw until hold; smart commit after
src/Floss.App/Config/
  AppConfig.cs                     // SmartShape* fields
src/Floss.App/Windows/
  SettingsWindow.cs                // Smart Shape section
tests/Floss.App.Tests/SmartShape/
  SmartShapeAnalyzerTests.cs
```

---

## Implementation order

1. Port analyzer + tests (no UI).
2. Overlay renderer with hard-coded test shape (debug hotkey).
3. Input state machine + overlay only (no commit).
4. Output commit via brush engine + verify single undo.
5. `AppConfig` + Settings UI.
6. Wire into `ToolFactory` for DirectDraw tools.
7. Gizmo phase.

Reference this document before writing implementation code.
