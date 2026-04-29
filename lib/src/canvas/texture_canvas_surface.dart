import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../providers/engine_provider.dart';
import 'drawing_surface.dart';
import 'stroke_sample.dart';

class TextureCanvasSurface extends ConsumerStatefulWidget {
  const TextureCanvasSurface({
    required this.textureWidth,
    required this.textureHeight,
    super.key,
  });

  final int textureWidth;
  final int textureHeight;

  @override
  ConsumerState<TextureCanvasSurface> createState() => _TextureCanvasSurfaceState();
}

class _TextureCanvasSurfaceState extends ConsumerState<TextureCanvasSurface>
    with TickerProviderStateMixin {
  final _textureBridge = const _TextureBridge();
  final List<StrokeSample> _pendingMoveSamples = <StrokeSample>[];
  int? _textureId;
  bool _textureUnavailable =
      kIsWeb || defaultTargetPlatform != TargetPlatform.linux;
  bool _flushScheduled = false;
  bool _textureUpdateScheduled = false;
  Ticker? _activeStrokeTicker;

  @override
  void initState() {
    super.initState();
    _createTexture();
  }

  @override
  void dispose() {
    _stopActiveStrokeTicker();
    final textureId = _textureId;
    if (textureId != null) {
      unawaited(_textureBridge.disposeTexture(textureId));
    }
    super.dispose();
  }

  void _startActiveStrokeTicker() {
    if (_activeStrokeTicker != null) return;
    _activeStrokeTicker = createTicker((_) => _scheduleTextureUpdate());
    _activeStrokeTicker!.start();
  }

  void _stopActiveStrokeTicker() {
    _activeStrokeTicker?.stop();
    _activeStrokeTicker?.dispose();
    _activeStrokeTicker = null;
  }

  @override
  Widget build(BuildContext context) {
    if (_textureUnavailable || _textureId == null) {
      return const DrawingSurface();
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        return Listener(
          behavior: HitTestBehavior.opaque,
          onPointerDown: (event) {
            _pendingMoveSamples.clear();
            _startActiveStrokeTicker();
            ref.read(engineProvider.notifier).beginStroke(
              _sampleFromEvent(context, event, constraints.biggest),
            );
          },
          onPointerMove: (event) {
            _pendingMoveSamples.add(
              _sampleFromEvent(context, event, constraints.biggest),
            );
            _scheduleMoveFlush();
          },
          onPointerUp: (event) {
            _flushPendingMoveSamples();
            ref.read(engineProvider.notifier).endStroke(
              _sampleFromEvent(context, event, constraints.biggest),
            );
            _stopActiveStrokeTicker();
            _scheduleTextureUpdate();
          },
          onPointerCancel: (_) {
            _pendingMoveSamples.clear();
            ref.read(engineProvider.notifier).cancelStroke();
            _stopActiveStrokeTicker();
            _scheduleTextureUpdate();
          },
          child: Texture(
            textureId: _textureId!,
            filterQuality: FilterQuality.low,
          ),
        );
      },
    );
  }

  Future<void> _createTexture() async {
    if (_textureUnavailable) {
      return;
    }
    try {
      final textureId = await _textureBridge.createTexture(
        width: widget.textureWidth,
        height: widget.textureHeight,
      );
      if (!mounted) {
        await _textureBridge.disposeTexture(textureId);
        return;
      }
      setState(() {
        _textureId = textureId;
      });
      _scheduleTextureUpdate();
    } on MissingPluginException {
      setState(() {
        _textureUnavailable = true;
      });
    } on PlatformException {
      setState(() {
        _textureUnavailable = true;
      });
    }
  }

  void _scheduleMoveFlush() {
    if (_flushScheduled) {
      return;
    }
    _flushScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _flushScheduled = false;
      _flushPendingMoveSamples();
    });
  }

  void _flushPendingMoveSamples() {
    if (_pendingMoveSamples.isEmpty) {
      return;
    }
    ref.read(engineProvider.notifier).appendStrokeSamples(
      List<StrokeSample>.of(_pendingMoveSamples),
    );
    _pendingMoveSamples.clear();
  }

  void _scheduleTextureUpdate() {
    if (_textureId == null || _textureUnavailable || _textureUpdateScheduled) {
      return;
    }
    _textureUpdateScheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      _textureUpdateScheduled = false;
      final textureId = _textureId;
      final pixels = await ref.read(engineProvider.notifier).snapshotRgba();
      if (!mounted || textureId == null || pixels == null) {
        return;
      }
      try {
        await _textureBridge.updateTexture(
          textureId: textureId,
          width: widget.textureWidth,
          height: widget.textureHeight,
          pixels: pixels,
        );
      } on PlatformException {
        if (mounted) {
          setState(() {
            _textureUnavailable = true;
          });
        }
      }
    });
  }

  StrokeSample _sampleFromEvent(
    BuildContext context,
    PointerEvent event,
    Size surfaceSize,
  ) {
    final renderBox = context.findRenderObject()! as RenderBox;
    final local = renderBox.globalToLocal(event.position);
    final scaleX = widget.textureWidth / surfaceSize.width;
    final scaleY = widget.textureHeight / surfaceSize.height;
    return StrokeSample(
      position: Offset(local.dx * scaleX, local.dy * scaleY),
      pressure: event.pressure == 0 ? 1 : event.pressure,
      timeStamp: event.timeStamp,
      pointer: event.pointer,
    );
  }
}

class _TextureBridge {
  const _TextureBridge();

  static const _channel = MethodChannel('floss/texture');

  Future<int> createTexture({required int width, required int height}) async {
    final textureId = await _channel.invokeMethod<int>('createTexture', {
      'width': width,
      'height': height,
    });
    if (textureId == null) {
      throw StateError('Texture plugin returned no texture ID.');
    }
    return textureId;
  }

  Future<void> updateTexture({
    required int textureId,
    required int width,
    required int height,
    required Uint8List pixels,
  }) {
    return _channel.invokeMethod<void>('updateTexture', {
      'textureId': textureId,
      'width': width,
      'height': height,
      'pixels': pixels,
    });
  }

  Future<void> disposeTexture(int textureId) {
    return _channel.invokeMethod<void>('disposeTexture', {
      'textureId': textureId,
    });
  }
}
