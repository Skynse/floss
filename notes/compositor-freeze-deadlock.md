# Compositor UI freeze (2026-06-05)

## Symptom

App appears frozen — no input, no redraw. Profiling shows render pool idle-wait; UI stuck in Avalonia render loop.

## Cause

`DrawingCanvas` runs `Composite()` on a **background thread** (`BackgroundCompositePass`). `Composite()` holds `lock (CompositeGate)` for all of `CompositeCore`, including `DispatchToPool` → `CountdownEvent.Wait()`.

UI `Render()` → `DrawTiles()` also takes `lock (CompositeGate)`.

**Deadlock:** background thread holds gate + waits on pool; UI thread waits on gate.

`UseParallelComposite` only skipped parallel on the UI thread, so background composite still used the pool while holding the gate.

## Fix

`UseParallelComposite` → always `false`. Tile merge stays serial inside the composite critical section.

## File

- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs`

## Prior terminal crashes

Separate issue: older builds used non-thread-safe `HashSet` in stroke overlay path (`PublishTileDisplayImage`). Current code uses `ConcurrentDictionary` for `_strokeOverlayTiles`. Restart after freeze; rebuild after fix.
