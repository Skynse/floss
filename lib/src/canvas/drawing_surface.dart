import 'package:flutter/material.dart';

class DrawingSurface extends StatelessWidget {
  const DrawingSurface({super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      color: const Color(0xfff7f4ed),
      child: const Center(
        child: Text(
          'Texture rendering unavailable',
          style: TextStyle(color: Color(0xff666666)),
        ),
      ),
    );
  }
}
