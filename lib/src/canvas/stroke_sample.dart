import 'dart:ui';

import 'package:flutter/foundation.dart';

@immutable
class StrokeSample {
  const StrokeSample({
    required this.position,
    required this.pressure,
    required this.timeStamp,
    required this.pointer,
  });

  final Offset position;
  final double pressure;
  final Duration timeStamp;
  final int pointer;
}
