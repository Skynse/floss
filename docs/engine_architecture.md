# Engine Architecture

The engine boundary is designed around tiny control messages, not pixel movement.

## Ownership

Flutter owns:

- application chrome, panels, gestures, viewport transforms, and command routing
- pointer capture and batching
- displaying the native render target

Rust owns:

- document, layer, tile, brush, and stroke state
- Catmull-Rom resampling and later velocity/prediction filters
- dirty tile calculation
- future pixel buffers, blend kernels, tile caches, and PSD import/export

## Bridge Shape

Use `flutter_rust_bridge` v2 with this repo's `flutter_rust_bridge.yaml`:

```yaml
rust_input: crate::api
rust_root: rust/
dart_output: lib/src/bridge/generated
```

The latest package visible on pub.dev at setup time is `flutter_rust_bridge: ^2.12.0`.

The Rust API starts at `rust/src/api/drawing.rs`. It exposes `FlossEngine`, `EngineOptions`, compact stroke samples, frame deltas, and stats. The Dart-side hand-written contract is `lib/src/bridge/native_engine_contract.dart`; generated FRB code should sit under `lib/src/bridge/generated`.

## Performance Rules

- Do not send whole canvas images over FRB during drawing.
- Batch pointer moves before bridge calls when the UI receives many events in one frame.
- Treat `FrameDelta.dirty_tiles` as the renderer's invalidation queue.
- Keep current Dart `CustomPaint` rendering as a debug/prototype surface only.
- The first native renderer should update CPU tile buffers; the second should move tile upload/compositing behind a Flutter texture.

## Integration Commands

Run these when you are ready to pull in dependencies and generate bindings:

```sh
flutter pub add flutter_rust_bridge
cargo install flutter_rust_bridge_codegen
flutter_rust_bridge_codegen generate
```

If the generator asks for native build integration, run:

```sh
flutter_rust_bridge_codegen integrate
```
