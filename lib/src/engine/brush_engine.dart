import 'dart:ui';

import '../brush/brush_preset.dart';
import '../canvas/stroke.dart';
import '../canvas/stroke_sample.dart';
import '../canvas/tile_grid.dart';

abstract interface class BrushEngine {
  CanvasTileGrid get tileGrid;

  BrushPreset get brush;

  Stroke? get activeStroke;

  List<Stroke> get committedStrokes;

  void setBrush(BrushPreset brush);

  void beginStroke(StrokeSample sample);

  void appendSample(StrokeSample sample);

  void appendSamples(List<StrokeSample> samples);

  void endStroke(StrokeSample sample);

  void cancelStroke();

  void clear();
}

class PrototypeBrushEngine implements BrushEngine {
  PrototypeBrushEngine({required Size canvasSize, BrushPreset? initialBrush})
    : tileGrid = CanvasTileGrid(canvasSize: canvasSize),
      _brush =
          initialBrush ??
          const BrushPreset(
            name: 'Round',
            size: 18,
            opacity: 0.88,
            hardness: 0.72,
            spacing: 0.18,
            color: Color(0xfff3f5ff),
          );

  @override
  final CanvasTileGrid tileGrid;

  BrushPreset _brush;
  Stroke? _activeStroke;
  int _nextStrokeId = 1;
  final List<Stroke> _committedStrokes = <Stroke>[];
  final List<StrokeSample> _rawSamples = <StrokeSample>[];
  final List<StrokeSample> _renderSamples = <StrokeSample>[];

  @override
  BrushPreset get brush => _brush;

  @override
  Stroke? get activeStroke => _activeStroke;

  @override
  List<Stroke> get committedStrokes =>
      List<Stroke>.unmodifiable(_committedStrokes);

  @override
  void setBrush(BrushPreset brush) {
    _brush = brush;
  }

  @override
  void beginStroke(StrokeSample sample) {
    _rawSamples
      ..clear()
      ..add(sample);
    _renderSamples
      ..clear()
      ..add(sample);
    _activeStroke = _buildStroke(id: _nextStrokeId, brush: _brush);
    _markStrokeDirty(_activeStroke);
  }

  @override
  void appendSample(StrokeSample sample) {
    appendSamples(<StrokeSample>[sample]);
  }

  @override
  void appendSamples(List<StrokeSample> samples) {
    if (samples.isEmpty) {
      return;
    }

    if (_activeStroke == null) {
      beginStroke(samples.first);
      if (samples.length == 1) {
        return;
      }
      _rawSamples.addAll(samples.skip(1));
    } else {
      _rawSamples.addAll(samples);
    }

    _renderSamples
      ..clear()
      ..addAll(
        _resampleCatmullRom(_rawSamples, spacing: _brush.size * _brush.spacing),
      );
    _activeStroke = _buildStroke(id: _nextStrokeId, brush: _brush);
    _markStrokeDirty(_activeStroke);
  }

  @override
  void endStroke(StrokeSample sample) {
    appendSamples(<StrokeSample>[sample]);
    final stroke = _activeStroke;
    if (stroke == null) {
      return;
    }
    _committedStrokes.add(stroke);
    _nextStrokeId += 1;
    _activeStroke = null;
    _rawSamples.clear();
    _renderSamples.clear();
  }

  @override
  void cancelStroke() {
    _activeStroke = null;
    _rawSamples.clear();
    _renderSamples.clear();
  }

  @override
  void clear() {
    _activeStroke = null;
    _committedStrokes.clear();
    _rawSamples.clear();
    _renderSamples.clear();
    tileGrid.clearDirtyTiles();
  }

  Stroke _buildStroke({required int id, required BrushPreset brush}) {
    return Stroke(
      id: id,
      brush: brush,
      rawSamples: List<StrokeSample>.unmodifiable(_rawSamples),
      renderSamples: List<StrokeSample>.unmodifiable(_renderSamples),
      bounds: _boundsFor(_renderSamples, brush.size),
    );
  }

  void _markStrokeDirty(Stroke? stroke) {
    if (stroke == null) {
      return;
    }
    tileGrid.markRectDirty(stroke.bounds);
  }

  Rect _boundsFor(List<StrokeSample> samples, double brushSize) {
    if (samples.isEmpty) {
      return Rect.zero;
    }

    var left = samples.first.position.dx;
    var top = samples.first.position.dy;
    var right = left;
    var bottom = top;
    for (final sample in samples.skip(1)) {
      left = sample.position.dx < left ? sample.position.dx : left;
      top = sample.position.dy < top ? sample.position.dy : top;
      right = sample.position.dx > right ? sample.position.dx : right;
      bottom = sample.position.dy > bottom ? sample.position.dy : bottom;
    }

    final inset = brushSize * 0.75;
    return Rect.fromLTRB(left, top, right, bottom).inflate(inset);
  }

  List<StrokeSample> _resampleCatmullRom(
    List<StrokeSample> samples, {
    required double spacing,
  }) {
    if (samples.length < 4) {
      return List<StrokeSample>.of(samples);
    }

    final output = <StrokeSample>[samples.first];
    for (var i = 0; i < samples.length - 1; i += 1) {
      final p0 = samples[_clampedIndex(i - 1, samples.length)];
      final p1 = samples[i];
      final p2 = samples[i + 1];
      final p3 = samples[_clampedIndex(i + 2, samples.length)];
      final distance = (p2.position - p1.position).distance;
      final steps = (distance / spacing.clamp(1, double.infinity))
          .ceil()
          .clamp(1, 96)
          .toInt();

      for (var step = 1; step <= steps; step += 1) {
        final t = step / steps;
        output.add(
          StrokeSample(
            position: _catmullRom(
              p0.position,
              p1.position,
              p2.position,
              p3.position,
              t,
            ),
            pressure: _lerpDouble(p1.pressure, p2.pressure, t),
            timeStamp: p2.timeStamp,
            pointer: p2.pointer,
          ),
        );
      }
    }

    return output;
  }

  int _clampedIndex(int index, int length) {
    return index.clamp(0, length - 1).toInt();
  }

  Offset _catmullRom(Offset p0, Offset p1, Offset p2, Offset p3, double t) {
    final t2 = t * t;
    final t3 = t2 * t;
    final x =
        0.5 *
        ((2 * p1.dx) +
            (-p0.dx + p2.dx) * t +
            (2 * p0.dx - 5 * p1.dx + 4 * p2.dx - p3.dx) * t2 +
            (-p0.dx + 3 * p1.dx - 3 * p2.dx + p3.dx) * t3);
    final y =
        0.5 *
        ((2 * p1.dy) +
            (-p0.dy + p2.dy) * t +
            (2 * p0.dy - 5 * p1.dy + 4 * p2.dy - p3.dy) * t2 +
            (-p0.dy + 3 * p1.dy - 3 * p2.dy + p3.dy) * t3);
    return Offset(x, y);
  }

  double _lerpDouble(double a, double b, double t) {
    return a + (b - a) * t;
  }
}
