import 'dart:ui';

import 'package:flutter/foundation.dart';

enum BrushBlendMode { normal, multiply, screen, overlay }

@immutable
class BrushPreset {
  const BrushPreset({
    required this.name,
    required this.size,
    required this.opacity,
    required this.hardness,
    required this.spacing,
    required this.color,
    this.blendMode = BrushBlendMode.normal,
    this.pressureCurveExponent = 2.0,
    this.velocitySizeSensitivity = 0.5,
    this.velocityOpacitySensitivity = 0.3,
  });

  final String name;
  final double size;
  final double opacity;
  final double hardness;
  final double spacing;
  final Color color;
  final BrushBlendMode blendMode;
  /// Exponent for pressure curve. 1.0 = linear, 2.0 = quadratic.
  final double pressureCurveExponent;
  /// How much velocity reduces brush size (0.0–1.0).
  final double velocitySizeSensitivity;
  /// How much velocity reduces brush opacity (0.0–1.0).
  final double velocityOpacitySensitivity;

  BrushPreset copyWith({
    String? name,
    double? size,
    double? opacity,
    double? hardness,
    double? spacing,
    Color? color,
    BrushBlendMode? blendMode,
    double? pressureCurveExponent,
    double? velocitySizeSensitivity,
    double? velocityOpacitySensitivity,
  }) {
    return BrushPreset(
      name: name ?? this.name,
      size: size ?? this.size,
      opacity: opacity ?? this.opacity,
      hardness: hardness ?? this.hardness,
      spacing: spacing ?? this.spacing,
      color: color ?? this.color,
      blendMode: blendMode ?? this.blendMode,
      pressureCurveExponent: pressureCurveExponent ?? this.pressureCurveExponent,
      velocitySizeSensitivity: velocitySizeSensitivity ?? this.velocitySizeSensitivity,
      velocityOpacitySensitivity: velocityOpacitySensitivity ?? this.velocityOpacitySensitivity,
    );
  }
}
