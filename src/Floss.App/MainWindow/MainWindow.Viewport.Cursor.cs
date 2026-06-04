using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Floss.App.Canvas;

namespace Floss.App;

public partial class MainWindow
{
    private ViewportCursorOverlay? _viewportCursorOverlay;

    private void WireViewportCursor()
    {
        if (_workspaceViewport == null || _canvas == null)
            return;

        _viewportCursorOverlay = new ViewportCursorOverlay
        {
            Canvas = _canvas,
            ZIndex = 10_000,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _canvas.CursorPreviewChanged += OnCanvasCursorPreviewChanged;

        // Last child so the cursor paints above canvas, rulers, and chrome.
        _workspaceViewport.Children.Add(_viewportCursorOverlay);

        _workspaceViewport.AddHandler(PointerEnteredEvent, OnWorkspacePointerEntered, RoutingStrategies.Tunnel);
    }

    private void OnCanvasCursorPreviewChanged()
    {
        SyncViewportOsCursor();
        if (_viewportCursorOverlay == null)
            return;

        _viewportCursorOverlay.InvalidateVisual();
    }

    private void OnWorkspacePointerEntered(object? sender, PointerEventArgs e)
        => UpdateViewportPointerFromEvent(e);

    private void UpdateViewportPointerFromEvent(PointerEventArgs e)
    {
        if (_canvas == null)
            return;

        _canvas.TrackViewportPointer(
            e.GetCurrentPoint(_workspaceViewport!),
            e.GetCurrentPoint(_canvas));
        SyncViewportOsCursor();
    }

    /// <summary>Hide the OS cursor over the viewport while the painted preview is shown.</summary>
    private void SyncViewportOsCursor()
    {
        if (_workspaceViewport == null || _canvas == null)
            return;

        var hide = _canvas.ShouldShowToolCursor;

        if (hide)
            ApplyViewportOsCursorHidden();
        else
            ClearViewportOsCursorHidden();
    }

    private void ApplyViewportOsCursorHidden()
    {
        _workspaceViewport!.Cursor = CursorNone;
        _canvas.Cursor = CursorNone;
        if (_canvasFrame != null)
            _canvasFrame.Cursor = CursorNone;
        if (_canvasHost != null)
            _canvasHost.Cursor = CursorNone;
        Cursor = CursorNone;
    }

    private void ClearViewportOsCursorHidden()
    {
        _workspaceViewport!.Cursor = null;
        _canvas.Cursor = CursorNone;
        if (_canvasFrame != null)
            _canvasFrame.Cursor = null;
        if (_canvasHost != null)
            _canvasHost.Cursor = null;
        Cursor = null;
    }

    internal void RefreshViewportCursorAfterInput()
    {
        _viewportCursorOverlay?.NotifyCursorChanged();
        _canvas?.InvalidateVisual();
        SyncViewportOsCursor();
    }
}
