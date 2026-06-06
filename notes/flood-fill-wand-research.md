# Flood fill & magic wand — research & improvement plan

Research date: 2026-06-04. Use this file before changing fill/wand code.

## Current architecture (Floss)

| Tool | Output | Core algorithm | File |
|------|--------|----------------|------|
| **Magic wand** | `MagicWandOutput` | Scanline flood → selection mask | `SelectionMask.FloodFillMask` |
| **Fill (bucket)** | `FloodFillOutput` | 4-neighbor **pixel BFS** + full-doc `bool[] visited` | `FloodFillOutput.Execute` |

Both share tolerance/reference **preset fields** on `ToolPreset` (`ToolGroupConfig.cs`) but use **different fill engines** and **different color metrics**.

### Magic wand path

```
ClickInput → MagicWandOutput.Execute
  → BuildReferenceBuffer (optional, full doc BGRA alloc)
  → SelectionMask.SetFromFloodFill / SetFromFloodFillBuffer
  → FloodFillMask (scanline + stack of seeds)
```

Key code:

- `src/Floss.App/Processes/Output/MagicWandOutput.cs`
- `src/Floss.App/Tools/Selection/SelectionMask.cs` — `SetFromFloodFill`, `FloodFillMask`, `_visitStamp` epoch

**Scanline fill** (good): extend horizontal span, mark visited, push new row seeds. Complexity **O(region pixels)**.

**Visit dedup**: `_visitStamp[]` + `_visitEpoch` — avoids clearing docW×docH each click (Krita-style epoch stamp).

**Similarity cache**: `Dictionary` keyed by packed BGRA for flat-color regions (comment: “Krita-style color-similarity cache”).

**Tolerance metric** (wand):

```csharp
int tolInt = (int)(tolerance * 255 * 4);
// Manhattan: |ΔB|+|ΔG|+|ΔR|+|ΔA| <= tolInt
```

### Fill (bucket) path

```
ClickInput → FloodFillOutput.Execute
  → BuildReferenceBuffer (optional)
  → Queue<int> BFS over linear indices 0..docW*docH-1
  → bool[docW * docH] visited
  → AlphaLockPixelOps.TryWriteColor per matched pixel
```

Key code: `src/Floss.App/Processes/Output/FloodFillOutput.cs`

**Tolerance metric** (fill) — **different from wand**:

```csharp
int toleranceSq = (int)(Tolerance * 255 * Tolerance * 255 * 4);
// Squared Euclidean: ΔR²+ΔG²+ΔB²+ΔA² <= toleranceSq
```

**Memory**: allocates `bool[pixelCount]` + queue every click. At 4096² ≈ 16M bools (~16 MB) **even when filling a tiny island**.

**Cap**: aborts if `docW * docH > 64_000_000` (8k×8k) — entire fill fails, no partial.

**No scanline**: revisits pixels via queue; still O(region) visits but worse constant factors and cache behavior than scanline.

---

## Gaps: UI exposed but not implemented

| Preset / UI field | Shown in UI | Synced in `ToolPresetSync` | Used at runtime |
|-------------------|-------------|------------------------------|-----------------|
| `Tolerance` | Fill + wand | Yes | Yes (inconsistent metrics) |
| `FillReference` | Fill only (rail) | Yes | Fill + wand code |
| `FillReference` | **Not on wand rail** | — | Wand defaults to CurrentLayer unless preset JSON edited |
| `ContiguousFill` | Fill + wand | **No** | **No** — always contiguous |
| `AreaScaling` | Fill (+ lasso UI) | **No** | **No** |
| `Antialiasing` / `AntialiasingQuality` | Other tools | Wand: **No** | Wand `Antialiasing` property unused |

**Contiguous off** (CSP/Krita): select/fill **all** pixels matching tolerance on the canvas, not only connected component. Requires full-image scan or spatial index — preset exists, behavior missing.

**Area scaling** (CSP): expand/contract fill under line art; maps to Krita **grow selection** / opacity spread. Not implemented.

---

## Performance bottlenecks (ordered)

### 1. Dual engines — fill tool is the weak path

Magic wand already uses scanline fill. **Bucket fill should not use a separate BFS implementation.**

| | Scanline (wand) | BFS (fill) |
|--|-----------------|------------|
| Visited structure | Epoch `int[]` (reused) | New `bool[]` per click |
| Frontier | Stack of row seeds | `Queue<int>` |
| Work | O(filled region) | O(filled region) but higher constant + alloc |

**Recommendation:** Extract shared `FloodFillEngine` (or route fill through mask-then-blit) used by both tools.

