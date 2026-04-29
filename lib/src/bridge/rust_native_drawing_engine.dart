import 'dart:typed_data';

import '../brush/brush_preset.dart' as app;
import '../canvas/stroke_sample.dart' as app;
import '../canvas/tile_grid.dart' as app;
import 'generated/api/drawing.dart' as api;
import 'generated/brush.dart' as rust_brush;
import 'generated/document.dart' as rust_document;
import 'generated/frb_generated.dart';
import 'generated/stroke.dart' as rust_stroke;
import 'generated/tile.dart' as rust_tile;
import 'native_engine_contract.dart';

class RustNativeDrawingEngine implements NativeDrawingEngine {
  api.FlossEngine? _engine;
  static Future<void>? _initFuture;

  @override
  Future<void> initialize(NativeEngineOptions options) async {
    _initFuture ??= RustLib.init();
    await _initFuture;
    _engine = await api.FlossEngine.create(
      options: api.EngineOptions(
        width: options.width,
        height: options.height,
        tileSize: options.tileSize,
      ),
    );
  }

  @override
  Future<void> setBrush(app.BrushPreset brush) async {
    await _requireEngine().setBrush(brush: _toRustBrush(brush));
  }

  @override
  Future<NativeFrameDelta> beginStroke(app.StrokeSample sample) async {
    return _toNativeFrameDelta(
      await _requireEngine().beginStroke(sample: _toRustSample(sample)),
    );
  }

  @override
  Future<NativeFrameDelta> appendStrokeSamples(
    List<app.StrokeSample> samples,
  ) async {
    return _toNativeFrameDelta(
      await _requireEngine().appendStrokeSamples(
        samples: samples.map(_toRustSample).toList(growable: false),
      ),
    );
  }

  @override
  Future<NativeFrameDelta> endStroke(app.StrokeSample sample) async {
    return _toNativeFrameDelta(
      await _requireEngine().endStroke(sample: _toRustSample(sample)),
    );
  }

  @override
  Future<NativeFrameDelta> cancelStroke() async {
    return _toNativeFrameDelta(await _requireEngine().cancelStroke());
  }

  @override
  Future<NativeFrameDelta> clear() async {
    return _toNativeFrameDelta(await _requireEngine().clear());
  }

  @override
  Future<NativeFrameDelta> drainFrameDelta() async {
    return _toNativeFrameDelta(await _requireEngine().drainFrameDelta());
  }

  @override
  Future<NativeEngineStats> stats() async {
    return _toNativeEngineStats(await _requireEngine().stats());
  }

  @override
  Future<Uint8List> snapshotRgba() async {
    return Uint8List.fromList(await _requireEngine().snapshotRgba());
  }

  @override
  Future<int> addLayer() async {
    final id = await _requireEngine().addLayer();
    return id.toInt();
  }

  @override
  Future<void> deleteLayer(int id) async {
    await _requireEngine().deleteLayer(id: BigInt.from(id));
  }

  @override
  Future<void> moveLayer(int id, int newIndex) async {
    await _requireEngine().moveLayer(id: BigInt.from(id), newIndex: BigInt.from(newIndex));
  }

  @override
  Future<void> setLayerVisibility(int id, bool visible) async {
    await _requireEngine().setLayerVisibility(id: BigInt.from(id), visible: visible);
  }

  @override
  Future<void> setLayerOpacity(int id, double opacity) async {
    await _requireEngine().setLayerOpacity(id: BigInt.from(id), opacity: opacity);
  }

  @override
  Future<void> setActiveLayer(int id) async {
    await _requireEngine().setActiveLayer(id: BigInt.from(id));
  }

  @override
  Future<void> renameLayer(int id, String name) async {
    await _requireEngine().renameLayer(id: BigInt.from(id), name: name);
  }

  @override
  Future<List<NativeLayerInfo>> listLayers() async {
    final layers = await _requireEngine().listLayers();
    return layers.map((layer) => NativeLayerInfo(
      id: layer.id.toInt(),
      name: layer.name,
      visible: layer.visible,
      opacity: layer.opacity,
    )).toList();
  }

  @override
  Future<int> activeLayerId() async {
    final id = await _requireEngine().activeLayerId();
    return id.toInt();
  }

  api.FlossEngine _requireEngine() {
    final engine = _engine;
    if (engine == null) {
      throw StateError('Rust drawing engine has not been initialized.');
    }
    return engine;
  }
}

rust_brush.BrushPreset _toRustBrush(app.BrushPreset brush) {
  return rust_brush.BrushPreset(
    name: brush.name,
    size: brush.size,
    opacity: brush.opacity,
    hardness: brush.hardness,
    spacing: brush.spacing,
    colorArgb: colorToArgb(brush.color),
    blendMode: switch (brush.blendMode) {
      app.BrushBlendMode.normal => rust_brush.BrushBlendMode.normal,
      app.BrushBlendMode.multiply => rust_brush.BrushBlendMode.multiply,
      app.BrushBlendMode.screen => rust_brush.BrushBlendMode.screen,
      app.BrushBlendMode.overlay => rust_brush.BrushBlendMode.overlay,
    },
    pressureCurveExponent: brush.pressureCurveExponent,
    velocitySizeSensitivity: brush.velocitySizeSensitivity,
    velocityOpacitySensitivity: brush.velocityOpacitySensitivity,
  );
}

rust_stroke.StrokeSample _toRustSample(app.StrokeSample sample) {
  return rust_stroke.StrokeSample(
    x: sample.position.dx,
    y: sample.position.dy,
    pressure: sample.pressure,
    velocity: 0.0, // Velocity is computed server-side during resampling
    timeMicros: sample.timeStamp.inMicroseconds,
    pointer: sample.pointer,
  );
}

NativeFrameDelta _toNativeFrameDelta(rust_document.FrameDelta delta) {
  return NativeFrameDelta(
    dirtyTiles: delta.dirtyTiles.map(_toAppTileCoord).toList(growable: false),
    activeRenderSampleCount: delta.activeRenderSampleCount,
    committedStrokeCount: delta.committedStrokeCount,
  );
}

NativeEngineStats _toNativeEngineStats(rust_document.EngineStats stats) {
  return NativeEngineStats(
    width: stats.width,
    height: stats.height,
    tileSize: stats.tileSize,
    tileColumns: stats.tileColumns,
    tileRows: stats.tileRows,
    dirtyTileCount: stats.dirtyTileCount,
    committedStrokeCount: stats.committedStrokeCount,
    activeRawSampleCount: stats.activeRawSampleCount,
  );
}

app.TileCoord _toAppTileCoord(rust_tile.TileCoord tile) {
  return app.TileCoord(tile.x, tile.y);
}
