import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';

import 'app_data_directory.dart';

class FlossConfig {
  final KeyboardShortcuts shortcuts;
  final BrushDefaults brushDefaults;
  final UiState uiState;
  final List<Color> recentColors;

  const FlossConfig({
    this.shortcuts = const KeyboardShortcuts(),
    this.brushDefaults = const BrushDefaults(),
    this.uiState = const UiState(),
    this.recentColors = const [],
  });

  Map<String, dynamic> toJson() => {
    'shortcuts': shortcuts.toJson(),
    'brushDefaults': brushDefaults.toJson(),
    'uiState': uiState.toJson(),
    'recentColors': recentColors.map((c) => c.value).toList(),
  };

  factory FlossConfig.fromJson(Map<String, dynamic> json) {
    return FlossConfig(
      shortcuts: KeyboardShortcuts.fromJson(json['shortcuts'] ?? {}),
      brushDefaults: BrushDefaults.fromJson(json['brushDefaults'] ?? {}),
      uiState: UiState.fromJson(json['uiState'] ?? {}),
      recentColors: (json['recentColors'] as List? ?? [])
          .map((v) => Color(v as int))
          .toList(),
    );
  }

  FlossConfig copyWith({
    KeyboardShortcuts? shortcuts,
    BrushDefaults? brushDefaults,
    UiState? uiState,
    List<Color>? recentColors,
  }) {
    return FlossConfig(
      shortcuts: shortcuts ?? this.shortcuts,
      brushDefaults: brushDefaults ?? this.brushDefaults,
      uiState: uiState ?? this.uiState,
      recentColors: recentColors ?? this.recentColors,
    );
  }
}

class KeyboardShortcuts {
  final String brushTool;
  final String eraserTool;
  final String panCanvas;
  final String zoomIn;
  final String zoomOut;
  final String resetZoom;
  final String undo;
  final String redo;
  final String brushSizeDecrease;
  final String brushSizeIncrease;
  final String swapColor;
  final String saveDocument;

  const KeyboardShortcuts({
    this.brushTool = 'b',
    this.eraserTool = 'e',
    this.panCanvas = 'space',
    this.zoomIn = 'ctrl+equal',
    this.zoomOut = 'ctrl+minus',
    this.resetZoom = 'ctrl+0',
    this.undo = 'ctrl+z',
    this.redo = 'ctrl+shift+z',
    this.brushSizeDecrease = 'bracket_left',
    this.brushSizeIncrease = 'bracket_right',
    this.swapColor = 'x',
    this.saveDocument = 'ctrl+s',
  });

  Map<String, dynamic> toJson() => {
    'brushTool': brushTool,
    'eraserTool': eraserTool,
    'panCanvas': panCanvas,
    'zoomIn': zoomIn,
    'zoomOut': zoomOut,
    'resetZoom': resetZoom,
    'undo': undo,
    'redo': redo,
    'brushSizeDecrease': brushSizeDecrease,
    'brushSizeIncrease': brushSizeIncrease,
    'swapColor': swapColor,
    'saveDocument': saveDocument,
  };

  factory KeyboardShortcuts.fromJson(Map<String, dynamic> json) {
    return KeyboardShortcuts(
      brushTool: json['brushTool'] ?? 'b',
      eraserTool: json['eraserTool'] ?? 'e',
      panCanvas: json['panCanvas'] ?? 'space',
      zoomIn: json['zoomIn'] ?? 'ctrl+equal',
      zoomOut: json['zoomOut'] ?? 'ctrl+minus',
      resetZoom: json['resetZoom'] ?? 'ctrl+0',
      undo: json['undo'] ?? 'ctrl+z',
      redo: json['redo'] ?? 'ctrl+shift+z',
      brushSizeDecrease: json['brushSizeDecrease'] ?? 'bracket_left',
      brushSizeIncrease: json['brushSizeIncrease'] ?? 'bracket_right',
      swapColor: json['swapColor'] ?? 'x',
      saveDocument: json['saveDocument'] ?? 'ctrl+s',
    );
  }
}

class BrushDefaults {
  final double size;
  final double opacity;
  final double hardness;
  final double spacing;

  const BrushDefaults({
    this.size = 8.0,
    this.opacity = 1.0,
    this.hardness = 0.9,
    this.spacing = 0.12,
  });

  Map<String, dynamic> toJson() => {
    'size': size,
    'opacity': opacity,
    'hardness': hardness,
    'spacing': spacing,
  };

  factory BrushDefaults.fromJson(Map<String, dynamic> json) {
    return BrushDefaults(
      size: (json['size'] ?? 8.0).toDouble(),
      opacity: (json['opacity'] ?? 1.0).toDouble(),
      hardness: (json['hardness'] ?? 0.9).toDouble(),
      spacing: (json['spacing'] ?? 0.12).toDouble(),
    );
  }
}

class UiState {
  final String? lastBrushPanel;
  final bool sideControlsVisible;

  const UiState({
    this.lastBrushPanel,
    this.sideControlsVisible = true,
  });

  Map<String, dynamic> toJson() => {
    'lastBrushPanel': lastBrushPanel,
    'sideControlsVisible': sideControlsVisible,
  };

  factory UiState.fromJson(Map<String, dynamic> json) {
    return UiState(
      lastBrushPanel: json['lastBrushPanel'],
      sideControlsVisible: json['sideControlsVisible'] ?? true,
    );
  }
}

class ConfigService {
  static FlossConfig? _cached;

  static Future<FlossConfig> load() async {
    if (_cached != null) return _cached!;
    
    final path = await AppDataDirectory.getConfigPath();
    final file = File(path);
    
    if (await file.exists()) {
      try {
        final content = await file.readAsString();
        final json = jsonDecode(content) as Map<String, dynamic>;
        _cached = FlossConfig.fromJson(json);
        return _cached!;
      } catch (e) {
        // If config is corrupted, return defaults
        _cached = const FlossConfig();
        return _cached!;
      }
    }
    
    // First run - create default config
    _cached = const FlossConfig();
    await save(_cached!);
    return _cached!;
  }

  static Future<void> save(FlossConfig config) async {
    _cached = config;
    final path = await AppDataDirectory.getConfigPath();
    final file = File(path);
    final json = const JsonEncoder.withIndent('  ').convert(config.toJson());
    await file.writeAsString(json);
  }

  static void clearCache() {
    _cached = null;
  }
}
