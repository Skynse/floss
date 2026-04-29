import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../providers/tool_provider.dart';

// Intents
class BrushToolIntent extends Intent {
  const BrushToolIntent();
}

class EraserToolIntent extends Intent {
  const EraserToolIntent();
}

class PanCanvasIntent extends Intent {
  const PanCanvasIntent();
}

class ZoomInIntent extends Intent {
  const ZoomInIntent();
}

class ZoomOutIntent extends Intent {
  const ZoomOutIntent();
}

class ResetZoomIntent extends Intent {
  const ResetZoomIntent();
}

class UndoIntent extends Intent {
  const UndoIntent();
}

class RedoIntent extends Intent {
  const RedoIntent();
}

class BrushSizeDecreaseIntent extends Intent {
  const BrushSizeDecreaseIntent();
}

class BrushSizeIncreaseIntent extends Intent {
  const BrushSizeIncreaseIntent();
}

class SwapColorIntent extends Intent {
  const SwapColorIntent();
}

class SaveDocumentIntent extends Intent {
  const SaveDocumentIntent();
}

// Action handlers
class ShortcutActions {
  final void Function(StudioTool) selectTool;
  final VoidCallback zoomIn;
  final VoidCallback zoomOut;
  final VoidCallback resetZoom;
  final VoidCallback undo;
  final VoidCallback redo;
  final VoidCallback decreaseBrushSize;
  final VoidCallback increaseBrushSize;
  final VoidCallback swapColor;
  final VoidCallback saveDocument;

  const ShortcutActions({
    required this.selectTool,
    required this.zoomIn,
    required this.zoomOut,
    required this.resetZoom,
    required this.undo,
    required this.redo,
    required this.decreaseBrushSize,
    required this.increaseBrushSize,
    required this.swapColor,
    required this.saveDocument,
  });
}

class ShortcutHandler extends StatelessWidget {
  const ShortcutHandler({
    required this.child,
    required this.actions,
    super.key,
  });

  final Widget child;
  final ShortcutActions actions;

  @override
  Widget build(BuildContext context) {
    return Shortcuts(
      shortcuts: {
        const SingleActivator(LogicalKeyboardKey.keyB): const BrushToolIntent(),
        const SingleActivator(LogicalKeyboardKey.keyE): const EraserToolIntent(),
        const SingleActivator(LogicalKeyboardKey.space): const PanCanvasIntent(),
        const SingleActivator(LogicalKeyboardKey.equal, control: true): const ZoomInIntent(),
        const SingleActivator(LogicalKeyboardKey.minus, control: true): const ZoomOutIntent(),
        const SingleActivator(LogicalKeyboardKey.digit0, control: true): const ResetZoomIntent(),
        const SingleActivator(LogicalKeyboardKey.keyZ, control: true): const UndoIntent(),
        const SingleActivator(LogicalKeyboardKey.keyZ, control: true, shift: true): const RedoIntent(),
        const SingleActivator(LogicalKeyboardKey.bracketLeft): const BrushSizeDecreaseIntent(),
        const SingleActivator(LogicalKeyboardKey.bracketRight): const BrushSizeIncreaseIntent(),
        const SingleActivator(LogicalKeyboardKey.keyX): const SwapColorIntent(),
        const SingleActivator(LogicalKeyboardKey.keyS, control: true): const SaveDocumentIntent(),
      },
      child: Actions(
        actions: {
          BrushToolIntent: CallbackAction<BrushToolIntent>(
            onInvoke: (_) => actions.selectTool(StudioTool.brush),
          ),
          EraserToolIntent: CallbackAction<EraserToolIntent>(
            onInvoke: (_) => actions.selectTool(StudioTool.eraser),
          ),
          PanCanvasIntent: CallbackAction<PanCanvasIntent>(
            onInvoke: (_) {
              // Pan is handled by gesture system, this just prevents space from scrolling
              return null;
            },
          ),
          ZoomInIntent: CallbackAction<ZoomInIntent>(
            onInvoke: (_) => actions.zoomIn(),
          ),
          ZoomOutIntent: CallbackAction<ZoomOutIntent>(
            onInvoke: (_) => actions.zoomOut(),
          ),
          ResetZoomIntent: CallbackAction<ResetZoomIntent>(
            onInvoke: (_) => actions.resetZoom(),
          ),
          UndoIntent: CallbackAction<UndoIntent>(
            onInvoke: (_) => actions.undo(),
          ),
          RedoIntent: CallbackAction<RedoIntent>(
            onInvoke: (_) => actions.redo(),
          ),
          BrushSizeDecreaseIntent: CallbackAction<BrushSizeDecreaseIntent>(
            onInvoke: (_) => actions.decreaseBrushSize(),
          ),
          BrushSizeIncreaseIntent: CallbackAction<BrushSizeIncreaseIntent>(
            onInvoke: (_) => actions.increaseBrushSize(),
          ),
          SwapColorIntent: CallbackAction<SwapColorIntent>(
            onInvoke: (_) => actions.swapColor(),
          ),
          SaveDocumentIntent: CallbackAction<SaveDocumentIntent>(
            onInvoke: (_) => actions.saveDocument(),
          ),
        },
        child: Focus(
          autofocus: true,
          canRequestFocus: true,
          child: child,
        ),
      ),
    );
  }
}
