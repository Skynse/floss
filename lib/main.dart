import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'src/app/floss_app.dart';

export 'src/app/floss_app.dart';

void main() {
  runApp(const ProviderScope(child: FlossApp()));
}
