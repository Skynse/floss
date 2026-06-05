using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Processes;
using Floss.App.Timelapse;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow
{
    // ── Tab model ─────────────────────────────────────────────────────────────

    private sealed class DocumentTab
    {
        public DrawingCanvas Canvas { get; }
        public string? FilePath { get; set; }
        public bool HasDocument { get; set; }
        public string DocumentName { get; set; } = "Untitled";
        public TimelapseSession? Timelapse { get; set; }

        // Viewport state persisted while this tab is inactive
        public double Zoom = 1.0;
        public double Rotation;
        public double PanX, PanY;
        public double FlipX = 1, FlipY = 1;

        public readonly HashSet<int> SelectedLayerIndices = new();

        public string DisplayTitle => string.IsNullOrEmpty(FilePath)
            ? DocumentName
            : Path.GetFileName(FilePath);

        public DocumentTab(DrawingCanvas canvas) => Canvas = canvas;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly List<DocumentTab> _tabs = new();
    private DocumentTab? _activeTab;
    private bool _pendingLayerRebuild;
    private bool _layerRebuildCoalesce;
    private StackPanel _tabBar = null!;
    private ScrollViewer _tabBarContainer = null!;
    private Action? _canvasUnwire;

    // ── Tab bar UI ────────────────────────────────────────────────────────────

    private ScrollViewer BuildTabBar()
    {
        _tabBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 3, 0, 0)
        };

        var newTabBtn = new Button
        {
            Content = "+",
            Width = 24,
            Height = 23,
            Padding = new Thickness(0),
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            Margin = new Thickness(2, 2, 4, 1)
        };
        ToolTip.SetTip(newTabBtn, "New document  (Ctrl+N)");
        newTabBtn.Click += (_, _) => _ = NewDocumentAsync();

        var bar = new DockPanel
        {
            LastChildFill = true,
            Background = new SolidColorBrush(Color.Parse(Bg0))
        };
        DockPanel.SetDock(newTabBtn, Dock.Right);
        bar.Children.Add(newTabBtn);
        bar.Children.Add(_tabBar);

        _tabBarContainer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = bar,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            IsVisible = false,
        };
        return _tabBarContainer;
    }

    private void UpdateTabBar()
    {
        _tabBar.Children.Clear();
        foreach (var tab in _tabs)
            _tabBar.Children.Add(BuildTabButton(tab));

        var hasTabs = _tabs.Count > 0;
        _tabBarContainer.IsVisible = hasTabs;
        if (_checkerboardOverlay != null)
            _checkerboardOverlay.IsVisible = _activeTab?.HasDocument == true;
    }

    private Control BuildTabButton(DocumentTab tab)
    {
        var isActive = tab == _activeTab;

        var title = new TextBlock
        {
            Text = tab.DisplayTitle + (tab.Canvas.IsDirty ? " ●" : ""),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse(isActive ? TextPrimary : TextSecondary)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var close = new Button
        {
            Content = "×",
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            Margin = new Thickness(4, 0, 0, 0)
        };
        close.Click += (_, e) => { _ = CloseTabAsync(tab); e.Handled = true; };

        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0)
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(close, 1);
        panel.Children.Add(title);
        panel.Children.Add(close);

        var border = new Border
        {
            Child = panel,
            MinWidth = 96,
            MaxWidth = 220,
            Height = 23,
            Background = new SolidColorBrush(Color.Parse(isActive ? Bg2 : Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(isActive ? Accent : Stroke)),
            BorderThickness = new Thickness(1, 1, 1, isActive ? 0 : 1),
            CornerRadius = new CornerRadius(2, 2, 0, 0),
            Margin = new Thickness(0, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Arrow)
        };

        border.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(border).Properties.IsMiddleButtonPressed)
                _ = CloseTabAsync(tab);
            else
                SwitchToTab(tab);
        };

        return border;
    }

    // ── Tab operations ────────────────────────────────────────────────────────

    private DocumentTab CreateTab()
    {
        var canvas = new DrawingCanvas();
        var tab = new DocumentTab(canvas);
        _tabs.Add(tab);
        return tab;
    }

    private void SwitchToTab(DocumentTab tab)
    {
        if (_activeTab == tab) return;

        // Cancel any in-progress tool operation before leaving
        _canvas?.CancelActiveTool();
        ResetTransientInputState();
        CancelPendingResizeOverlay();

        var currentColor = _canvas?.PaintColor;

        // Always unwire the current canvas before swapping (safe even on first call)
        UnwireCanvas();

        // Save current viewport state into the departing tab
        if (_activeTab != null)
        {
            _activeTab.Canvas.ClearViewportPointer();
            _activeTab.Canvas.RecoverInputState();
            _activeTab.Zoom = _zoom;
            _activeTab.Rotation = _rotation;
            _activeTab.PanX = _canvasPan.X;
            _activeTab.PanY = _canvasPan.Y;
            _activeTab.FlipX = _canvasFlip.ScaleX;
            _activeTab.FlipY = _canvasFlip.ScaleY;
            _activeTab.SelectedLayerIndices.Clear();
            foreach (var i in _selectedLayerIndices)
                _activeTab.SelectedLayerIndices.Add(i);
        }

        _activeTab = tab;
        _canvas = tab.Canvas;
        _canvas.PreferViewportToolCursor = true;
        _currentFilePath = tab.FilePath;
        _canvas.RecoverInputState();

        // Swap canvas into the frame
        if (_canvasHost != null)
        {
            for (int i = _canvasHost.Children.Count - 1; i >= 0; i--)
            {
                if (_canvasHost.Children[i] is DrawingCanvas)
                    _canvasHost.Children.RemoveAt(i);
            }
            _canvasHost.Children.Insert(0, _canvas);
        }
        else
        {
            _canvasFrame!.Child = _canvas;
        }

        // Update overlays
        if (_checkerboardOverlay != null) _checkerboardOverlay.Canvas = _canvas;
        if (_rulerOverlay is RulerOverlay ro) ro.Canvas = _canvas;
        if (_resizeOverlay != null) _resizeOverlay.Canvas = _canvas;
        if (_selectionOutlineOverlay != null) _selectionOutlineOverlay.Canvas = _canvas;
        if (_viewportCursorOverlay != null)
            _viewportCursorOverlay.Canvas = _canvas;

        // Restore viewport state
        _zoom = tab.Zoom;
        _rotation = tab.Rotation;
        _canvasScale.ScaleX = _zoom;
        _canvasScale.ScaleY = _zoom;
        _canvasRotate.Angle = _rotation;
        _canvasFlip.ScaleX = tab.FlipX;
        _canvasFlip.ScaleY = tab.FlipY;
        _canvasPan.X = tab.PanX;
        _canvasPan.Y = tab.PanY;
        SyncViewportStateToCanvas();

        // Restore selected layers
        _selectedLayerIndices.Clear();
        foreach (var i in tab.SelectedLayerIndices)
            _selectedLayerIndices.Add(i);
        if (_selectedLayerIndices.Count == 0 && _canvas.Layers.Count > 0)
            _selectedLayerIndices.Add(_canvas.ActiveLayerIndex);

        WireCanvas();

        // Sync paint color so the new canvas uses the same color as the previous one
        if (currentColor.HasValue)
            _canvas.SetPaintColor(currentColor.Value);

        // Recreate tool factory so new tools bind to this canvas's document/brush engine
        _toolFactory = new ToolFactory(_canvas.Document, _canvas.BrushEngine);
        InvalidatePresetToolCache();
        var activeToolPreset = _activeToolGroup?.ActivePreset;
        if (activeToolPreset != null)
        {
            _canvas.SetActiveTool(_toolFactory.CreateTool(activeToolPreset), activeToolPreset);

            // Push the current user-facing brush onto the new canvas
            if (_activePreset != null)
                _canvas.SyncBrushFromContext(_activePreset);
        }

        // Sync UI
        _canvasFrame.IsVisible = tab.HasDocument;
        SetDocumentPanelsVisible(tab.HasDocument);
        if (_zoomDisplay != null) _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        if (_rotDisplay != null) _rotDisplay.Text = $"{Math.Round(_rotation)}°";
        SyncCanvasFrameToDocument(fitToViewport: false);
        BuildLayerList();
        RefreshColorSliders();
        UpdateStatus();
        UpdateTitle();
        UpdateTabBar();
        UpdateTimelapseMenuState();
        _rulerOverlay?.InvalidateVisual();
        _checkerboardOverlay?.InvalidateVisual();
        _resizeOverlay?.InvalidateVisual();
        SyncCanvasViewport();
        SyncBrushSizeLimits();
        ResetTransientInputState();
        RefreshViewportCursorAfterInput();
        ActivateCanvasKeyboardRegion();
        _workspaceViewport?.Focus();
    }

    private async System.Threading.Tasks.Task<DocumentTab?> NewTabAsync()
    {
        var tab = CreateTab();
        SwitchToTab(tab);
        return tab;
    }

    private async System.Threading.Tasks.Task CloseTabAsync(DocumentTab tab)
    {
        if (tab.Canvas.IsDirty)
        {
            SwitchToTab(tab);
            var save = await new UnsavedChangesDialog(tab.DisplayTitle).ShowDialog<bool?>(this);
            if (save == null) return;
            if (save == true)
            {
                await SaveDocumentAsync();
                if (tab.Canvas.IsDirty) return;
            }
        }

        var idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);

        if (_tabs.Count == 0)
        {
            UnwireCanvas();
            _activeTab = null;
            _canvasFrame.IsVisible = false;
            SetDocumentPanelsVisible(false);
            UpdateTabBar();
            UpdateTitle();
            UpdateTimelapseMenuState();
            if (tab.Timelapse != null)
            {
                tab.Timelapse.SetRecording(false);
                await tab.Timelapse.WaitForIdleAsync();
                tab.Timelapse.Dispose();
                tab.Timelapse = null;
            }
            tab.Canvas.Dispose();
            BuildLayerList();
            ScheduleDocumentGc();
            return;
        }

        if (_activeTab == tab)
        {
            var next = _tabs[Math.Clamp(idx, 0, _tabs.Count - 1)];
            SwitchToTab(next);
        }
        else
        {
            UpdateTabBar();
        }

        if (tab.Timelapse != null)
        {
            tab.Timelapse.SetRecording(false);
            await tab.Timelapse.WaitForIdleAsync();
            tab.Timelapse.Dispose();
            tab.Timelapse = null;
        }
        tab.Canvas.Dispose();
        ScheduleDocumentGc();
    }

    private async System.Threading.Tasks.Task<bool> ResolveUnsavedDocumentsBeforeCloseAsync()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (!tab.HasDocument || !tab.Canvas.IsDirty)
                continue;

            SwitchToTab(tab);
            var save = await new UnsavedChangesDialog(tab.DisplayTitle).ShowDialog<bool?>(this);
            if (save == null)
                return false;

            if (save == true)
            {
                await SaveDocumentAsync();
                if (tab.Canvas.IsDirty)
                    return false;
            }
        }

        return true;
    }

    private static void ScheduleDocumentGc()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Compact the LOH so freed tile/history byte[] arrays actually return to the OS.
                // Without compaction the committed pages stay reserved even after collection.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch (Exception ex) { CrashLog.Write(ex, "MainWindow.ScheduleDocumentGc"); }
        });
    }

    // ── Canvas event wiring ───────────────────────────────────────────────────
    // Extracted so events can be rewired on tab switch.

    private void WireCanvas()
    {
        _canvas.StatsChanged += OnCanvasStatsChanged;
        _canvas.HistoryChanged += OnCanvasHistoryChanged;
        _canvas.SelectionChanged += OnCanvasSelectionChanged;
        _canvas.LayersChanged += OnCanvasLayersChanged;
        _canvas.LayerMetadataChanged += OnCanvasLayerMetadataChanged;
        _canvas.LayersFoundByRect += ExpandAndScrollToLayers;
        _canvas.ColorSampled += OnCanvasColorSampled;
        _canvas.DirtyStateChanged += OnCanvasDirtyStateChanged;
        _canvas.CursorPreviewChanged += OnCanvasCursorPreviewChanged;
        _canvas.SmartShapeUiChanged += UpdateSmartShapeLauncher;

        _canvasUnwire = UnwireCanvas;
    }

    private void UnwireCanvas()
    {
        _canvas.StatsChanged -= OnCanvasStatsChanged;
        _canvas.HistoryChanged -= OnCanvasHistoryChanged;
        _canvas.SelectionChanged -= OnCanvasSelectionChanged;
        _canvas.LayersChanged -= OnCanvasLayersChanged;
        _canvas.LayerMetadataChanged -= OnCanvasLayerMetadataChanged;
        _canvas.LayersFoundByRect -= ExpandAndScrollToLayers;
        _canvas.ColorSampled -= OnCanvasColorSampled;
        _canvas.DirtyStateChanged -= OnCanvasDirtyStateChanged;
        _canvas.CursorPreviewChanged -= OnCanvasCursorPreviewChanged;
        _canvas.SmartShapeUiChanged -= UpdateSmartShapeLauncher;
    }

    private void OnCanvasStatsChanged(object? s, EventArgs e)
    {
        // Dispatch at Background priority — `NotifyChanged` fires during
        // brush stroke preview flushes and we must not add ~30 UI property
        // writes to the render-path hot loop.
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatus, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnCanvasHistoryChanged(object? s, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus();
            CaptureTimelapseFrameAfterHistory();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
    private void OnCanvasSelectionChanged(object? s, EventArgs e) => UpdateSelectionActionBar();

    private void OnCanvasLayersChanged(object? s, EventArgs e)
    {
        PruneLayerSelection();
        SyncCanvasFrameToDocument(fitToViewport: false);
        _rulerOverlay?.InvalidateVisual();
        // Coalesce to one rebuild per frame, but never drop a later LayersChanged
        // (e.g. expand/collapse while a rebuild is already queued).
        ScheduleLayerListRebuild();
        UpdateStatus();
        UpdateSelectionActionBar();
    }

    private void ScheduleLayerListRebuild()
    {
        if (_pendingLayerRebuild)
        {
            _layerRebuildCoalesce = true;
            return;
        }

        _pendingLayerRebuild = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            do
            {
                _layerRebuildCoalesce = false;
                _pendingLayerRebuild = false;
                BuildLayerList();
            } while (_layerRebuildCoalesce);
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void OnCanvasLayerMetadataChanged(object? s, LayerMetadataChangedEventArgs e)
    {
        UpdateLayerRow(e.LayerIndex);
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatus, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnCanvasColorSampled(object? s, Avalonia.Media.Color c) => SetColor(c);

    private void OnCanvasDirtyStateChanged(object? s, EventArgs e)
    {
        UpdateTitle();
        UpdateTabBar();
    }

    private void UpdateTitle()
    {
        if (_activeTab == null) { Title = "Floss Studio"; return; }
        var name = _activeTab.DisplayTitle;
        var star = _canvas.IsDirty ? "*" : "";
        Title = $"Floss Studio — {name}{star}";
    }
}
