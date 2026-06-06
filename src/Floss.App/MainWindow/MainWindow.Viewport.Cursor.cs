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

        _canvas.PreferViewportToolCursor = true;

        _viewportCursorOverlay = new ViewportCursorOverlay
        {
            Canvas = _canvas,
            ZIndex = 10_000,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        // CursorPreviewChanged is wired per-tab in WireCanvas / UnwireCanvas.

        // Last child so the cursor paints above canvas, rulers, and chrome.
        _workspaceViewport.Children.Add(_viewportCursorOverlay);

        _workspaceViewport.AddHandler(PointerEnteredEvent, OnWorkspacePointerEntered, RoutingStrategies.Tunnel);
        SyncViewportOsCursor();
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

    /// <summary>
    /// Single owner of OS cursor state for the canvas subtree.
    /// Painted brush preview: hide OS cursor, overlay draws the ring.
    /// Transform and other modes: show native cursors on canvas and viewport alike.
    /// </summary>
    private void SyncViewportOsCursor()
    {
        if (_workspaceViewport == null || _canvas == null)
            return;

        ApplyViewportOsCursor(_canvas.ShouldShowToolCursor
            ? CursorNone
            : ResolveViewportOsCursor());
    }

    private Cursor? ResolveViewportOsCursor()
    {
        var kind = _canvas!.ViewportOsCursorKind;
        return kind.HasValue ? new Cursor(kind.Value) : null;
    }

    private void ApplyViewportOsCursor(Cursor? cursor)
    {
        _workspaceViewport!.Cursor = cursor;
        _canvas!.Cursor = cursor;
        if (_canvasFrame != null)
            _canvasFrame.Cursor = cursor;
        if (_canvasHost != null)
            _canvasHost.Cursor = cursor;
    }

    private void ApplyViewportOsCursorHidden() => ApplyViewportOsCursor(CursorNone);

    internal void RefreshViewportCursorAfterInput()
    {
        _viewportCursorOverlay?.NotifyCursorChanged();
        _canvas?.InvalidateVisual();
        SyncViewportOsCursor();
    }
}
