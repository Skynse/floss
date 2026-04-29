import 'package:flutter_riverpod/flutter_riverpod.dart';

enum StudioTool { brush, eraser }

final toolProvider = StateProvider<StudioTool>((ref) => StudioTool.brush);
