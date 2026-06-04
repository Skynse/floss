# Floss Rust Rewrite — Update Spec

## Goal
Update the existing Rust code (from `rust_rewrite` branch) to match the current C# codebase architecture.

## Priority Order
1. **Compositor** — CPU compositor matching C# LayerCompositor
2. **App wiring** — Wire ToolController, Compositor, proper stroke pipeline in main.rs
3. **Brush color mix** — Smudge/smear/color mixing (LCh space)
4. **Preview pipeline** — Async brush rendering matching C# DirectDrawOutput

## Phase 1: Compositor

### Current State
- `floss-compositor/src/compositor.rs`: GPU-oriented data model only. No actual compositing.
- `floss-core/src/blend.rs`: 12 blend modes with `blend_per_pixel()` function
- `floss-app/src/main.rs`: Simple CPU compositing in `composite_region()` — iterates layers, reads pixel via `try_read_pixel`, src-over compositing

### Target State (matching C# LayerCompositor.cs)
- Full CPU compositor that produces a flat BGRA output buffer
- Tile-major processing: composite one tile at a time
- All 40+ blend modes with LUTs for 15 modes, HSL modes
- LayerProjectionPlane: sibling stack building for clip groups, pass-through groups
- Adjustment layer application
- Stroke-suspend optimization (cache below-paint-layer composite)

### Implementation Plan

#### Step 1.1: Expand BlendMode enum (floss-core/src/blend.rs)
Add all blend modes from C# BlendMode.cs with traits:
- `PreservesAlpha` — modes that don't change alpha
- `HasLut` — modes that use lookup tables
- `IsHslMode` — HSL color space modes

Modes to add:
- Perceptual color modes: LinearDodge, LinearBurn, VividLight, LinearLight, PinLight, HardMix, Exclusion, Subtract, Divide, LighterColor, DarkerColor
- Combine modes: NormalCombine, CombineMultiply, CombineDivide, CombineAdd, CombineSubtract, CombineReplace
- HSL: Hue_Legacy, Saturation_Legacy, Color_Legacy, Luminosity_Legacy, Hue, Saturation, Color, Luminosity
- Misc: Dissolve, Behind, Clear, DarkerColor

#### Step 1.2: Per-pixel blend functions (floss-core/src/blend.rs)
Add `BlendPerPixel` function matching C# LayerCompositorPixelOps:
- LUT-based blending for 15 modes
- HSL mode blending using double-precision math
- Per-pixel alpha handling

#### Step 1.3: Build LayerCompositor (floss-compositor)
Rename current `Compositor` → remove GPU stuff. Build CPU compositor:
- `LayerCompositor::new(w, h)` 
- `Composite(output: &mut [u8])` — produce flat BGRA buffer
- Tile-major iteration: for each composite tile coordinate, composite all visible layers
- Layer stack traversal via LayerProjectionPlane::BuildSiblingStack
- Stroke-suspend: `SuspendStroke(paint_layer_index)`, `ResumeStroke()`, below-cache
- Cell grid output (for display): group tiles into MaxCellDim-sized cells

### Files to modify
- `crates/floss-core/src/blend.rs` — expand BlendMode, add BlendPerPixel
- `crates/floss-compositor/src/compositor.rs` — replace GPU model with CPU compositor
- `crates/floss-compositor/src/shaders.rs` — remove GPU shaders (no longer needed)

---

## Phase 2: App Wiring

### Current State
- main.rs uses its own `DrawState` struct, does direct stamping
- Does NOT use ToolController, CompositeTool, DirectDrawOutput
- Simple CPU compositing in main.rs
- Tablet pressure via evdev (working)

### Target State
- Use ToolController for input routing
- Use DirectDrawOutput via CompositeTool for brush strokes
- Use LayerCompositor for display compositing
- Proper viewport with zoom/pan

### Implementation Plan
1. Build AppState using ToolController + Compositor
2. Wire pointer events through ToolController
3. Use LayerCompositor for display
4. Viewport transform with zoom/pan

---

## Phase 3: Brush Color Mix

### Current State
- BrushPreset has color_mix, color_load, color_stretch, smudge_mode, mixing_mode fields
- BrushEngine ignores all of them — always stamps fixed brush.color
- No smudge/smear support

### Target State (matching C# BrushEngine.cs + ColorMix.cs)
- LCh color space mixing for smudge/smear/blend
- Batch mode (Blend smudge): sample pixel once, process all stamps
- Sequential mode (Smear/Smudge): read from live layer between stamps
- Halton-sequence weighted blur sampling for pigment pickup
- Spatial smear (Krita-style pixel displacement)

---

## Phase 4: Preview Pipeline

### Current State
- DirectDrawOutput::execute() is synchronous
- No async preview rendering
- No time-slicing

### Target State (matching C# DirectDrawOutput.cs)
- Async brush rendering via ProcessQueuedAsync
- Adaptive time-slicing into ~10ms budget chunks
- Before-tile capture for undo
- Preview dirty region batching
- Selection/alpha-lock restoration
