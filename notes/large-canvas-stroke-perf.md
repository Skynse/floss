# Large canvas stroke performance

## Problem
Drawing on large canvases feels slow because each dab triggers display recompositing of the full layer stack for every dirty 64px tile in the viewport.

## Root cause (current baseline @ 4b53bee revert)
`LayerCompositor` has **stroke suspend hooks** (`BeginStrokeSuspend` / `ExtendStrokeSuspend`) but:
- `ExtendStrokeSuspend` is a no-op
- `CompositeCore` never uses `_strokePaintLayerIndex`
- No **stroke-below cache** — every tile recomposites all layers from paper up

The optimization existed in commit `2d070e0` and was dropped in `4220611` revert.

## Fix (restore + improve)
1. **Per-tile below-paint cache** (`_strokeBelowCache`): composite layers below the active paint layer once per tile; during stroke only re-merge paint layer + layers above onto that cache.
2. **Do not invalidate below cache on `ExtendStrokeSuspend`** — below layers are static during a stroke; only display tiles in the dirty region need updates.
3. **Per-tile below-cache readiness** — a single global “cache valid” flag caused new dirty tiles (stroke moved into new 64px cells) to skip building the below cache and show checkerboard seams. Track `_strokeBelowCacheReady[]` per tile index.
3. **Use `wasFull` not `_fullDirty`** when deciding if stroke fast-path is allowed.
4. **Nested paint layers**: split inside the containing root group when the active layer is not a root sibling.

## Key files
| File | Role |
|------|------|
| `src/Floss.App/Canvas/Compositing/LayerCompositor.cs` | `CompositeCore`, stroke cache, suspend API |
| `src/Floss.App/Processes/Output/DirectDrawOutput.cs` | `NotifyStrokeSuspendBegin/Extend`, dirty flush |
| `src/Floss.App/Canvas/DrawingCanvas.cs` | wires suspend events, `Composite(..., viewport)` |
| `src/Floss.App/Document/DrawingDocument.cs` | stroke suspend events |

## Reference implementation
`git show 2d070e0:src/Floss.App/Canvas/Compositing/LayerCompositor.cs` lines ~402–520, ~665–735
