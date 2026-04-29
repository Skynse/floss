import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../bridge/native_engine_contract.dart';
import '../brush/brush_preset.dart';
import '../canvas/texture_canvas_surface.dart';
import '../config/app_data_directory.dart';
import '../config/config_service.dart';
import '../input/shortcut_handler.dart';
import '../providers/brush_provider.dart';
import '../providers/engine_provider.dart';
import '../providers/tool_provider.dart';

const _paperColor = Color(0xfff7f4ed);
const _canvasWidth = 1024;
const _canvasHeight = 1422;

enum _PanelKind { brush, color, layers }

class StudioShell extends ConsumerStatefulWidget {
  const StudioShell({super.key});

  @override
  ConsumerState<StudioShell> createState() => _StudioShellState();
}

class _StudioShellState extends ConsumerState<StudioShell> {
  _PanelKind? _openPanel = _PanelKind.brush;
  BrushPreset _lastPaintBrush = _brushPresets.first;
  double _zoomScale = 1.0;

  @override
  void initState() {
    super.initState();
    _initializeApp();
  }

  Future<void> _initializeApp() async {
    // Ensure app data directory exists
    await AppDataDirectory.getPath();
    // Load config (creates defaults if none exists)
    final config = await ConfigService.load();
    // TODO: Apply loaded config settings
    
    // Initialize engine
    await ref.read(engineProvider.notifier).initialize(
      const NativeEngineOptions(
        width: _canvasWidth,
        height: _canvasHeight,
        tileSize: 256,
      ),
    );
    final brush = ref.read(brushProvider);
    await ref.read(engineProvider.notifier).setBrush(brush);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      body: SafeArea(
        child: Stack(
          children: [
            const Positioned.fill(child: _StudioBackdrop()),
            Positioned.fill(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(86, 66, 44, 28),
                child: Center(
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 1040),
                    child: AspectRatio(
                      aspectRatio: 0.72,
                      child: DecoratedBox(
                        decoration: BoxDecoration(
                          boxShadow: const [
                            BoxShadow(
                              color: Color(0x66000000),
                              blurRadius: 36,
                              offset: Offset(0, 18),
                            ),
                          ],
                          border: Border.all(
                            color: const Color(0x22ffffff),
                          ),
                        ),
                        child: const ClipRect(
                          child: TextureCanvasSurface(
                            textureWidth: _canvasWidth,
                            textureHeight: _canvasHeight,
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
            Positioned(
              left: 18,
              top: 12,
              right: 18,
              child: _TopToolbar(
                openPanel: _openPanel,
                onToolSelected: _selectTool,
                onPanelToggled: _togglePanel,
              ),
            ),
            const Positioned(
              left: 18,
              top: 110,
              bottom: 54,
              child: _SideControls(),
            ),
            if (_openPanel == _PanelKind.brush)
              Positioned(
                top: 76,
                right: 70,
                bottom: 30,
                child: _BrushLibrary(
                  onBrushSelected: _selectBrush,
                ),
              ),
            if (_openPanel == _PanelKind.color)
              Positioned(
                right: 360,
                bottom: 44,
                child: _ColorPanel(
                  onColorSelected: _selectColor,
                ),
              ),
            if (_openPanel == _PanelKind.layers)
              const Positioned(
                top: 76,
                right: 70,
                child: _LayersPanel(),
              ),
            const Positioned(
              left: 18,
              right: 18,
              bottom: 10,
              child: _StatusStrip(),
            ),
          ],
        ),
      ),
    );
  }

  void _togglePanel(_PanelKind panel) {
    setState(() {
      _openPanel = _openPanel == panel ? null : panel;
    });
  }

  void _selectTool(StudioTool tool) {
    ref.read(toolProvider.notifier).state = tool;
    if (tool == StudioTool.brush) {
      ref.read(brushProvider.notifier).updateBrush(_lastPaintBrush);
    } else {
      final brush = ref.read(brushProvider);
      ref.read(brushProvider.notifier).updateBrush(
        brush.copyWith(
          name: 'Soft Eraser',
          opacity: 1,
          color: _paperColor,
          blendMode: BrushBlendMode.normal,
        ),
      );
    }
  }

  void _selectBrush(BrushPreset brush) {
    _lastPaintBrush = brush.copyWith(color: _lastPaintBrush.color);
    ref.read(toolProvider.notifier).state = StudioTool.brush;
    ref.read(brushProvider.notifier).updateBrush(_lastPaintBrush);
  }

  void _selectColor(Color color) {
    final brush = _lastPaintBrush.copyWith(color: color);
    _lastPaintBrush = brush;
    ref.read(toolProvider.notifier).state = StudioTool.brush;
    ref.read(brushProvider.notifier).updateBrush(brush);
  }
}

class _TopToolbar extends ConsumerWidget {
  const _TopToolbar({
    required this.openPanel,
    required this.onToolSelected,
    required this.onPanelToggled,
  });

  final _PanelKind? openPanel;
  final ValueChanged<StudioTool> onToolSelected;
  final ValueChanged<_PanelKind> onPanelToggled;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selectedTool = ref.watch(toolProvider);
    final brush = ref.watch(brushProvider);

    return _FloatingBar(
      height: 52,
      child: Row(
        children: [
          const Padding(
            padding: EdgeInsets.symmetric(horizontal: 14),
            child: Text(
              'Floss',
              style: TextStyle(fontSize: 17, fontWeight: FontWeight.w700),
            ),
          ),
          _ToolButton(
            tooltip: 'Brush library',
            selected: openPanel == _PanelKind.brush,
            icon: Icons.brush,
            onPressed: () => onPanelToggled(_PanelKind.brush),
          ),
          _ToolButton(
            tooltip: 'Paint',
            selected: selectedTool == StudioTool.brush,
            icon: Icons.draw,
            onPressed: () => onToolSelected(StudioTool.brush),
          ),
          _ToolButton(
            tooltip: 'Eraser',
            selected: selectedTool == StudioTool.eraser,
            icon: Icons.auto_fix_off,
            onPressed: () => onToolSelected(StudioTool.eraser),
          ),
          const Spacer(),
          _ToolButton(
            tooltip: 'Layers',
            selected: openPanel == _PanelKind.layers,
            icon: Icons.layers,
            onPressed: () => onPanelToggled(_PanelKind.layers),
          ),
          _ToolButton(
            tooltip: 'Clear canvas',
            selected: false,
            icon: Icons.delete_outline,
            onPressed: () => ref.read(engineProvider.notifier).clear(),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: _ColorWell(
              color: brush.color,
              selected: openPanel == _PanelKind.color,
              onTap: () => onPanelToggled(_PanelKind.color),
            ),
          ),
        ],
      ),
    );
  }
}

class _SideControls extends ConsumerWidget {
  const _SideControls();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final brush = ref.watch(brushProvider);

    return _FloatingBar(
      width: 50,
      padding: const EdgeInsets.symmetric(vertical: 12),
      child: Column(
        children: [
          Expanded(
            child: RotatedBox(
              quarterTurns: 3,
              child: Slider(
                value: brush.size.clamp(2, 96),
                min: 2,
                max: 96,
                onChanged: (value) {
                  ref.read(brushProvider.notifier).updateBrush(
                    brush.copyWith(size: value),
                  );
                },
              ),
            ),
          ),
          Text(
            brush.size.round().toString(),
            style: Theme.of(context).textTheme.labelSmall,
          ),
          const SizedBox(height: 16),
          Expanded(
            child: RotatedBox(
              quarterTurns: 3,
              child: Slider(
                value: brush.opacity.clamp(0.05, 1),
                min: 0.05,
                max: 1,
                onChanged: (value) {
                  ref.read(brushProvider.notifier).updateBrush(
                    brush.copyWith(opacity: value),
                  );
                },
              ),
            ),
          ),
          Text(
            '${(brush.opacity * 100).round()}%',
            style: Theme.of(context).textTheme.labelSmall,
          ),
          const SizedBox(height: 16),
          Expanded(
            child: RotatedBox(
              quarterTurns: 3,
              child: Slider(
                value: brush.pressureCurveExponent.clamp(1.0, 4.0),
                min: 1.0,
                max: 4.0,
                divisions: 6,
                label: brush.pressureCurveExponent.toStringAsFixed(1),
                onChanged: (value) {
                  ref.read(brushProvider.notifier).updateBrush(
                    brush.copyWith(pressureCurveExponent: value),
                  );
                },
              ),
            ),
          ),
          Text(
            'P ${brush.pressureCurveExponent.toStringAsFixed(1)}',
            style: Theme.of(context).textTheme.labelSmall,
          ),
          const SizedBox(height: 16),
          Expanded(
            child: RotatedBox(
              quarterTurns: 3,
              child: Slider(
                value: brush.velocitySizeSensitivity.clamp(0.0, 1.0),
                min: 0.0,
                max: 1.0,
                onChanged: (value) {
                  ref.read(brushProvider.notifier).updateBrush(
                    brush.copyWith(velocitySizeSensitivity: value),
                  );
                },
              ),
            ),
          ),
          Text(
            'V ${(brush.velocitySizeSensitivity * 100).round()}%',
            style: Theme.of(context).textTheme.labelSmall,
          ),
          const SizedBox(height: 16),
          Expanded(
            child: RotatedBox(
              quarterTurns: 3,
              child: Slider(
                value: brush.velocityOpacitySensitivity.clamp(0.0, 1.0),
                min: 0.0,
                max: 1.0,
                onChanged: (value) {
                  ref.read(brushProvider.notifier).updateBrush(
                    brush.copyWith(velocityOpacitySensitivity: value),
                  );
                },
              ),
            ),
          ),
          Text(
            'O ${(brush.velocityOpacitySensitivity * 100).round()}%',
            style: Theme.of(context).textTheme.labelSmall,
          ),
        ],
      ),
    );
  }
}

class _BrushLibrary extends ConsumerWidget {
  const _BrushLibrary({required this.onBrushSelected});

  final ValueChanged<BrushPreset> onBrushSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selectedTool = ref.watch(toolProvider);
    final currentBrush = ref.watch(brushProvider);

    return _FloatingPanel(
      width: 360,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const _PanelTitle(title: 'Brush Library'),
          const SizedBox(height: 10),
          Expanded(
            child: ListView.separated(
              itemCount: _brushPresets.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (context, index) {
                final preset = _brushPresets[index];
                final selected =
                    selectedTool == StudioTool.brush &&
                    currentBrush.name == preset.name;
                return _BrushPresetTile(
                  preset: preset,
                  selected: selected,
                  onTap: () => onBrushSelected(preset),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

class _BrushPresetTile extends StatelessWidget {
  const _BrushPresetTile({
    required this.preset,
    required this.selected,
    required this.onTap,
  });

  final BrushPreset preset;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: selected ? const Color(0xff2f7bff) : const Color(0xff191b1f),
      borderRadius: BorderRadius.circular(7),
      child: InkWell(
        borderRadius: BorderRadius.circular(7),
        onTap: onTap,
        child: SizedBox(
          height: 78,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(12, 10, 12, 8),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  preset.name,
                  style: const TextStyle(fontWeight: FontWeight.w700),
                ),
                const Spacer(),
                CustomPaint(
                  painter: _BrushPreviewPainter(preset),
                  size: const Size(double.infinity, 28),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _ColorPanel extends ConsumerWidget {
  const _ColorPanel({required this.onColorSelected});

  final ValueChanged<Color> onColorSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final brush = ref.watch(brushProvider);

    return _FloatingPanel(
      width: 300,
      height: 262,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const _PanelTitle(title: 'Colors'),
          const SizedBox(height: 18),
          Wrap(
            spacing: 12,
            runSpacing: 12,
            children: [
              for (final color in _swatches)
                _SwatchButton(
                  color: color,
                  selected: brush.color == color,
                  onTap: () => onColorSelected(color),
                ),
            ],
          ),
        ],
      ),
    );
  }
}

class _LayersPanel extends ConsumerWidget {
  const _LayersPanel();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final engineState = ref.watch(engineProvider);
    final layers = engineState.layers;

    return _FloatingPanel(
      width: 300,
      height: 320,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const _PanelTitle(title: 'Layers'),
              const Spacer(),
              IconButton(
                icon: const Icon(Icons.add, size: 18),
                tooltip: 'Add layer',
                onPressed: () => ref.read(engineProvider.notifier).addLayer(),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Expanded(
            child: ReorderableListView.builder(
              buildDefaultDragHandles: false,
              itemCount: layers.length,
              onReorder: (oldIndex, newIndex) {
                if (oldIndex < newIndex) newIndex--;
                ref.read(engineProvider.notifier).moveLayer(layers[oldIndex].id, newIndex);
              },
              itemBuilder: (context, index) {
                final layer = layers[index];
                final isActive = layer.id == engineState.activeLayerId;
                return _LayerTile(
                  key: ValueKey(layer.id),
                  layer: layer,
                  isActive: isActive,
                  onTap: () => ref.read(engineProvider.notifier).setActiveLayer(layer.id),
                  onVisibilityToggle: () => ref.read(engineProvider.notifier).setLayerVisibility(layer.id, !layer.visible),
                  onDelete: layers.length > 1
                      ? () => ref.read(engineProvider.notifier).deleteLayer(layer.id)
                      : null,
                  onOpacityChanged: (opacity) => ref.read(engineProvider.notifier).setLayerOpacity(layer.id, opacity),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

class _LayerTile extends StatelessWidget {
  const _LayerTile({
    super.key,
    required this.layer,
    required this.isActive,
    required this.onTap,
    required this.onVisibilityToggle,
    required this.onDelete,
    required this.onOpacityChanged,
  });

  final NativeLayerInfo layer;
  final bool isActive;
  final VoidCallback onTap;
  final VoidCallback onVisibilityToggle;
  final VoidCallback? onDelete;
  final ValueChanged<double> onOpacityChanged;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: isActive ? const Color(0xff2f7bff) : Colors.transparent,
      borderRadius: BorderRadius.circular(7),
      child: InkWell(
        borderRadius: BorderRadius.circular(7),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  IconButton(
                    icon: Icon(
                      layer.visible ? Icons.visibility : Icons.visibility_off,
                      size: 16,
                    ),
                    padding: EdgeInsets.zero,
                    constraints: const BoxConstraints(minWidth: 28, minHeight: 28),
                    onPressed: onVisibilityToggle,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      layer.name,
                      style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  if (onDelete != null)
                    IconButton(
                      icon: const Icon(Icons.delete_outline, size: 16),
                      padding: EdgeInsets.zero,
                      constraints: const BoxConstraints(minWidth: 28, minHeight: 28),
                      onPressed: onDelete,
                    ),
                ],
              ),
              const SizedBox(height: 4),
              Row(
                children: [
                  const SizedBox(width: 36),
                  Expanded(
                    child: Slider(
                      value: layer.opacity,
                      min: 0.0,
                      max: 1.0,
                      onChanged: onOpacityChanged,
                    ),
                  ),
                  SizedBox(
                    width: 36,
                    child: Text(
                      '${(layer.opacity * 100).round()}%',
                      style: Theme.of(context).textTheme.labelSmall,
                      textAlign: TextAlign.right,
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _StatusStrip extends ConsumerWidget {
  const _StatusStrip();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final engineState = ref.watch(engineProvider);
    final engineLabel = engineState.nativeReady ? 'rust' : 'dart';
    final error = engineState.nativeError;

    return Text(
      error == null
          ? 'engine $engineLabel   layers ${engineState.layers.length}'
          : 'engine dart fallback   $error',
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
      style: Theme.of(
        context,
      ).textTheme.labelSmall?.copyWith(color: const Color(0xff9ca3af)),
    );
  }
}

class _ToolButton extends StatelessWidget {
  const _ToolButton({
    required this.tooltip,
    required this.icon,
    required this.selected,
    required this.onPressed,
  });

  final String tooltip;
  final IconData icon;
  final bool selected;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    return IconButton(
      tooltip: tooltip,
      style: IconButton.styleFrom(
        backgroundColor: selected
            ? const Color(0xff2f7bff)
            : Colors.transparent,
        foregroundColor: selected ? Colors.white : const Color(0xffd8dbe0),
      ),
      onPressed: onPressed,
      icon: Icon(icon),
    );
  }
}

class _ColorWell extends StatelessWidget {
  const _ColorWell({
    required this.color,
    required this.selected,
    required this.onTap,
  });

  final Color color;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: 'Colors',
      child: InkWell(
        borderRadius: BorderRadius.circular(18),
        onTap: onTap,
        child: Container(
          width: 36,
          height: 36,
          decoration: BoxDecoration(
            color: color,
            shape: BoxShape.circle,
            border: Border.all(
              color: selected
                  ? const Color(0xff2f7bff)
                  : const Color(0xff8b9099),
              width: selected ? 3 : 2,
            ),
          ),
        ),
      ),
    );
  }
}

class _SwatchButton extends StatelessWidget {
  const _SwatchButton({
    required this.color,
    required this.selected,
    required this.onTap,
  });

  final Color color;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      borderRadius: BorderRadius.circular(20),
      onTap: onTap,
      child: Container(
        width: 38,
        height: 38,
        decoration: BoxDecoration(
          color: color,
          shape: BoxShape.circle,
          border: Border.all(
            color: selected ? Colors.white : const Color(0xff2f333a),
            width: selected ? 3 : 1,
          ),
        ),
      ),
    );
  }
}

class _PanelTitle extends StatelessWidget {
  const _PanelTitle({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) {
    return Text(
      title,
      style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w800),
    );
  }
}

class _FloatingBar extends StatelessWidget {
  const _FloatingBar({
    required this.child,
    this.width,
    this.height,
    this.padding = EdgeInsets.zero,
  });

  final Widget child;
  final double? width;
  final double? height;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(18),
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 18, sigmaY: 18),
        child: Container(
          width: width,
          height: height,
          padding: padding,
          decoration: BoxDecoration(
            color: const Color(0xdd202226),
            borderRadius: BorderRadius.circular(18),
            border: Border.all(color: const Color(0x22ffffff)),
          ),
          child: child,
        ),
      ),
    );
  }
}

class _FloatingPanel extends StatelessWidget {
  const _FloatingPanel({required this.child, required this.width, this.height});

  final Widget child;
  final double width;
  final double? height;

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(14),
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
        child: Container(
          width: width,
          height: height,
          padding: const EdgeInsets.all(18),
          decoration: BoxDecoration(
            color: const Color(0xee2a2c31),
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: const Color(0x22ffffff)),
            boxShadow: const [
              BoxShadow(
                color: Color(0x66000000),
                blurRadius: 28,
                offset: Offset(0, 16),
              ),
            ],
          ),
          child: child,
        ),
      ),
    );
  }
}

class _StudioBackdrop extends StatelessWidget {
  const _StudioBackdrop();

  @override
  Widget build(BuildContext context) {
    return CustomPaint(painter: _StudioBackdropPainter());
  }
}

class _StudioBackdropPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(
      Offset.zero & size,
      Paint()..color = const Color(0xff151719),
    );
    final gridPaint = Paint()
      ..color = const Color(0xff25282d)
      ..strokeWidth = 1;
    const step = 18.0;
    for (var x = 0.0; x < size.width; x += step) {
      canvas.drawLine(Offset(x, 0), Offset(x, size.height), gridPaint);
    }
    for (var y = 0.0; y < size.height; y += step) {
      canvas.drawLine(Offset(0, y), Offset(size.width, y), gridPaint);
    }
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

class _BrushPreviewPainter extends CustomPainter {
  const _BrushPreviewPainter(this.brush);

  final BrushPreset brush;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = Colors.white.withValues(alpha: brush.opacity)
      ..strokeCap = StrokeCap.round
      ..strokeWidth = brush.size.clamp(2, 20);
    final path = Path()
      ..moveTo(8, size.height * 0.62)
      ..cubicTo(
        size.width * 0.28,
        size.height * 0.92,
        size.width * 0.52,
        size.height * 0.08,
        size.width - 8,
        size.height * 0.38,
      );
    canvas.drawPath(path, paint);
  }

  @override
  bool shouldRepaint(_BrushPreviewPainter oldDelegate) {
    return oldDelegate.brush != brush;
  }
}

const _swatches = <Color>[
  Color(0xff111111),
  Color(0xffffffff),
  Color(0xffe53935),
  Color(0xffff8f00),
  Color(0xffffeb3b),
  Color(0xff43a047),
  Color(0xff00acc1),
  Color(0xff1e88e5),
  Color(0xff5e35b1),
  Color(0xffd81b60),
  Color(0xff795548),
  Color(0xff78909c),
];

final _brushPresets = <BrushPreset>[
  const BrushPreset(
    name: 'Technical Pen',
    size: 8,
    opacity: 1,
    hardness: 0.9,
    spacing: 0.12,
    color: Color(0xff111111),
  ),
  const BrushPreset(
    name: 'Studio Pen',
    size: 16,
    opacity: 0.92,
    hardness: 0.78,
    spacing: 0.16,
    color: Color(0xff111111),
  ),
  const BrushPreset(
    name: 'Soft Pencil',
    size: 22,
    opacity: 0.62,
    hardness: 0.35,
    spacing: 0.18,
    color: Color(0xff111111),
  ),
  const BrushPreset(
    name: 'Marker',
    size: 32,
    opacity: 0.72,
    hardness: 0.44,
    spacing: 0.22,
    color: Color(0xff111111),
  ),
  const BrushPreset(
    name: 'Airbrush',
    size: 46,
    opacity: 0.38,
    hardness: 0.18,
    spacing: 0.2,
    color: Color(0xff111111),
  ),
];
