using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Processes;
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
        else
            _checkerboardOverlay?.InvalidateVisual();
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
            case GestureMode.None:
                _canvasPan.X += d.X;
                _canvasPan.Y += d.Y;
                _canvas.PanOffsetX = _canvasPan.X;
                _canvas.PanOffsetY = _canvasPan.Y;
                _rulerOverlay?.InvalidateVisual();
                _checkerboardOverlay?.InvalidateVisual();
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

        _canvas.PanOffsetX = _canvasPan.X;
        _canvas.PanOffsetY = _canvasPan.Y;
        _rulerOverlay?.InvalidateVisual();
        _checkerboardOverlay?.InvalidateVisual();
        ClampCanvasPan();
        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        UpdateStatus();
    }

    private void SetRotation(double degrees)
    {
        _rotation = degrees % 360;
        _canvasRotate.Angle = _rotation;
        _canvas.CanvasRotation = _rotation;
        _checkerboardOverlay?.InvalidateVisual();
        ClampCanvasPan();
        _rotDisplay.Text = $"{Math.Round(_rotation)}°";
        UpdateStatus();
    }

    private void ResetView()
    {
        _rotation = 0;
        _canvasRotate.Angle = 0;
        _canvas.CanvasRotation = 0;
        _canvasFlip.ScaleX = 1;
        _canvasFlip.ScaleY = 1;
        _canvas.FlipX = 1;
        _canvas.FlipY = 1;

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
        _canvas.PanOffsetX = 0;
        _canvas.PanOffsetY = 0;
        _rulerOverlay?.InvalidateVisual();
        ClampCanvasPan();

        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        _rotDisplay.Text = "0°";
        UpdateStatus();
    }

    private void ToggleCanvasOnly()
    {
        _canvasOnly = !_canvasOnly;

        // Krita-style: hide only the left/right dockers, keep
        // menu bar, top toolbar, status bar and footer visible.
        if (_leftRail != null) _leftRail.IsVisible = !_canvasOnly;
        if (_rightPanel != null) _rightPanel.IsVisible = !_canvasOnly;
        if (_splitterControl != null) _splitterControl.IsVisible = !_canvasOnly;

        // Collapse / restore layout grid columns so the canvas expands into
        // the space freed by the hidden side panels.
        if (_rootGrid != null && _rootColumnWidths != null)
        {
            for (var i = 0; i < _rootGrid.ColumnDefinitions.Count; i++)
            {
                var col = _rootGrid.ColumnDefinitions[i];
                if (_canvasOnly)
                {
                    col.Width = i == 1
                        ? new GridLength(1, GridUnitType.Star)
                        : new GridLength(0);
                    col.MinWidth = 0;
                    col.MaxWidth = double.PositiveInfinity;
                }
                else
                {
                    col.Width = _rootColumnWidths[i];
                    col.MinWidth = i == 1 ? 320 : (i == 3 ? 200 : 0);
                    col.MaxWidth = i == 3 ? 700 : double.PositiveInfinity;
                }
            }
        }

        if (_canvasOnly)
        {
            _workspaceViewport.Focus();
        }
    }

    private void ToggleRulers()
    {
        _showRulers = !_showRulers;
        if (_rulerOverlay != null) _rulerOverlay.IsVisible = _showRulers;
        App.Config.ShowRulers = _showRulers;
        App.Config.Save();
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

        var maxX = Math.Max(100.0, vpW * 0.5 + rotatedW * 0.5 - 80);
        var maxY = Math.Max(100.0, vpH * 0.5 + rotatedH * 0.5 - 80);

        _canvasPan.X = Math.Clamp(_canvasPan.X, -maxX, maxX);
        _canvasPan.Y = Math.Clamp(_canvasPan.Y, -maxY, maxY);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {

        var focused = FocusManager.GetFocusedElement();
        if (focused is TextBox or ComboBox)
        {
            // Let the input control handle the key normally
            return;
        }
        var key = e.Key;
        var mods = Floss.App.Input.KeyBinding.ModifiersWithKeyDown(key, e.KeyModifiers);
        var sc = App.Shortcuts;

        // Gestures must be checked before other shortcuts since they
        // consume modifier-only key-downs that shouldn't trigger tool shortcuts.
        var (gesture, gestureBinding) = DetectGesture(key, mods, sc);
        if (gesture != GestureMode.None)
        {
            BeginGesture(gesture, key, gestureBinding);
            e.Handled = true;
            return;
        }

        // Tool group shortcut recording
        if (_recordingToolGroup != null)
        {
            if (e.Key == Key.Escape) { CancelToolGroupShortcutRecording(); e.Handled = true; return; }
            if (e.Key is Key.Back or Key.Delete) { CommitToolGroupShortcut(Input.KeyBinding.Empty); e.Handled = true; return; }
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            CommitToolGroupShortcut(new Input.KeyBinding(e.Key, mods));
            e.Handled = true;
            return;
        }

        // Preset alternate invocation recording
        if (_recordingPresetAltInvocation != null)
        {
            if (e.Key == Key.Escape) { CancelPresetAltInvocationRecording(); e.Handled = true; return; }
            if (e.Key is Key.Back or Key.Delete) { CommitPresetAltInvocation(Input.KeyBinding.Empty); e.Handled = true; return; }
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                _recordingPresetPendingMods = mods;
                return;
            }
            _recordingPresetPendingMods = KeyModifiers.None;
            CommitPresetAltInvocation(new Input.KeyBinding(e.Key, mods));
            e.Handled = true;
            return;
        }

        if (sc.Undo.Matches(key, mods)) { _canvas.Undo(); e.Handled = true; }
        else if (sc.Redo.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.RedoAlt.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.ToggleCanvasOnly.Matches(key, mods)) { ToggleCanvasOnly(); e.Handled = true; }
        else if (sc.ToggleRulers.Matches(key, mods)) { ToggleRulers(); e.Handled = true; }
        else if (sc.FileNew.Matches(key, mods)) { _ = NewDocumentAsync(); e.Handled = true; }
        else if (sc.FileSave.Matches(key, mods)) { _ = SaveDocumentAsync(); e.Handled = true; }
        else if (sc.FileSaveAs.Matches(key, mods)) { _ = SaveDocumentAsAsync(); e.Handled = true; }
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
        else if (sc.RotateCanvas90Cw.Matches(key, mods))
        { _canvas.RotateCanvas90Clockwise(); SyncCanvasFrameToDocument(false); ClampCanvasPan(); _rulerOverlay?.InvalidateVisual(); e.Handled = true; }
        else if (sc.RotateCanvas90Ccw.Matches(key, mods))
        { _canvas.RotateCanvas90CounterClockwise(); SyncCanvasFrameToDocument(false); ClampCanvasPan(); _rulerOverlay?.InvalidateVisual(); e.Handled = true; }
        else if (sc.RotateCanvas180.Matches(key, mods)) { _canvas.RotateCanvas180(); e.Handled = true; }
        else if (sc.SelectAll.Matches(key, mods)) { _canvas.SelectAll(); e.Handled = true; }
        else if (sc.Deselect.Matches(key, mods)) { _canvas.Deselect(); e.Handled = true; }
        else if (sc.InvertSelect.Matches(key, mods)) { _canvas.InvertSelection(); e.Handled = true; }
        else if (sc.Transform.Matches(key, mods))
        {
            if (_canvas.IsTransformActive) { _canvas.CommitActiveTool(); }
            else
            {
                _canvas.BeginSelectionTransform(
                    _selectedLayerIndices.Count > 1 ? _selectedLayerIndices.ToList() : null);
            }
            e.Handled = true;
        }
        else if (key == Key.Back && mods == KeyModifiers.None)
        { _canvas.ClearSelectionContent(); e.Handled = true; }
        else if (EffectiveAltInvocation().Matches(key, mods))
        {
            if (_canvas.ActiveTool != _eyedropperTool)
                _canvas.SetAlternateTool(_eyedropperTool);
            e.Handled = true;
        }
        else if (key == Key.Escape)
        { _canvas.CancelActiveTool(); e.Handled = true; }
        else if ((key == Key.Return || key == Key.Enter) && _canvas.ActiveTool is SelectTool or PolylineTool or TransformTool or CompositeTool { CanCommitFromClick: true })
        { _canvas.CommitActiveTool(); e.Handled = true; }
        else if (sc.ColorCycle.Matches(key, mods)) { CycleColor(); e.Handled = true; }
        else if (sc.ColorDefault.Matches(key, mods)) { SetColor(Color.Parse("#111111")); e.Handled = true; }
        else if (sc.Copy.Matches(key, mods)) { _canvas.CopyToClipboard(); e.Handled = true; }
        else if (sc.Paste.Matches(key, mods)) { _canvas.PasteFromClipboard(); e.Handled = true; }
        else if (sc.FlipHorizontal.Matches(key, mods)) { _canvas.FlipCanvas(horizontal: true); e.Handled = true; }
        else if (sc.FlipVertical.Matches(key, mods)) { _canvas.FlipCanvas(horizontal: false); e.Handled = true; }
        else if (sc.MirrorHorizontal.Matches(key, mods)) { _canvasFlip.ScaleX = -_canvasFlip.ScaleX; _canvas.FlipX = (int)_canvasFlip.ScaleX; _rulerOverlay?.InvalidateVisual(); _checkerboardOverlay?.InvalidateVisual(); ClampCanvasPan(); UpdateStatus(); e.Handled = true; }
        else if (sc.MirrorVertical.Matches(key, mods)) { _canvasFlip.ScaleY = -_canvasFlip.ScaleY; _canvas.FlipY = (int)_canvasFlip.ScaleY; _rulerOverlay?.InvalidateVisual(); _checkerboardOverlay?.InvalidateVisual(); ClampCanvasPan(); UpdateStatus(); e.Handled = true; }
        else if (sc.DeleteSelection.Matches(key, mods)) { _canvas.ClearSelectionContent(); e.Handled = true; }
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

        if (!e.Handled)
        {
            // Collect all groups that share this shortcut
            var matching = _toolGroupButtons
                .Where(p => !p.Group.Shortcut.IsEmpty && p.Group.Shortcut.Matches(key, mods))
                .ToList();

            if (matching.Count > 0)
            {
                var activeIdx = matching.FindIndex(p => p.Group == _activeToolGroup);
                // Single group already active → do nothing; otherwise advance to next group
                if (!(matching.Count == 1 && activeIdx >= 0))
                {
                    var next = matching[(activeIdx + 1) % matching.Count];
                    var preset = next.Group.ActivePreset ?? next.Group.Presets.FirstOrDefault();
                    if (preset != null) ActivatePreset(next.Group, preset);
                }
                e.Handled = true;
            }
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

            // Restore alternate tool if it was active before the gesture
            if (_altToolBeforeGesture != null)
            {
                _canvas.SetAlternateTool(_altToolBeforeGesture);
                _altToolBeforeGesture = null;
            }
        }

        // Commit pending modifier-only key for preset alt invocation recording
        if (_recordingPresetAltInvocation != null && _recordingPresetPendingMods != KeyModifiers.None)
        {
            var pending = _recordingPresetPendingMods;
            _recordingPresetPendingMods = KeyModifiers.None;
            if (pending != KeyModifiers.None)
            {
                CommitPresetAltInvocation(new Input.KeyBinding(Key.None, pending));
                e.Handled = true;
                return;
            }
        }

        var altInvocation = EffectiveAltInvocation();
        if (altInvocation.Key != Key.None && e.Key == altInvocation.Key)
        {
            _canvas.SetAlternateTool(null);
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
        // Clear alternate tool during gestures so brush-size/zoom don't color-pick
        _altToolBeforeGesture = _canvas.AlternateTool;
        _canvas.SetAlternateTool(null);
        Cursor = gesture switch
        {
            GestureMode.Pan => CursorPan,
            GestureMode.BrushSize => CursorNone,
            _ => CursorArrow
        };
    }

    private Floss.App.Input.KeyBinding EffectiveAltInvocation()
    {
        var preset = _activeToolGroup?.ActivePreset?.AlternateInvocation;
        if (preset != null && !preset.IsEmpty) return preset;
        return App.Shortcuts.AlternateInvocation;
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }
}