### 2. `TiledPixelBuffer.GetPixel` in hot loop (wand, current layer)

`Similar()` calls `pixels.GetPixel` per probe → `EnsureRaw(ToTileKey)` + lock per pixel.

```700:714:src/Floss.App/Document/TiledPixelBuffer.cs
public void GetPixel(int x, int y, out byte b, out byte g, out byte r, out byte a)
{
    var tile = EnsureRaw(ToTileKey(x, y));
    ...
}
```

**Recommendation:** Tile-local scanline: walk contiguous bytes inside a 256×256 tile; only re-resolve tile pointer on boundary cross. Optional flat row buffer when `refBuf` already exists.

### 3. `BuildReferenceBuffer` — full composite every click

Both outputs allocate `byte[w*h*4]` and `BlendOnto` all visible layers when reference ≠ current layer.

**Recommendation:** Cache composite keyed by `(document revision, FillReferenceMode, visible layer set)`; invalidate on layer paint. Only rebuild dirty tiles (align with `LayerCompositor` / tile COW notes in `docs/krita-tile-cow-architecture.md`).

### 4. `simCache` Dictionary on gradient-heavy art

Helps flat fills; on noisy/gradient areas every packed color is unique → dictionary overhead without benefit.

**Recommendation:** Krita uses **OptimizedDifferencePolicy** with fast `memcmp` path at threshold 0 (`KisColorSelectionPolicies.h`). Add integer difference function without per-pixel allocations; drop dictionary on ref-buffer path (direct array index).

### 5. Selection mask commit — full metadata scan

`RecomputeMaskMetadata` scans entire mask after wand click. For huge selections, consider incremental bounds/count during fill.

### 6. No tests for flood/wand behavior

`tests/` has selection op tests but **no** tolerance, contiguous, reference-layer, or fill correctness tests.

---

## Reference apps (what “good” looks like)

### Krita — contiguous select (`KisToolSelectContiguous`, `kis_scanline_fill.cpp`)

Sources (external):

