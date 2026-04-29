import 'dart:ui';

import 'package:flutter/foundation.dart';

import '../brush/brush_preset.dart';
import 'stroke_sample.dart';

@immutable
class Stroke {
  const Stroke({
    required this.id,
    required this.brush,
    required this.rawSamples,
    required this.renderSamples,
    required this.bounds,
  });

  final int id;
  final BrushPreset brush;
  final List<StrokeSample> rawSamples;
  final List<StrokeSample> renderSamples;
  final Rect bounds;
}
