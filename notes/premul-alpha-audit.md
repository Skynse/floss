# Premul / unpremul audit (2026-06-06)

> **Crucial fix (confirmed):** Dark halos on default round-brush strokes were **not** fixed by this audit’s image-tip changes. The real fix is `StampSrcOverMaskedRow` AVX → scalar associated-alpha. **Read `notes/dark-brush-edge-fix.md` before touching brush SIMD or premul again.**

## Canonical format

- **Layer tiles / compositor / brush CPU stamp:** unpremul BGRA (`TiledPixelBuffer`, `SimdPixelOps.StampSrcOver`, `LayerCompositorPixelOps` normal blend).
- **SK scratch / tile RenderWithSkia:** `SKAlphaType.Unpremul`.

## Bugs found

| Location | Issue |
|----------|--------|
| `ImageBrushTip.cs` | Color stamps and scaled sources use `SKAlphaType.Premul` but downstream treats RGB as unpremul. |
| `BrushTipNodeGraph.ImageSamplerColor` | Reads premul stamp bytes as straight RGB; alpha from separate mask → dark edges on graph/image tips. |
| `BrushEngine.SampleColorDabPixel` | Bilinear filters unpremul RGBA separately → dark halos on scaled color dabs. |
| `NodeGraphView` / `BrushTipBrowserWindow` | UI previews use Premul (display only). |

## Compositor / brush CPU paths (OK)

- `SimdPixelOps.StampSrcOver` — unpremul Porter-Duff with associated alpha.
- `LayerCompositorPixelOps.CompositeLayer` normal path — unpremul src-over.
- `AlphaLockPixelOps.ApplySrcOver` — unpremul.
- `BlendTilePremultiplied` — unused dead path; not called from compositor.

## Fixes applied

1. `ImageBrushTip` — BGRA bitmaps → `SKAlphaType.Unpremul`.
2. `ImageSamplerColor` — read unpremul RGB; combine stamp alpha with mask alpha.
3. `SampleColorDabPixel` — bilinear in premul space, unpremul at end.
4. UI preview bitmaps → Unpremul for consistency.
5. **See `notes/dark-brush-edge-fix.md`** — primary confirmed fix for round-brush dark edges (`StampSrcOverMaskedRow`).

## Relevant paths (round brush dark edges)

Full write-up: **`notes/dark-brush-edge-fix.md`**