- [kis_scanline_fill.cpp](https://srcdoc.krita.maou-maou.fr/kis__scanline__fill_8cpp_source.html)
- [KisColorSelectionPolicies.h](https://srcdoc.krita.maou-maou.fr/KisColorSelectionPolicies_8h_source.html)
- [Contiguous select manual](https://docs.krita.org/en/reference_manual/tools/contiguous_select.html)

Features Floss lacks:

| Feature | Krita | Floss |
|---------|-------|-------|
| Scanline core | Yes (`KisScanlineFill`) | Wand yes, fill no |
| Threshold | Integer 0–255 style | 0–1 float, **two metrics** |
| Close gap | Yes (5.3+, `KisGapMap`) | No |
| Opacity spread | Yes (5.1+) | No (`AreaScaling` unwired) |
| Anti-alias selection | Yes | No |
| Grow / feather | Yes | No |
| Reference layers | All / current / color labels | Current / reference / all (partial UI) |
| Boundary fill mode | `FloodFill` vs `BoundaryFill` | No |
| Difference policies | Hard/soft, transparent-aware | Manhattan or L2 only |

Krita difference policy pattern (worth copying):

- `OptimizedDifferencePolicy` — fast path when threshold minimal
- `SlowDifferencePolicy` — general case
- `opacityFromDifference` — soft selection edge (hard vs soft policy)

### Clip Studio Paint

Manual: [Fill subtools](https://help.clip-studio.com/en-us/manual_en/810_subtools/F.htm), [Advanced fill](https://help.clip-studio.com/en-us/manual_en/420_fill/Advanced_Fill.htm)

| Feature | CSP | Floss |
|---------|-----|-------|
| Connected pixels only | Toggle (`ContiguousFill`) | UI only, not wired |
| Refer multiple / reference layer | Yes | `FillReferenceMode` partial |
| Close gap | Yes | No |
| Area scaling | Yes (bleed under lines) | UI only |
| Tolerance + area scaling interaction | Documented | N/A |

CSP treats **fill tool and auto-select (wand) as paired settings** — same options, different output (pixels vs selection). Floss should mirror that: **one options struct, two outputs**.

---

## Color similarity — unify before adding features

**Problem:** Same slider produces different regions on wand vs fill.

**Proposal:** Single `ColorDifference` module:

```csharp
// notes/flood-fill-wand-research.md — target API sketch
static int DifferenceBGRA(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ColorMatchMode mode);
static bool IsSimilar(..., double tolerance01); // tolerance maps to Krita-like 0..255 internally
```

Modes to consider (phase 2+):

1. **Manhattan RGBA** (current wand) — fast, predictable
2. **Max channel** (cheaper than L2)
3. **HSV distance** (CSP-like “color margin” feel on hues)
4. **Perceptual LAB** (best quality, slower)

Phase 1: pick one metric for both tools; document mapping `tolerance 0.1 ≈ Krita threshold 26`.

---

## Recommended implementation roadmap

### Phase 1 — Correctness & deduplication (high ROI)

1. **`FloodFillEngine` crate/module** under `src/Floss.App/` (e.g. `Canvas/FloodFill/`):
   - Port `SelectionMask.FloodFillMask` scanline core
   - Accept `Func<int,int,bool>` or ref buffer + tolerance
   - Return spans or visit callback for “write pixel” / “set mask”

2. **Rewrite `FloodFillOutput`** to use scanline engine + **epoch visit array** (reuse global pool or `SelectionMask`-style stamp), not per-click `bool[]`.

3. **Unify `ColorDifference`** — same metric for wand + fill; add unit tests with fixed 3×3 patterns.

4. **Wire `ContiguousFill`**:
   - `true` → existing scanline (connected)
   - `false` → scan layer bounds (or doc bounds), test every pixel with `similar()` (CSP “not connected”)

5. **Wire `FillReference` in wand rail UI** (parity with fill).

6. **Tests:** `FloodFillEngineTests`, `MagicWandToleranceTests`, `ContiguousFillTests`, reference-layer tests.

### Phase 2 — Performance

1. Tile-aware scanline (direct tile byte access)
2. Cached reference composite (document generation counter)
3. Incremental mask bounds during fill
4. Optional: run fill on `Task.Run` + cancel token for >N ms operations (UI thread stays responsive)

### Phase 3 — Artist-facing quality (Krita/CSP parity)

1. **Close gap** — port Krita `KisGapMap` idea for lineart (morphological max distance, then scanline)
2. **Area scaling / grow** — morphological expand/contract on mask after fill, or Krita opacity spread
3. **Anti-alias selection** — soft edge from difference policy (`SoftSelectionPolicy`)
4. **Live preview** on wand drag (Krita shows marching ants while adjusting threshold — optional)

### Phase 4 — Advanced (lower priority)

- LAB/HSV tolerance modes
- Boundary-fill mode (fill until strong edge)
- GPU flood fill for 8k+ (compute shader / Skia path)

---

## Proposed module layout

```
src/Floss.App/Canvas/FloodFill/
  ColorDifference.cs      // unified metric + tolerance mapping
  FloodFillScanline.cs    // scanline engine (visit epoch, seed stack)
  FloodFillReference.cs   // cached composite builder
  NonContiguousFill.cs    // full-bounds scan when ContiguousFill=false
```

Consumers:

- `SelectionMask` — thin wrapper: fill mask bytes via engine callback
- `FloodFillOutput` — fill pixels via engine callback + `AlphaLockPixelOps`
- Future: close-gap preprocessor hook before scanline

---

## What NOT to do

- **Blog “scanbox” full-grid iteration** ([ultimate-3d-floodfill](https://unity3dmc.blogspot.com/2017/02/ultimate-3d-floodfill-scanline.html)) — O(passes × canvas) vs O(region) scanline; wrong for sparse 2D selections.
- **Keep two fill implementations** — doubles bug surface (already caused metric mismatch).
- **Allocate `bool[docW*docH]` per click** — replace with epoch stamp array (grow once, reuse).

---

## File index (current)

| File | Role |
|------|------|
| `src/Floss.App/Processes/Output/MagicWandOutput.cs` | Wand click → selection |
| `src/Floss.App/Processes/Output/FloodFillOutput.cs` | Bucket fill → pixels |
| `src/Floss.App/Tools/Selection/SelectionMask.cs` | Scanline `FloodFillMask`, visit epoch |
| `src/Floss.App/Processes/ToolPresetSync.cs` | Live preset → output fields |
| `src/Floss.App/Config/ToolGroupConfig.cs` | `Tolerance`, `ContiguousFill`, `FillReference`, `AreaScaling` |
| `src/Floss.App/MainWindow/MainWindow.ToolProperty.cs` | Rail UI |
| `src/Floss.App/Document/TiledPixelBuffer.cs` | `GetPixel`, `BlendOnto` |

## External references

- Krita scanline fill: `libs/image/floodfill/kis_scanline_fill.cpp`
- Krita color policies: `libs/image/KisColorSelectionPolicies.h`
- Krita contiguous tool: `KisToolSelectContiguous`
- CSP fill/auto-select: [subtool F](https://help.clip-studio.com/en-us/manual_en/810_subtools/F.htm)
