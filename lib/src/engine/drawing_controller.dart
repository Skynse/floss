import 'package:flutter/foundation.dart';

import '../brush/brush_preset.dart';
import '../bridge/native_engine_contract.dart';
import '../canvas/stroke.dart';
import '../canvas/stroke_sample.dart';
import 'brush_engine.dart';

class DrawingController extends ChangeNotifier {
  DrawingController({required this.engine, this.nativeEngine});

  final BrushEngine engine;
  final NativeDrawingEngine? nativeEngine;
  Future<void> _nativeQueue = Future<void>.value();
  bool _nativeReady = false;
  NativeFrameDelta _latestNativeDelta = NativeFrameDelta.empty;
  Object? _nativeError;
  List<NativeLayerInfo> _layers = [];
  int _activeLayerId = 1;

  BrushPreset get brush => engine.brush;

  Stroke? get activeStroke => engine.activeStroke;

  List<Stroke> get committedStrokes => engine.committedStrokes;

  int get dirtyTileCount => _nativeReady
      ? _latestNativeDelta.dirtyTiles.length
      : engine.tileGrid.dirtyTiles.length;

  bool get nativeReady => _nativeReady;

  Object? get nativeError => _nativeError;

  List<NativeLayerInfo> get layers => List.unmodifiable(_layers);

  int get activeLayerId => _activeLayerId;

  Future<void> initializeNative(NativeEngineOptions options) async {
    final native = nativeEngine;
    if (native == null) {
      return;
    }

    try {
      await native.initialize(options);
      await native.setBrush(engine.brush);
      _layers = await native.listLayers();
      _activeLayerId = await native.activeLayerId();
      _nativeReady = true;
      _nativeError = null;
    } catch (error) {
      _nativeReady = false;
      _nativeError = error;
    }
    notifyListeners();
  }

  void updateBrush(BrushPreset brush) {
    engine.setBrush(brush);
    _enqueueNative((native) async {
      await native.setBrush(brush);
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  void beginStroke(StrokeSample sample) {
    engine.beginStroke(sample);
    _enqueueNative((native) => native.beginStroke(sample));
    notifyListeners();
  }

  void appendSample(StrokeSample sample) {
    engine.appendSample(sample);
    notifyListeners();
  }

  void appendSamples(List<StrokeSample> samples) {
    if (samples.isEmpty) {
      return;
    }
    engine.appendSamples(samples);
    _enqueueNative((native) => native.appendStrokeSamples(samples));
    notifyListeners();
  }

  void endStroke(StrokeSample sample) {
    engine.endStroke(sample);
    _enqueueNative((native) => native.endStroke(sample));
    notifyListeners();
  }

  void cancelStroke() {
    engine.cancelStroke();
    _enqueueNative((native) => native.cancelStroke());
    notifyListeners();
  }

  void clear() {
    engine.clear();
    _enqueueNative((native) => native.clear());
    notifyListeners();
  }

  Future<void> addLayer() async {
    _enqueueNative((native) async {
      final id = await native.addLayer();
      _layers = await native.listLayers();
      _activeLayerId = id;
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  Future<void> deleteLayer(int id) async {
    _enqueueNative((native) async {
      await native.deleteLayer(id);
      _layers = await native.listLayers();
      _activeLayerId = await native.activeLayerId();
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  Future<void> moveLayer(int id, int newIndex) async {
    _enqueueNative((native) async {
      await native.moveLayer(id, newIndex);
      _layers = await native.listLayers();
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  Future<void> setLayerVisibility(int id, bool visible) async {
    _enqueueNative((native) async {
      await native.setLayerVisibility(id, visible);
      _layers = await native.listLayers();
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  Future<void> setLayerOpacity(int id, double opacity) async {
    _enqueueNative((native) async {
      await native.setLayerOpacity(id, opacity);
      _layers = await native.listLayers();
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  Future<void> setActiveLayer(int id) async {
    _enqueueNative((native) async {
      await native.setActiveLayer(id);
      _activeLayerId = id;
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  Future<void> renameLayer(int id, String name) async {
    _enqueueNative((native) async {
      await native.renameLayer(id, name);
      _layers = await native.listLayers();
      return NativeFrameDelta.empty;
    });
    notifyListeners();
  }

  Future<Uint8List?> snapshotRgba() async {
    final native = nativeEngine;
    if (native == null || !_nativeReady) {
      return null;
    }
    try {
      return await native.snapshotRgba();
    } catch (error) {
      _nativeError = error;
      notifyListeners();
      return null;
    }
  }

  void _enqueueNative(
    Future<NativeFrameDelta> Function(NativeDrawingEngine native) action,
  ) {
    final native = nativeEngine;
    if (native == null || !_nativeReady) {
      return;
    }

    _nativeQueue = _nativeQueue.then((_) async {
      try {
        _latestNativeDelta = await action(native);
        _nativeError = null;
      } catch (error) {
        _nativeError = error;
      }
      notifyListeners();
    });
  }
}
