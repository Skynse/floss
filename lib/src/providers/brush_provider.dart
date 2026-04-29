import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../brush/brush_preset.dart';
import 'engine_provider.dart';

final brushProvider = StateNotifierProvider<BrushNotifier, BrushPreset>((ref) {
  return BrushNotifier(ref);
});

class BrushNotifier extends StateNotifier<BrushPreset> {
  BrushNotifier(this._ref) : super(_defaultBrush);

  final Ref _ref;

  static const _defaultBrush = BrushPreset(
    name: 'Technical Pen',
    size: 8,
    opacity: 1,
    hardness: 0.9,
    spacing: 0.12,
    color: Color(0xff111111),
  );

  void updateBrush(BrushPreset brush) {
    state = brush;
    _ref.read(engineProvider.notifier).setBrush(brush);
  }
}
