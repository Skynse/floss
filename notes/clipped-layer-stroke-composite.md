# Clipped layer live stroke compositing

## Symptom

Painting on a clipping layer showed unclipped dab tiles until pen release; blocky 64×64 artifacts at dirty boundaries.

## Cause

`LayerCompositor` stroke split copied `ProjectionSiblingItem` from the full root stack into `AboveRoots` without reindexing. `BaseLayerIndex` pointed at the clip base in the **full** stack, not in the partial above slice — clip flatten used the wrong base (often the clip layer itself).

The clip base was also left in `BelowRoots` via `HasClippingChildren` skip, so the above pass had to supply the entire clip group.

## Fix

`TryCreateStrokeSplitPlan`:

1. `ClipCompositeStartIndex` — first sibling to include (clip base + chain).
2. `BelowRoots` — root items **before** clip base only.
3. `AboveRoots` — `BuildSiblingStack` on layers from clip base through stack end (valid indices).

Nested group paint uses the same clip start for in-group below/above child subsets.

## Files

- `LayerProjectionPlane.cs` — `ClipCompositeStartIndex`
- `LayerCompositor.cs` — `TryCreateStrokeSplitPlan`, `BuildStrokeBelowTile`, `CompositeStrokeAboveLayers`
