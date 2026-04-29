import 'dart:ui';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:floss/main.dart';
import 'package:floss/src/canvas/stroke_sample.dart';
import 'package:floss/src/engine/brush_engine.dart';

void main() {
  testWidgets('studio shell renders the drawing workspace', (tester) async {
    await tester.pumpWidget(const ProviderScope(child: FlossApp()));

    expect(find.text('Floss'), findsOneWidget);
    expect(find.text('Brush Library'), findsOneWidget);
    expect(find.text('Technical Pen'), findsOneWidget);
  });

  test('prototype brush engine resamples strokes and tracks dirty tiles', () {
    final engine = PrototypeBrushEngine(canvasSize: const Size(1024, 1024));

    engine.beginStroke(_sample(0, 40, 40));
    engine.appendSample(_sample(1, 140, 45));
    engine.appendSample(_sample(2, 220, 90));
    engine.appendSample(_sample(3, 300, 120));
    engine.endStroke(_sample(4, 360, 160));

    expect(engine.committedStrokes, hasLength(1));
    expect(engine.committedStrokes.single.renderSamples.length, greaterThan(5));
    expect(engine.tileGrid.dirtyTiles, isNotEmpty);
  });
}

StrokeSample _sample(int index, double x, double y) {
  return StrokeSample(
    position: Offset(x, y),
    pressure: 1,
    timeStamp: Duration(milliseconds: index * 8),
    pointer: 1,
  );
}
