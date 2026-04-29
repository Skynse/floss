import 'package:flutter/material.dart';

import '../studio/studio_shell.dart';

class FlossApp extends StatelessWidget {
  const FlossApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Floss',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xff3d6cff),
          brightness: Brightness.dark,
        ),
        scaffoldBackgroundColor: const Color(0xff111318),
        useMaterial3: true,
      ),
      home: const StudioShell(),
    );
  }
}
