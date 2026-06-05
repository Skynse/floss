# Tier A compositor cache (persistent display + split budgets)

Reference for `LayerCompositor` changes (2026-06-04). Builds on `notes/drawpile-compositor-comparison.md`.

## Problem

Pan on large docs re-merges the layer stack because:
1. `TrimCompositeCache` evicts `_tileScratch` by distance from viewport center.
2. `CompositeCore` treats `_tileScratch == null` in the viewport as "missing" and re-composites even when `_cellBitmaps` already hold the merged pixels.

## Fix

### 1. Persistent display (`_tileDisplayCommitted`)

- Set `true` in `CopyTileToCell` after writing scratch into the cell bitmap.
- Clear for tiles in a content-dirty region (`QueueDirtyTilesForRegion`).
- Clear all on full invalidation / resize / `ClearAll`.
- **Missing prefetch**: only queue tiles where `scratch == null && !displayCommitted`.
- Pan reads cells via `DrawTiles` — no merge needed if display committed.

### 2. Scratch trim (LRU, display-aware)

- Track `_tileScratchGeneration[idx]` on composite touch.
- When over `MaxCompositeCacheTiles`, evict scratch for **display-committed** tiles first (oldest generation).
- Then evict uncommitted scratch if still over limit.
- **No viewport-distance eviction** — pan does not drop merged display.

### 3. Split budgets

| Queue | Budget | When |
|-------|--------|------|
| Content dirty (`_pendingComposite`) | `DirtyTileBudget` (32) / `StrokeSuspendTileBudget` (256) | Document edits |
| Navigation prefetch (`missing`) | `NavigationPrefetchBudget` (1024) | Viewport holes without display |

Remove `unbounded = viewport != null` — that disabled all caps during normal render.

Exception: `wasFull` bootstrap (open/import/resize) still uses unbounded content budget for one-shot drain.

## Key files

- `src/Floss.App/Canvas/Compositing/LayerCompositor.cs` — `CompositeCore`, `TrimCompositeCache`, `CopyTileToCell`
- `src/Floss.App/Canvas/Compositing/CompositorConfig.cs` — budget constants
- `tests/Floss.App.Tests/LayerCompositorTests.cs` — pan revisit test

## Snippets

```csharp
// CopyTileToCell — mark display ready
_tileDisplayCommitted[ti] = true;

// missing list
if (idx >= 0 && _tileScratch[idx] == null && !_pendingComposite.Contains(idx)
    && (ti >= _tileDisplayCommitted.Length || !_tileDisplayCommitted[ti]))
    missing.Add(idx);

// budgets
var contentBudget = _strokeSuspendDepth > 0 ? StrokeSuspendTileBudget : DirtyTileBudget;
SelectPendingTiles(vpClip, missing, contentBudget, NavigationPrefetchBudget);
```
