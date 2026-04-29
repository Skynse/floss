import 'dart:typed_data';
import 'dart:ui';

import '../brush/brush_preset.dart';
import '../canvas/stroke_sample.dart';
import '../canvas/tile_grid.dart';

class NativeEngineOptions {
  const NativeEngineOptions({
    required this.width,
    required this.height,
    required this.tileSize,
  });

  final int width;
  final int height;
  final int tileSize;
}

class NativeFrameDelta {
  const NativeFrameDelta({
    required this.dirtyTiles,
    required this.activeRenderSampleCount,
    required this.committedStrokeCount,
  });

  final List<TileCoord> dirtyTiles;
  final int activeRenderSampleCount;
  final int committedStrokeCount;

  static const empty = NativeFrameDelta(
    dirtyTiles: <TileCoord>[],
    activeRenderSampleCount: 0,
    committedStrokeCount: 0,
  );
}

class NativeEngineStats {
  const NativeEngineStats({
    required this.width,
    required this.height,
    required this.tileSize,
    required this.tileColumns,
    required this.tileRows,
    required this.dirtyTileCount,
    required this.committedStrokeCount,
    required this.activeRawSampleCount,
  });

  final int width;
  final int height;
  final int tileSize;
  final int tileColumns;
  final int tileRows;
  final int dirtyTileCount;
  final int committedStrokeCount;
  final int activeRawSampleCount;
}

class NativeLayerInfo {
  const NativeLayerInfo({
    required this.id,
    required this.name,
    required this.visible,
    required this.opacity,
  });

  final int id;
  final String name;
  final bool visible;
  final double opacity;
}

abstract interface class NativeDrawingEngine {
  Future<void> initialize(NativeEngineOptions options);

  Future<void> setBrush(BrushPreset brush);

  Future<NativeFrameDelta> beginStroke(StrokeSample sample);

  Future<NativeFrameDelta> appendStrokeSamples(List<StrokeSample> samples);

  Future<NativeFrameDelta> endStroke(StrokeSample sample);

  Future<NativeFrameDelta> cancelStroke();

  Future<NativeFrameDelta> clear();

  Future<NativeFrameDelta> drainFrameDelta();

  Future<NativeEngineStats> stats();

  Future<Uint8List> snapshotRgba();

  // Layer management
  Future<int> addLayer();

  Future<void> deleteLayer(int id);

  Future<void> moveLayer(int id, int newIndex);

  Future<void> setLayerVisibility(int id, bool visible);

  Future<void> setLayerOpacity(int id, double opacity);

  Future<void> setActiveLayer(int id);

  Future<void> renameLayer(int id, String name);

  Future<List<NativeLayerInfo>> listLayers();

  Future<int> activeLayerId();
}

int colorToArgb(Color color) {
  final alpha = (color.a * 255).round() & 0xff;
  final red = (color.r * 255).round() & 0xff;
  final green = (color.g * 255).round() & 0xff;
  final blue = (color.b * 255).round() & 0xff;
  return alpha << 24 | red << 16 | green << 8 | blue;
}
