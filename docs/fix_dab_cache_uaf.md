---
name: fix-dab-cache-uaf
description: "Use-after-free crash fix in BrushEngine dab cache — evicting a bitmap that was already placed in the current segment's write buckets"
metadata: 
  node_type: memory
  type: project
  originSessionId: 0cc6cb38-1d04-4655-b7d7-5cead09f00da
---

## Bug: Dab cache evicts bitmaps still referenced in the same collection pass

**File**: `src/Floss.App/Brushes/BrushEngine.cs`  
**Methods**: `TryRasterizeCachedDabsTileMajor`, `TryRasterizeCachedColorDabsTileMajor`  
**Symptom**: Native crash (SIGABRT/SIGSEGV, no .NET exception) when drawing fast with color image brush tips for a long time.

### What was happening

`TryGetCachedDab` and `TryGetCachedColorDab` both did inline eviction: on a cache miss, they'd dequeue the oldest key, remove it from the cache, and call `.Dispose()` on its bitmap immediately.

In the tile-major rasterizers, stamps are collected in a loop — each stamp calls `TryGetCached*Dab`, which places the returned dab into a `PlacedDab`/`PlacedColorDab` stored in `buckets`. If a later stamp triggers a cache miss, the evicted "oldest" entry could be the same dab that was returned and stored in `buckets` for an earlier stamp in the **same loop**. After disposal, the write phase calls `placed.Dab.Bitmap.GetPixels().ToPointer()` → null pointer → crash.

This reproduces as "draw too fast for too long" because the cache (32 color dabs, 64 regular dabs) must be full first. Once full, every new unique stamp key evicts the oldest, which grows likely to match a same-segment dab after many varied strokes fill and cycle the cache.

### Fix

Removed inline eviction from `TryGetCachedDab` and `TryGetCachedColorDab`. Added:

```csharp
internal void TrimDabCache()
{
    while (_dabCache.Count > MaxCachedDabs && _dabCacheOrder.Count > 0)
    {
        var key = _dabCacheOrder.Dequeue();
        if (_dabCache.Remove(key, out var old))
            old.Mask.Dispose();
    }
}

internal void TrimColorDabCache()
{
    while (_colorDabCache.Count > MaxCachedColorDabs && _colorDabCacheOrder.Count > 0)
    {
        var key = _colorDabCacheOrder.Dequeue();
        if (_colorDabCache.Remove(key, out var old))
            old.Bitmap.Dispose();
    }
}
```

Called **after** `ExitPixelWriteLock()` in each tile-major rasterizer — at that point the `buckets` local is about to go out of scope and no dab bitmap references are outstanding.

**Why:** Eviction is safe only when no references to evictable bitmaps are live. The write lock phase consumes all `placed` references; trimming after is always safe.
