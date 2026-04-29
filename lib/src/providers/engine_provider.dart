import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../bridge/native_engine_contract.dart';
import '../bridge/rust_native_drawing_engine.dart';
import '../brush/brush_preset.dart';
import '../canvas/stroke_sample.dart';

final engineProvider = StateNotifierProvider<EngineNotifier, EngineState>((ref) {
  return EngineNotifier();
});

class EngineState {
  const EngineState({
    this.nativeReady = false,
    this.nativeError,
    this.layers = const [],
    this.activeLayerId = 1,
  });

  final bool nativeReady;
  final Object? nativeError;
  final List<NativeLayerInfo> layers;
  final int activeLayerId;

  EngineState copyWith({
    bool? nativeReady,
    Object? nativeError,
    List<NativeLayerInfo>? layers,
    int? activeLayerId,
  }) {
    return EngineState(
      nativeReady: nativeReady ?? this.nativeReady,
      nativeError: nativeError ?? this.nativeError,
      layers: layers ?? this.layers,
      activeLayerId: activeLayerId ?? this.activeLayerId,
    );
  }
}

class EngineNotifier extends StateNotifier<EngineState> {
  EngineNotifier() : super(const EngineState());

  NativeDrawingEngine? _native;
  Future<void> _queue = Future.value();

  Future<void> initialize(NativeEngineOptions options) async {
    if (_native != null) return;

    final native = RustNativeDrawingEngine();
    try {
      await native.initialize(options);
      _native = native;
      final layers = await native.listLayers();
      final activeId = await native.activeLayerId();
      state = state.copyWith(
        nativeReady: true,
        nativeError: null,
        layers: layers,
        activeLayerId: activeId,
      );
    } catch (error) {
      state = state.copyWith(nativeReady: false, nativeError: error);
    }
  }

  Future<void> setBrush(BrushPreset brush) async {
    _enqueue((native) async {
      await native.setBrush(brush);
    });
  }

  Future<void> beginStroke(StrokeSample sample) async {
    _enqueue((native) async {
      await native.beginStroke(sample);
    });
  }

  Future<void> appendStrokeSamples(List<StrokeSample> samples) async {
    if (samples.isEmpty) return;
    _enqueue((native) async {
      await native.appendStrokeSamples(samples);
    });
  }

  Future<void> endStroke(StrokeSample sample) async {
    _enqueue((native) async {
      await native.endStroke(sample);
    });
  }

  Future<void> cancelStroke() async {
    _enqueue((native) async {
      await native.cancelStroke();
    });
  }

  Future<void> clear() async {
    _enqueue((native) async {
      await native.clear();
    });
  }

  Future<Uint8List?> snapshotRgba() async {
    final native = _native;
    if (native == null || !state.nativeReady) return null;
    try {
      return await native.snapshotRgba();
    } catch (error) {
      state = state.copyWith(nativeError: error);
      return null;
    }
  }

  // Layer operations
  Future<void> addLayer() async {
    _enqueue((native) async {
      final id = await native.addLayer();
      final layers = await native.listLayers();
      state = state.copyWith(layers: layers, activeLayerId: id);
    });
  }

  Future<void> deleteLayer(int id) async {
    _enqueue((native) async {
      await native.deleteLayer(id);
      final layers = await native.listLayers();
      final activeId = await native.activeLayerId();
      state = state.copyWith(layers: layers, activeLayerId: activeId);
    });
  }

  Future<void> moveLayer(int id, int newIndex) async {
    _enqueue((native) async {
      await native.moveLayer(id, newIndex);
      final layers = await native.listLayers();
      state = state.copyWith(layers: layers);
    });
  }

  Future<void> setLayerVisibility(int id, bool visible) async {
    _enqueue((native) async {
      await native.setLayerVisibility(id, visible);
      final layers = await native.listLayers();
      state = state.copyWith(layers: layers);
    });
  }

  Future<void> setLayerOpacity(int id, double opacity) async {
    _enqueue((native) async {
      await native.setLayerOpacity(id, opacity);
      final layers = await native.listLayers();
      state = state.copyWith(layers: layers);
    });
  }

  Future<void> setActiveLayer(int id) async {
    _enqueue((native) async {
      await native.setActiveLayer(id);
      state = state.copyWith(activeLayerId: id);
    });
  }

  Future<void> renameLayer(int id, String name) async {
    _enqueue((native) async {
      await native.renameLayer(id, name);
      final layers = await native.listLayers();
      state = state.copyWith(layers: layers);
    });
  }

  void _enqueue(Future<void> Function(NativeDrawingEngine native) action) {
    final native = _native;
    if (native == null || !state.nativeReady) return;

    _queue = _queue.then((_) async {
      try {
        await action(native);
        state = state.copyWith(nativeError: null);
      } catch (error) {
        state = state.copyWith(nativeError: error);
      }
    });
  }
}
