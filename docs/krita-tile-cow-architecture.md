# Krita Tile COW Architecture (tiles3)

Source: `/home/neckles/projects/krita/libs/image/tiles3/`

## Architecture Overview

```
KisTiledDataManager (64x64 tile grid, hash table)
  └─ KisTile (positioned at col,row, owns ref to KisTileData)
       └─ KisTileData (actual pixel bytes, refcounted, COW source)
            ├─ m_data: quint8* (pixel buffer, 64×64×pixelSize bytes)
            ├─ m_usersCount: QAtomicInt (COW user count)
            ├─ m_refCount: QAtomicInt (lifetime ref count)
            └─ m_clonesStack: lock-free stack of pre-allocated KisTileData*
                 └─ Filled by KisTileDataPooler (background thread)

KisTileDataStore (global singleton)
  ├─ m_pooler: KisTileDataPooler (background thread, pre-clones tiles)
  ├─ m_swapper: KisTileDataSwapper (swap to disk)
  ├─ duplicateTileData(td): clone via m_clonesStack.pop() or new+KisTileData copy ctor
  └─ freeTileData(td): unregister + delete
```

## COW Trigger: KisTile::lockForWrite()

File: `kis_tile.cc:221-261`

```cpp
void KisTile::lockForWrite() {
    blockSwapping();  // prevent swapper from touching data

    // Key: check if more than 1 user shares this tile data
    if (lazyCopying()) {  // == m_tileData->m_usersCount > 1
        m_COWMutex.lock();
        if (lazyCopying()) {  // double-check under lock
            KisTileData *newTD = m_tileData->clone();  // → duplicateTileData
            newTD->acquire();           // newTD: users=1, ref=1
            newTD->blockSwapping();
            KisTileData *oldTD = m_tileData;
            m_tileData = newTD;         // swap to clone
            safeReleaseOldTileData(oldTD);  // users--, unlock, release
        }
        m_COWMutex.unlock();
    }
}
```

**Key**: `lazyCopying()` is just `(m_tileData->m_usersCount > 1)`. No refcount dictionary lookup per tile — it's stored directly on the tile data object.

## Clone: KisTileData::clone() → KisTileDataStore::duplicateTileData()

File: `kis_tile_data_store.cc:174-191`

```cpp
KisTileData* KisTileDataStore::duplicateTileData(KisTileData *rhs) {
    KisTileData *td = 0;

    // FAST PATH: pop pre-clone from lock-free stack (no malloc, no memcpy)
    if (rhs->m_clonesStack.pop(td)) {
        // HIT — use pre-allocated clone created by pooler thread
    } else {
        // MISS — allocate + memcpy on the hot path
        rhs->blockSwapping();
        td = new KisTileData(*rhs);  // copy ctor: malloc + memcpy
        rhs->unblockSwapping();
    }
    registerTileData(td);
    return td;
}
```

## Pre-clone Pool: KisTileDataPooler (background thread)

File: `kis_tile_data_pooler.cc:170-214`

Pooler iterates all tile data regularly. For each tile:
- If `usersCount > 1` and tile was recently accessed (age=0): it's a "beggar" — needs clones
- Pre-allocates up to `min(usersCount - 1, 16)` clones via `new KisTileData(*td)` (copy ctor copies pixel data)
- Clones stored in `td->m_clonesStack` (lock-free stack)
- Memory budget enforced by freeing clones from "donor" tiles (age > 0)

Result: when `lockForWrite()` triggers COW, the clone is already waiting in the stack. **Zero malloc, zero memcpy on the hot path.**

## Acquire/Release (Refcounting)

File: `kis_tile_data.h:35-58`

```cpp
// Called when a KisTile starts referencing this data
bool acquire() {
    if (m_usersCount == 1) {
        // Only 1 user — flush stale pre-clones
        while (m_clonesStack.pop(clone)) delete clone;
    }
    bool _ref = ref();         // m_refCount.ref()
    m_usersCount.ref();        // COW user count
    return _ref;
}

// Called when a KisTile drops its reference
bool release() {
    m_usersCount.deref();      // COW user count
    bool _ref = deref();       // m_refCount.deref(); when reaches 0, store frees
    return _ref;
}
```

## What Floss's COW Is Missing

| Aspect | Krita | Floss |
|--------|-------|-------|
| Refcount storage | On tile data object (struct field) | Dictionary lookup `_refs[(x,y)]` |
| Clone trigger | `usersCount > 1` inline check | Dictionary lookup + comparison |
| Clone pool | Pre-allocated in background thread | Always `new byte[16384]` + `Buffer.BlockCopy` |
| Tile data object | Separate allocation from pixel buffer | `byte[]` is both container and buffer |
| Memory management | Boost pool + SimpleCache for pixel buffers | GC-managed `byte[]` arrays |

Floss's `_refs` dictionary approach requires O(1) lookup on every tile access. Krita stores the refcount inline on the `KisTileData` object — no dictionary, no lookup. This is faster but requires separating the "tile position" concept from the "tile data" concept (which Floss doesn't do — `byte[]` arrays are indexed by position).
