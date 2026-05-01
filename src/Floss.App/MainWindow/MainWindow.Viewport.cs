using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App;

public partial class MainWindow
{
    // ── Viewport ──────────────────────────────────────────────────────────────
    private void SyncCanvasFrameToDocument(bool fitToViewport)
    {
        var w = Math.Max(1, _canvas.Document.Width);
        var h = Math.Max(1, _canvas.Document.Height);
        _canvasFrame.Width = w;
        _canvasFrame.Height = h;

        if (fitToViewport)
            ResetView();
    }

    private void Workspace_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var f = App.Shortcuts.ZoomScrollFactor;
        var factor = e.Delta.Y > 0 ? f : 1.0 / f;
        SetZoom(_zoom * factor, e.GetPosition(_workspaceViewport));
        e.Handled = true;
    }

    private void Workspace_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_workspaceViewport);
        var middle = pt.Properties.IsMiddleButtonPressed;
        if (_activeGesture == GestureMode.None)
        {
            var (gesture, gestureBinding) = DetectGesture(Key.None, e.KeyModifiers, App.Shortcuts);
            if (gesture != GestureMode.None && gestureBinding?.IsModifierOnly == true)
            {
                BeginGesture(gesture, Key.None, gestureBinding);
            }
        }
        if (!middle && _activeGesture == GestureMode.None) return;
        _isPanning = true;
        _lastPanPoint = e.GetPosition(_workspaceViewport);
        _gestureStartPoint = _lastPanPoint;
        if (_activeGesture == GestureMode.BrushSize)
        {
            _canvas.LockCursorPreview(e.GetPosition(_canvas), forceBrushOutline: true);
        }
        e.Pointer.Capture(_workspaceViewport);
        e.Handled = true;
    }

    private void Workspace_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var pt = e.GetPosition(_workspaceViewport);
        var d = pt - _lastPanPoint;
        _lastPanPoint = pt;
        var sc = App.Shortcuts;

        switch (_activeGesture)
        {
            case GestureMode.Pan:
            case GestureMode.None: // middle-mouse pan
                _canvasPan.X += d.X;
                _canvasPan.Y += d.Y;
                ClampCanvasPan();
                break;
            case GestureMode.Zoom:
                var axisDelta = sc.GestureZoomAxis == GestureAxis.Horizontal ? d.X : -d.Y;
                SetZoom(_zoom * Math.Pow(sc.GestureZoomSensitivity, axisDelta), _gestureStartPoint);
                break;
            case GestureMode.Rotate:
                SetRotation(_rotation + d.X * sc.GestureRotateSensitivity);
                break;
            case GestureMode.BrushSize:
                _sizeSlider.Value = Math.Clamp(
                    _sizeSlider.Value + d.X * sc.GestureSizeSensitivity,
                    _sizeSlider.Minimum, _sizeSlider.Maximum);
                break;
        }
        e.Handled = true;
    }

    private void Workspace_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        _canvas.UnlockCursorPreview();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // pan_new = (cursor - vpCenter) * (1 - ratio) + pan_old * ratio
    private void SetZoom(double newZoom, Point? cursor)
    {
        var oldZoom = _zoom;
        _zoom = Math.Clamp(newZoom, 0.05, 16.0);
        _canvasScale.ScaleX = _zoom;
        _canvasScale.ScaleY = _zoom;
        _canvas.CanvasZoom = _zoom;

        if (cursor.HasValue && oldZoom > 0)
        {
            var ratio = _zoom / oldZoom;
            var vpW = _workspaceViewport.Bounds.Width;
            var vpH = _workspaceViewport.Bounds.Height;
            var c = cursor.Value;
            _canvasPan.X = (c.X - vpW * 0.5) * (1 - ratio) + _canvasPan.X * ratio;
            _canvasPan.Y = (c.Y - vpH * 0.5) * (1 - ratio) + _canvasPan.Y * ratio;
        }

        ClampCanvasPan();
        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        UpdateStatus();
    }

    private void SetRotation(double degrees)
    {
        _rotation = degrees % 360;
        _canvasRotate.Angle = _rotation;
        ClampCanvasPan();
        _rotDisplay.Text = $"{Math.Round(_rotation)}°";
        UpdateStatus();
    }

    private void ResetView()
    {
        _rotation = 0;
        _canvasRotate.Angle = 0;

        var w = Math.Max(1, _canvas.Document.Width);
        var h = Math.Max(1, _canvas.Document.Height);
        var vpW = Math.Max(1, _workspaceViewport.Bounds.Width);
        var vpH = Math.Max(1, _workspaceViewport.Bounds.Height);
        var outset = Math.Min(ResetViewOutset, Math.Min(vpW, vpH) * 0.2);
        var availableW = Math.Max(1, vpW - outset * 2);
        var availableH = Math.Max(1, vpH - outset * 2);

        _zoom = Math.Clamp(Math.Min(availableW / w, availableH / h), 0.05, 16.0);
        _canvasScale.ScaleX = _zoom;
        _canvasScale.ScaleY = _zoom;
        _canvas.CanvasZoom = _zoom;
        _canvasPan.X = 0;
        _canvasPan.Y = 0;
        ClampCanvasPan();

        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        _rotDisplay.Text = "0°";
        UpdateStatus();
    }

    private void ClampCanvasPan()
    {
        var vpW = Math.Max(1, _workspaceViewport.Bounds.Width);
        var vpH = Math.Max(1, _workspaceViewport.Bounds.Height);
        if (vpW <= 1 || vpH <= 1) return;

        var angle = Math.Abs(_rotation % 180) * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));
        var docW = Math.Max(1, _canvas.Document.Width) * _zoom;
        var docH = Math.Max(1, _canvas.Document.Height) * _zoom;
        var rotatedW = docW * cos + docH * sin;
        var rotatedH = docW * sin + docH * cos;

        var marginX = Math.Min(vpW * 0.45, 360);
        var marginY = Math.Min(vpH * 0.45, 360);
        var maxX = Math.Max(marginX, (rotatedW - vpW) * 0.5 + marginX);
        var maxY = Math.Max(marginY, (rotatedH - vpH) * 0.5 + marginY);

        _canvasPan.X = Math.Clamp(_canvasPan.X, -maxX, maxX);
        _canvasPan.Y = Math.Clamp(_canvasPan.Y, -maxY, maxY);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        var mods = Floss.App.Input.KeyBinding.ModifiersWithKeyDown(key, e.KeyModifiers);
        var sc = App.Shortcuts;

        // Pen gestures take priority — they suspend drawing and activate a drag mode
        var (gesture, gestureBinding) = DetectGesture(key, mods, sc);
        if (gesture != GestureMode.None)
        {
            BeginGesture(gesture, key, gestureBinding);
            e.Handled = true;
            return;
        }

        if (sc.Undo.Matches(key, mods)) { _canvas.Undo(); e.Handled = true; }
        else if (sc.Redo.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.RedoAlt.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.FileSave.Matches(key, mods)) { _ = SaveFlossAsync(); e.Handled = true; }
        else if (sc.FileOpen.Matches(key, mods)) { _ = OpenDocumentAsync(); e.Handled = true; }
        else if (sc.LayerNew.Matches(key, mods)) { _canvas.AddLayer(); e.Handled = true; }
        else if (sc.LayerDuplicate.Matches(key, mods)) { _canvas.DuplicateLayer(); e.Handled = true; }
        else if (sc.LayerDelete.Matches(key, mods)) { _canvas.DeleteLayer(); e.Handled = true; }
        else if (sc.LayerMoveUp.Matches(key, mods)) { _canvas.MoveActiveLayer(1); e.Handled = true; }
        else if (sc.LayerMoveDown.Matches(key, mods)) { _canvas.MoveActiveLayer(-1); e.Handled = true; }
        else if (sc.LayerMerge.Matches(key, mods)) { _canvas.MergeDown(_selectedLayerIndices.Count > 1 ? _selectedLayerIndices.OrderBy(x => x).ToList() : null); e.Handled = true; }
        else if (sc.ZoomReset.Matches(key, mods)) { ResetView(); e.Handled = true; }
        else if (sc.ZoomFit.Matches(key, mods)) { SyncCanvasFrameToDocument(fitToViewport: true); e.Handled = true; }
        else if (sc.ZoomIn.Matches(key, mods) || sc.ZoomInAlt.Matches(key, mods))
        { SetZoom(_zoom * sc.ZoomKeyFactor, null); e.Handled = true; }
        else if (sc.ZoomOut.Matches(key, mods))
        { SetZoom(_zoom / sc.ZoomKeyFactor, null); e.Handled = true; }
        else if (sc.RotateReset.Matches(key, mods)) { SetRotation(0); e.Handled = true; }
        else if (sc.RotateLeft.Matches(key, mods)) { SetRotation(_rotation - sc.RotateKeyStep); e.Handled = true; }
        else if (sc.RotateRight.Matches(key, mods)) { SetRotation(_rotation + sc.RotateKeyStep); e.Handled = true; }
        else if (sc.ToolBrush.Matches(key, mods)) { SetTool("brush"); e.Handled = true; }
        else if (sc.ToolEraser.Matches(key, mods)) { SetTool("eraser"); e.Handled = true; }
        else if (sc.ToolMove.Matches(key, mods)) { ActivateTool(_moveTool, _moveToolButton); e.Handled = true; }
        else if (sc.ToolSelect.Matches(key, mods)) { ActivateTool(_selectTool, _selectToolButton); e.Handled = true; }
        else if (sc.ToolWand.Matches(key, mods)) { ActivateTool(_magicWandTool, _wandToolButton); e.Handled = true; }
        else if (sc.ToolFill.Matches(key, mods)) { ActivateTool(_fillTool, _fillToolButton); e.Handled = true; }
        else if (sc.ToolLasso.Matches(key, mods)) { ActivateTool(_lassoFillTool, _lassoFillToolButton); e.Handled = true; }
        else if (sc.ToolEyedropper.Matches(key, mods)) { ActivateTool(_eyedropperTool, _eyedropToolButton); e.Handled = true; }
        else if (sc.ToolSmudge.Matches(key, mods)) { ActivateTool(_canvas.SmudgeTool, _smudgeToolButton); e.Handled = true; }
        else if (sc.ToolTransform.Matches(key, mods)) { _canvas.BeginSelectionTransform(); UpdateStatus(); e.Handled = true; }
        else if (sc.SelectAll.Matches(key, mods)) { _canvas.SelectAll(); e.Handled = true; }
        else if (sc.Deselect.Matches(key, mods)) { _canvas.Deselect(); e.Handled = true; }
        else if (sc.InvertSelect.Matches(key, mods)) { _canvas.InvertSelection(); e.Handled = true; }
        else if ((key == Key.LeftAlt || key == Key.RightAlt) && mods == KeyModifiers.Alt)
        {
            if (_preAltTool == null && _canvas.ActiveTool != _eyedropperTool)
            {
                _preAltTool = _canvas.ActiveTool;
                _preAltToolButton = _activeToolButton ?? _brushToolButton;
                ActivateTool(_eyedropperTool, _eyedropToolButton);
            }
            e.Handled = true;
        }
        else if (key == Key.Escape)
        { _canvas.CancelActiveTool(); e.Handled = true; }
        else if ((key == Key.Return || key == Key.Enter) && _canvas.ActiveTool is SelectTool or PolylineTool or TransformTool)
        { _canvas.CommitActiveTool(); e.Handled = true; }
        else if (sc.ColorCycle.Matches(key, mods)) { CycleColor(); e.Handled = true; }
        else if (sc.ColorDefault.Matches(key, mods)) { SetColor(Color.Parse("#111111")); e.Handled = true; }
        else if (sc.OpenSettings.Matches(key, mods)) { OpenSettings(); e.Handled = true; }
        else if (sc.OpenBrushEditor.Matches(key, mods)) { OpenBrushEditor(); e.Handled = true; }
        else if (sc.BrushSizeDecrease.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Max(_sizeSlider.Minimum, _sizeSlider.Value - sc.BrushSizeStep);
            e.Handled = true;
        }
        else if (sc.BrushSizeIncrease.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Min(_sizeSlider.Maximum, _sizeSlider.Value + sc.BrushSizeStep);
            e.Handled = true;
        }
        else if (sc.BrushSizeDecreaseLarge.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Max(_sizeSlider.Minimum, _sizeSlider.Value - sc.BrushSizeStepLarge);
            e.Handled = true;
        }
        else if (sc.BrushSizeIncreaseLarge.Matches(key, mods))
        {
            _sizeSlider.Value = Math.Min(_sizeSlider.Maximum, _sizeSlider.Value + sc.BrushSizeStepLarge);
            e.Handled = true;
        }
        else if (sc.BrushOpacityDecrease.Matches(key, mods))
        {
            _opacitySlider.Value = Math.Max(_opacitySlider.Minimum, _opacitySlider.Value - sc.BrushOpacityStep);
            e.Handled = true;
        }
        else if (sc.BrushOpacityIncrease.Matches(key, mods))
        {
            _opacitySlider.Value = Math.Min(_opacitySlider.Maximum, _opacitySlider.Value + sc.BrushOpacityStep);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var mods = Floss.App.Input.KeyBinding.ModifiersAfterKeyUp(e.Key, e.KeyModifiers);
        if (_activeGesture != GestureMode.None &&
            (_gestureKey == Key.None
                ? (mods & _gestureModifiers) != _gestureModifiers
                : e.Key == _gestureKey))
        {
            _activeGesture = GestureMode.None;
            _gestureKey = Key.None;
            _gestureModifiers = KeyModifiers.None;
            _isPanning = false;
            _canvas.UnlockCursorPreview();
            _canvas.PaintInputSuspended = false;
            Cursor = Cursor.Default;
            e.Handled = true;
        }

        if ((e.Key == Key.LeftAlt || e.Key == Key.RightAlt) && _preAltTool != null)
        {
            ActivateTool(_preAltTool, _preAltToolButton ?? _brushToolButton);
            _preAltTool = null;
            _preAltToolButton = null;
            e.Handled = true;
        }
    }

    private static (GestureMode Mode, Floss.App.Input.KeyBinding? Binding) DetectGesture(Key key, KeyModifiers mods, ShortcutsConfig sc)
    {
        if (sc.GesturePan.Matches(key, mods)) return (GestureMode.Pan, sc.GesturePan);
        if (sc.GestureZoom.Matches(key, mods)) return (GestureMode.Zoom, sc.GestureZoom);
        if (sc.GestureRotate.Matches(key, mods)) return (GestureMode.Rotate, sc.GestureRotate);
        if (sc.GestureBrushSize.Matches(key, mods)) return (GestureMode.BrushSize, sc.GestureBrushSize);
        return (GestureMode.None, null);
    }

    private void BeginGesture(GestureMode gesture, Key key, Floss.App.Input.KeyBinding? binding)
    {
        _activeGesture = gesture;
        _gestureKey = binding?.IsModifierOnly == true ? Key.None : key;
        _gestureModifiers = binding?.Modifiers ?? KeyModifiers.None;
        _canvas.PaintInputSuspended = true;
        Cursor = gesture switch
        {
            GestureMode.Pan => new Cursor(StandardCursorType.SizeAll),
            GestureMode.BrushSize => new Cursor(StandardCursorType.None),
            _ => new Cursor(StandardCursorType.Arrow)
        };
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }
}
