# Floss

Floss is a Flutter drawing app scaffold aimed at a Procreate-style workflow with room for native, tile-backed rendering later.

## Current Groundwork

- Studio UI shell with canvas, tool rail, brush controls, layer placeholder, and status bar.
- `BrushEngine` interface that isolates stroke/layer logic from Flutter widgets.
- Prototype Dart brush engine with pressure-aware samples, Catmull-Rom resampling, and dirty tile tracking.
- Canvas surface built from `Listener`, `RepaintBoundary`, and `CustomPaint` as a temporary renderer.
- Tests covering app bootstrapping and brush-engine tile invalidation.
- Rust core scaffold under `rust/` with the document, tile, brush, stroke, and FRB-facing API split.

## Performance Direction

The prototype painter is intentionally disposable. The intended high-performance path is:

1. Keep Flutter responsible for UI, pointer capture, panels, and composition.
2. Move tile storage, brush stamping, layer compositing, and import/export into a native core.
3. Use tile dirty ranges to update only changed 256x256 regions.
4. Present the native canvas through a texture-backed renderer instead of moving full images through Dart.
5. Keep brush input as compact stroke samples crossing the boundary, not pixel buffers.

The central contract for that swap is `lib/src/engine/brush_engine.dart`.

See `docs/engine_architecture.md` for the Rust/Flutter bridge plan.
