import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter/foundation.dart';

@immutable
class TileCoord {
  const TileCoord(this.x, this.y);

  final int x;
  final int y;

  @override
  bool operator ==(Object other) {
    return other is TileCoord && other.x == x && other.y == y;
  }

  @override
  int get hashCode => Object.hash(x, y);

  @override
  String toString() => 'TileCoord($x, $y)';
}

class CanvasTileGrid {
  CanvasTileGrid({required this.canvasSize, this.tileSize = 256});

  final Size canvasSize;
  final int tileSize;
  final Set<TileCoord> _dirtyTiles = <TileCoord>{};

  int get columns => (canvasSize.width / tileSize).ceil();

  int get rows => (canvasSize.height / tileSize).ceil();

  Set<TileCoord> get dirtyTiles => Set<TileCoord>.unmodifiable(_dirtyTiles);

  Set<TileCoord> drainDirtyTiles() {
    final drainedTiles = Set<TileCoord>.of(_dirtyTiles);
    _dirtyTiles.clear();
    return drainedTiles;
  }

  void markRectDirty(Rect rect) {
    if (rect.isEmpty) {
      return;
    }

    final int minX = math.max(0, rect.left ~/ tileSize);
    final int minY = math.max(0, rect.top ~/ tileSize);
    final int maxX = math.min(columns - 1, rect.right ~/ tileSize);
    final int maxY = math.min(rows - 1, rect.bottom ~/ tileSize);

    for (var y = minY; y <= maxY; y += 1) {
      for (var x = minX; x <= maxX; x += 1) {
        _dirtyTiles.add(TileCoord(x, y));
      }
    }
  }

  void clearDirtyTiles() {
    _dirtyTiles.clear();
  }
}
