# Krita brush resize and stabilizer (reference for Floss)

Source tree: `/home/neckles/projects/krita`

## Brush resize (`ChangeSize` / `ChangeSizeSnap`)

| File | Role |
|------|------|
| `plugins/tools/tool_enclose_and_fill/subtools/KisToolBasicBrushBase.cpp` | Shape tools resize |
| `libs/ui/tool/kis_tool_freehand.cc` | Freehand paint tools resize |
| `libs/ui/tool/kis_tool_utils.cpp` | `setCursorPos` |
| `libs/ui/tool/kis_tool_paint.cc` | `requestUpdateOutline` |

### Gesture flow (Krita)

1. **beginAlternateAction**: `GESTURE_MODE`, store `m_changeSizeInitialGestureDocPoint = event->point`, `m_changeSizeInitialGestureGlobalPoint = QCursor::pos()`, blank cursor, outline visible.
2. **continueAlternateAction**: Size from **horizontal widget drag** only:
   - `offset = convertDocumentToWidget(event->point) - convertDocumentToWidget(m_lastDocumentPoint)`
   - `sizeDiff = scaleCoeff * offset.x()` where `scaleCoeff` maps half screen width to max brush size in doc space.
   - `newSize = m_lastPaintOpSize + sizeDiff` (snap variant uses `qRound`).
   - **Outline stays at** `m_changeSizeInitialGestureDocPoint` (not at pointer).
3. **endAlternateAction**: `KisToolUtils::setCursorPos(m_changeSizeInitialGestureGlobalPoint)` — warps OS cursor back to press screen position; outline at initial doc point.

```cpp
// KisToolBasicBrushBase.cpp — continueAlternateAction (excerpt)
const qreal sizeDiff = scaleCoeff * offset.x();
settings->setPaintOpSize(newSize);
requestUpdateOutline(m_changeSizeInitialGestureDocPoint, 0);
```

```cpp
// kis_tool_utils.cpp
void setCursorPos(const QPoint &point) {
    QScreen *screen = qApp->screenAt(point);
    QCursor::setPos(screen, point);
}
```

**Not radial**: pointer does not stay on the brush circle edge; outline is anchored at the press point while size changes from horizontal motion.

## Floss CSP-style radial resize (target)

- Fixed **center** in canvas control coords (offset from press by `startRadius` along last drag direction).
- **Diameter** = `2 * distance(center, pointer)` (`BrushSizeAdjustment.FromRadiusDistance`).
- Pointer must sit on the **visible ring** at `center + dir * (size/2)` (snap when clamped or drift).
- On end: restore screen cursor to gesture-start position (same idea as Krita `endAlternateAction`).

## Stabilizer (Krita)

| File | Role |
|------|------|
| `libs/ui/tool/kis_tool_freehand_helper.cpp` | `stabilizerStart`, `getStabilizedPaintInfo`, `stabilizerPollAndPaint` |
| `libs/ui/tool/KisStabilizerDelayedPaintHelper.cpp` | Optional delayed paint |

- Deque prefilled with first sample; size `qMax(3, qRound(effectiveSmoothnessDistance(speed)))`.
- Poll timer (`stabilizerSampleSize` config).
- Uniform average over deque (`mixOtherOnlyPosition` or full sensor mix).
- Optional delay-distance gate before painting.

Floss equivalent: `BrushStrokeInputProcess` moving average + `EffectiveStabilization` on preset (no forced default when 0).

## Floss bugs fixed (resize cursor)

1. `ViewportCursorOverlay` skipped all drawing when `IsCursorPreviewLocked` — resize ring only on canvas with wrong `radiusScale` (`CanvasZoom` in local space → **double zoom**).
2. No snap of pointer to `center + dir * radius` when size clamps.
3. No cursor restore on gesture end.

## Floss key files

- `src/Floss.App/Input/CanvasInputRouter.cs` — gesture, snap, warp, restore
- `src/Floss.App/Canvas/DrawingCanvas.cs` — resize preview draw, `radiusScale` fix
- `src/Floss.App/Canvas/ViewportCursorOverlay.cs` — draw resize preview when locked
- `src/Floss.App/Input/PlatformCursorWarp.cs` — OS warp (best-effort)
