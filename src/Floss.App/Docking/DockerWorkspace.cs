using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Floss.App.Docking;

/// <summary>
/// Manages the dockable panel layout — left rail, center workspace, right panel,
/// floating windows, drag-and-drop reordering, visibility toggling, canvas-only mode,
/// and workspace preset save/load.
/// </summary>
public sealed class DockerWorkspace
{
    private const double SplitterWidth = 3.0;
    private const int SnapDistance = 18;
    private const int FloatingGap = 6;

    private readonly Window _owner;
    private readonly Action _onPersistLayout;

    private Grid _rootGrid = null!;
    private Grid _centerArea = null!;
    private Border _leftRail = null!;
    private GridSplitter _leftSplitter = null!;
    private Border _rightPanelShell = null!;
    private GridSplitter _rightSplitter = null!;
    private Grid _dockerHostGrid = null!;

    // Panel state
    private readonly Dictionary<string, bool> _panelVisible = new();
    private readonly Dictionary<string, Border> _panelSections = new();
    private readonly Dictionary<string, IDockPanel> _panelDefinitions = new();
    private readonly Dictionary<string, Control> _panelContentCache = new();

    // Floating windows
    private readonly Dictionary<string, Window> _floatingWindows = new();
    private bool _suppressFloatingClosed;
    private bool _suppressFloatingSnap;

    // Drag-and-drop state
    private string? _dragPanelId;
    private Point _dragStart;
    private bool _isDragging;
    private Border? _dropIndicator;
    private int _dropColumn;
    private int _dropIndex;

    // Canvas-only mode
    private bool _canvasOnly;
    private GridLength[]? _savedColumnWidths;

    // Workspace presets
    private readonly Dictionary<string, WorkspaceLayout> _presets = new();

    public WorkspaceLayout Layout { get; private set; } = WorkspaceLayout.CreateDefault();
    public Control RootElement => _rootGrid;
    public IReadOnlyDictionary<string, WorkspaceLayout> Presets => _presets;

    // Events
    public event Action? CanvasOnlyChanged;
    public event Action<string>? PanelToggled;
    public event Action<string>? PanelMoved;

    // Panel content is built via a delegate to allow MainWindow to wire itself
    public Func<string, Control>? BuildPanelContent { get; set; }

    public DockerWorkspace(Window owner, Action onPersistLayout)
    {
        _owner = owner;
        _onPersistLayout = onPersistLayout;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Initialization
    // ═══════════════════════════════════════════════════════════════════════════

    public void Initialize(IReadOnlyList<IDockPanel> panels, WorkspaceLayout layout,
        Grid centerArea)
    {
        _centerArea = centerArea;

        foreach (var panel in panels)
            _panelDefinitions[panel.Id] = panel;

        Layout = layout;
        Layout.Normalize(panels.Select(p => p.Id).ToList());

        BuildRootLayout();
        AddCenterArea(centerArea);
        BuildAllPanels();
        OpenFloatingWindows();
    }

    private void BuildRootLayout()
    {
        _rootGrid = new Grid();

        var leftWidth = Layout.LeftColumn.PanelIds.Count > 1
            ? Math.Max(120, Layout.LeftRailWidth)
            : 48;
        var rightWidth = Math.Clamp(Layout.RightPanelWidth, 300, 1000);

        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition(leftWidth, GridUnitType.Pixel) { MinWidth = 36 });
        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 });
        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
        _rootGrid.ColumnDefinitions.Add(new ColumnDefinition(rightWidth, GridUnitType.Pixel) { MinWidth = 300, MaxWidth = 1000 });

        _savedColumnWidths = _rootGrid.ColumnDefinitions.Select(c => c.Width).ToArray();

        _leftSplitter = new GridSplitter
        {
            Width = SplitterWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(ParseColor("#1e1e20"))
        };
        _leftSplitter.DragCompleted += (_, _) => PersistFromUi();

        _rightSplitter = new GridSplitter
        {
            Width = SplitterWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(ParseColor("#1e1e20"))
        };
        _rightSplitter.DragCompleted += (_, _) => PersistFromUi();

        // Build left rail (tools column)
        _leftRail = BuildLeftColumn();
        Grid.SetColumn(_leftRail, 0);
        Grid.SetColumn(_leftSplitter, 1);
        Grid.SetColumn(_rightSplitter, 3);

        _rootGrid.Children.Add(_leftRail);
        _rootGrid.Children.Add(_leftSplitter);
        _rootGrid.Children.Add(_rightSplitter);

        // Build right panel
        _rightPanelShell = BuildRightPanel();
        Grid.SetColumn(_rightPanelShell, 4);
        _rootGrid.Children.Add(_rightPanelShell);
    }

    private void AddCenterArea(Grid centerArea)
    {
        Grid.SetColumn(centerArea, 2);
        _rootGrid.Children.Add(centerArea);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Left Column
    // ═══════════════════════════════════════════════════════════════════════════

    private Border BuildLeftColumn()
    {
        var column = BuildDockColumn(Layout.LeftColumn, -1);
        return new Border
        {
            Background = new SolidColorBrush(ParseColor("#252528")),
            BorderBrush = new SolidColorBrush(ParseColor("#3a3a3d")),
            BorderThickness = new Thickness(0, 0, 1, 0),
            ClipToBounds = true,
            Child = column
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Right Panel
    // ═══════════════════════════════════════════════════════════════════════════

    private Border BuildRightPanel()
    {
        var columns = Layout.RightColumns;
        _dockerHostGrid = new Grid { ClipToBounds = true };

        if (columns.Count == 0)
            return new Border { Child = _dockerHostGrid, ClipToBounds = true };

        var split = Math.Clamp(Layout.RightDockSplit, 0.2, 0.8);

        for (var i = 0; i < columns.Count; i++)
        {
            var frac = i == 0 ? split : 1.0 - split;
            _dockerHostGrid.ColumnDefinitions.Add(
                new ColumnDefinition(frac, GridUnitType.Star));

            var col = BuildDockColumn(columns[i], i);
            Grid.SetColumn(col, i * 2);
            _dockerHostGrid.Children.Add(col);

            if (i < columns.Count - 1)
            {
                _dockerHostGrid.ColumnDefinitions.Add(
                    new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
                var sp = new GridSplitter
                {
                    Width = SplitterWidth,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(ParseColor("#3a3a3d"))
                };
                sp.DragCompleted += (_, _) => PersistFromUi();
                Grid.SetColumn(sp, i * 2 + 1);
                _dockerHostGrid.Children.Add(sp);
            }
        }

        return new Border
        {
            Background = new SolidColorBrush(ParseColor("#252528")),
            BorderBrush = new SolidColorBrush(ParseColor("#3a3a3d")),
            ClipToBounds = true,
            Child = _dockerHostGrid
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Dock Column
    // ═══════════════════════════════════════════════════════════════════════════

    private Grid BuildDockColumn(DockColumnLayout colLayout, int columnIndex)
    {
        var grid = new Grid { ClipToBounds = true };
        var visibleIds = colLayout.PanelIds
            .Where(id => IsPanelVisible(id))
            .ToList();

        if (visibleIds.Count == 0)
            return grid;

        for (var i = 0; i < visibleIds.Count; i++)
        {
            var id = visibleIds[i];

            var proportion = Layout.PanelProportions.TryGetValue(id, out var saved)
                ? Math.Max(0.05, saved)
                : _panelDefinitions.TryGetValue(id, out var def)
                    ? def.Proportion
                    : 0.2;

            var minH = _panelDefinitions.TryGetValue(id, out var def2)
                ? def2.MinHeight
                : 64;

            var row = new RowDefinition(new GridLength(proportion, GridUnitType.Star)) { MinHeight = minH };
            grid.RowDefinitions.Add(row);

            var section = GetOrCreatePanelSection(id, colLayout, columnIndex);
            if (section != null)
            {
                Grid.SetRow(section, grid.RowDefinitions.Count - 1);
                grid.Children.Add(section);
            }

            if (i == visibleIds.Count - 1) continue;

            grid.RowDefinitions.Add(new RowDefinition(new GridLength(SplitterWidth, GridUnitType.Pixel)));
            var sp = new GridSplitter
            {
                Height = SplitterWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                Background = new SolidColorBrush(ParseColor("#3a3a3d"))
            };
            sp.DragCompleted += (_, _) => PersistFromUi();
            Grid.SetRow(sp, grid.RowDefinitions.Count - 1);
            grid.Children.Add(sp);
        }

        return grid;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Panel Section (header + body)
    // ═══════════════════════════════════════════════════════════════════════════

    private Border? GetOrCreatePanelSection(string id, DockColumnLayout colLayout, int columnIndex)
    {
        if (_panelSections.TryGetValue(id, out var existing))
            return existing;

        if (!_panelDefinitions.TryGetValue(id, out var def))
            return null;

        var content = BuildPanelContent?.Invoke(id);
        if (content == null) return null;

        _panelContentCache[id] = content;

        var section = BuildPanelSection(def, content);
        _panelSections[id] = section;
        _panelVisible[id] = true;
        return section;
    }

    private Border BuildPanelSection(IDockPanel panel, Control content)
    {
        var titleText = new TextBlock
        {
            Text = panel.Title,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(ParseColor("#8a8a8e")),
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new Border
        {
            Padding = new Thickness(7, 3, 7, 2),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { titleText }
            },
            ContextMenu = BuildPanelContextMenu(panel.Id)
        };

        var id = panel.Id;
        header.PointerPressed += (_, e) => HeaderPointerPressed(id, header, e);
        header.PointerMoved += (_, e) => HeaderPointerMoved(id, header, e);
        header.PointerReleased += (_, e) => HeaderPointerReleased(id, header, e);
        header.PointerCaptureLost += (_, _) => CancelDrag();

        content.VerticalAlignment = VerticalAlignment.Stretch;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.ClipToBounds = true;

        var body = new Border { ClipToBounds = true, Child = content };

        var outer = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ClipToBounds = true
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        outer.Children.Add(header);
        outer.Children.Add(body);

        return new Border
        {
            BorderBrush = new SolidColorBrush(ParseColor("#3a3a3d")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            ClipToBounds = true,
            Child = outer
        };
    }

    private ContextMenu BuildPanelContextMenu(string id)
    {
        var placement = Layout.FindPanel(id);
        var isOnLeft = placement?.ColumnIndex == -1;
        var isFloating = Layout.IsFloating(id);

        var floatItem = new MenuItem { Header = isFloating ? "_Dock" : "_Detach" };
        floatItem.Click += (_, _) =>
        {
            if (Layout.IsFloating(id)) DockPanel(id);
            else FloatPanel(id);
        };

        var moveLeft = new MenuItem { Header = "Move to _Left Side", IsEnabled = !isFloating && !isOnLeft };
        moveLeft.Click += (_, _) => MovePanel(id, -1, int.MaxValue);

        var moveRight = new MenuItem { Header = "Move to _Right Side", IsEnabled = !isFloating && isOnLeft };
        moveRight.Click += (_, _) => MovePanel(id, 0, int.MaxValue);

        var moveUp = new MenuItem { Header = "Move _Up", IsEnabled = !isFloating };
        moveUp.Click += (_, _) => ShiftPanel(id, -1);

        var moveDown = new MenuItem { Header = "Move _Down", IsEnabled = !isFloating };
        moveDown.Click += (_, _) => ShiftPanel(id, 1);

        var reset = new MenuItem { Header = "_Reset Panel Size" };
        reset.Click += (_, _) =>
        {
            Layout.PanelProportions.Remove(id);
            Relayout();
        };

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                floatItem, new Separator(),
                moveLeft, moveRight, new Separator(),
                moveUp, moveDown, new Separator(),
                reset
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Drag-and-Drop Reordering
    // ═══════════════════════════════════════════════════════════════════════════

    private void HeaderPointerPressed(string id, Control header, PointerPressedEventArgs e)
    {
        if (Layout.IsFloating(id)) return;
        var point = e.GetCurrentPoint(header);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragPanelId = id;
        _dragStart = point.Position;
        _isDragging = false;
        _dropColumn = -1;
        _dropIndex = -1;
        e.Pointer.Capture(header);
        e.Handled = true;
    }

    private void HeaderPointerMoved(string id, Control header, PointerEventArgs e)
    {
        if (_dragPanelId != id) return;
        var local = e.GetPosition(header);
        var d = local - _dragStart;
        if (!_isDragging && d.X * d.X + d.Y * d.Y < 36)
            return;

        _isDragging = true;
        if (_dockerHostGrid == null) return;

        UpdateDropPreview(id, e.GetPosition(_dockerHostGrid));
        e.Handled = true;
    }

    private void HeaderPointerReleased(string id, Control header, PointerReleasedEventArgs e)
    {
        if (_dragPanelId != id) return;
        if (_isDragging && _dropColumn >= 0 && _dropIndex >= 0)
            ApplyDrop(id, _dropColumn, _dropIndex);
        CancelDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void CancelDrag()
    {
        _dragPanelId = null;
        _isDragging = false;
        _dropColumn = -1;
        _dropIndex = -1;
        if (_dropIndicator != null)
            _dropIndicator.IsVisible = false;
    }

    private void UpdateDropPreview(string movingId, Point hostPoint)
    {
        if (_dockerHostGrid == null) return;

        var target = ResolveDropTarget(movingId, hostPoint);
        if (target == null)
        {
            if (_dropIndicator != null) _dropIndicator.IsVisible = false;
            _dropColumn = -1;
            _dropIndex = -1;
            return;
        }

        var (colIdx, insertIdx, x, y, width) = target.Value;
        _dropColumn = colIdx;
        _dropIndex = insertIdx;

        _dropIndicator ??= new Border
        {
            Height = SplitterWidth,
            Background = new SolidColorBrush(ParseColor("#0078d4")),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            ZIndex = 1000
        };

        if (!_dockerHostGrid.Children.Contains(_dropIndicator))
            _dockerHostGrid.Children.Add(_dropIndicator);

        _dropIndicator.Width = Math.Max(16, width);
        _dropIndicator.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
        _dropIndicator.IsVisible = true;
    }

    private (int col, int idx, double x, double y, double w)? ResolveDropTarget(
        string movingId, Point hostPoint)
    {
        if (_dockerHostGrid == null || _dockerHostGrid.ColumnDefinitions.Count < 2)
            return null;
        if (hostPoint.X < 0 || hostPoint.Y < 0 ||
            hostPoint.X > _dockerHostGrid.Bounds.Width ||
            hostPoint.Y > _dockerHostGrid.Bounds.Height)
            return null;

        var colCount = (Layout.RightColumns?.Count) ?? 0;
        if (colCount == 0) return null;

        // Simple: determine which column the point is over
        var totalW = _dockerHostGrid.Bounds.Width;
        var colFrac = hostPoint.X / totalW;
        var colIdx = (int)(colFrac * colCount);
        colIdx = Math.Clamp(colIdx, 0, colCount - 1);

        var x = colCount > 1
            ? (double)colIdx / colCount * totalW
            : 0;
        var width = colCount > 1
            ? totalW / colCount
            : totalW;

        var ids = Layout.RightColumns![colIdx].PanelIds
            .Where(id => IsPanelVisible(id))
            .ToList();

        var y = 0.0;
        var insertIdx = 0;

        for (var i = 0; i < ids.Count; i++)
        {
            if (!_panelSections.TryGetValue(ids[i], out var section)) continue;
            var topLeft = section.TranslatePoint(new Point(0, 0), _dockerHostGrid) ?? new Point(x, 0);
            var midpoint = topLeft.Y + section.Bounds.Height * 0.5;
            if (hostPoint.Y < midpoint)
            {
                y = topLeft.Y;
                insertIdx = i;
                return (colIdx, insertIdx, x, y, width);
            }
            insertIdx = i + 1;
            y = topLeft.Y + section.Bounds.Height;
        }

        return (colIdx, insertIdx, x, Math.Max(0, y), width);
    }

    private void ApplyDrop(string id, int columnIndex, int insertIndex)
    {
        PersistFromUi();
        Layout.RemovePanel(id);

        if (columnIndex < 0)
        {
            insertIndex = Math.Clamp(insertIndex, 0, Layout.LeftColumn.PanelIds.Count);
            Layout.LeftColumn.PanelIds.Insert(insertIndex, id);
        }
        else
        {
            if ((uint)columnIndex >= (uint)Layout.RightColumns.Count) return;
            var target = Layout.RightColumns[columnIndex].PanelIds;
            insertIndex = Math.Clamp(insertIndex, 0, target.Count);
            target.Insert(insertIndex, id);
        }

        if (Layout.FloatingPanels.TryGetValue(id, out var fs))
            fs.IsFloating = false;

        Relayout();
        PanelMoved?.Invoke(id);
        _onPersistLayout();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Move / Shift
    // ═══════════════════════════════════════════════════════════════════════════

    private void ShiftPanel(string id, int delta)
    {
        PersistFromUi();
        var placement = Layout.FindPanel(id);
        if (placement == null) return;

        var (colIdx, panelIdx) = placement.Value;
        var panelIds = colIdx < 0
            ? Layout.LeftColumn.PanelIds
            : Layout.RightColumns[colIdx].PanelIds;

        var target = Math.Clamp(panelIdx + delta, 0, panelIds.Count - 1);
        if (target == panelIdx) return;

        panelIds.RemoveAt(panelIdx);
        panelIds.Insert(target, id);

        Relayout();
        PanelMoved?.Invoke(id);
        _onPersistLayout();
    }

    public void MovePanel(string id, int columnIndex, int insertIndex)
    {
        PersistFromUi();
        Layout.RemovePanel(id);

        if (columnIndex < 0)
        {
            insertIndex = Math.Clamp(insertIndex, 0, Layout.LeftColumn.PanelIds.Count);
            Layout.LeftColumn.PanelIds.Insert(insertIndex, id);
        }
        else if (columnIndex < Layout.RightColumns.Count)
        {
            var target = Layout.RightColumns[columnIndex].PanelIds;
            insertIndex = Math.Clamp(insertIndex, 0, target.Count);
            target.Insert(insertIndex, id);
        }

        if (Layout.FloatingPanels.TryGetValue(id, out var fs))
            fs.IsFloating = false;

        Relayout();
        PanelMoved?.Invoke(id);
        _onPersistLayout();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Visibility Toggle
    // ═══════════════════════════════════════════════════════════════════════════

    public void TogglePanelVisibility(string id)
    {
        if (Layout.HiddenPanelIds.Contains(id))
            Layout.HiddenPanelIds.Remove(id);
        else
            Layout.HiddenPanelIds.Add(id);

        Relayout();
        PanelToggled?.Invoke(id);
        _onPersistLayout();
    }

    public bool IsPanelVisible(string id)
        => !Layout.HiddenPanelIds.Contains(id) && !Layout.IsFloating(id);

    // ═══════════════════════════════════════════════════════════════════════════
    // Floating Windows
    // ═══════════════════════════════════════════════════════════════════════════

    public void FloatPanel(string id)
    {
        PersistFromUi();

        if (!Layout.FloatingPanels.TryGetValue(id, out var fs))
            fs = Layout.FloatingPanels[id] = new FloatingPanelState { IsFloating = true };

        fs.IsFloating = true;
        if (fs.Width <= 0) fs.Width = 320;
        if (fs.Height <= 0) fs.Height = 480;

        Relayout();
        OpenFloatingWindow(id);
        _onPersistLayout();
    }

    public void DockPanel(string id)
    {
        SaveFloatingBounds(id);

        if (Layout.FloatingPanels.TryGetValue(id, out var fs))
            fs.IsFloating = false;

        if (_floatingWindows.Remove(id, out var win))
        {
            win.Closing -= FloatingWindowClosing;
            win.Close();
        }

        Relayout();
        _onPersistLayout();
    }

    private void OpenFloatingWindows()
    {
        foreach (var id in Layout.FloatingPanels
            .Where(p => p.Value.IsFloating)
            .Select(p => p.Key)
            .ToArray())
        {
            OpenFloatingWindow(id);
        }
    }

    private void OpenFloatingWindow(string id)
    {
        if (_floatingWindows.ContainsKey(id)) return;
        if (!_panelDefinitions.TryGetValue(id, out var def)) return;

        var cfg = Layout.FloatingPanels.TryGetValue(id, out var f)
            ? f : new FloatingPanelState();

        // Build content for the floating window
        var content = BuildPanelContent?.Invoke(id);
        if (content == null) return;

        var section = BuildPanelSection(def, content);

        var window = new Window
        {
            Title = def.Title,
            Width = Math.Max(220, cfg.Width),
            Height = Math.Max(180, cfg.Height),
            MinWidth = 220,
            MinHeight = 140,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(ParseColor("#1e1e20")),
            Content = section
        };

        var pos = FindFloatingPosition(id, cfg, window.Width, window.Height);
        window.Position = pos;
        window.PositionChanged += (_, _) => SnapFloatingWindow(id);
        window.Closing += FloatingWindowClosing;
        window.Closed += (_, _) =>
        {
            _floatingWindows.Remove(id);
            if (_suppressFloatingClosed) return;
            if (Layout.FloatingPanels.TryGetValue(id, out var floating))
            {
                floating.IsFloating = false;
                _onPersistLayout();
                Relayout();
            }
        };

        _floatingWindows[id] = window;
        window.Show(_owner);
    }

    private PixelPoint FindFloatingPosition(string id, FloatingPanelState cfg,
        double width, double height)
    {
        var x = cfg.X;
        var y = cfg.Y;
        var rect = new Rect(x, y, Math.Max(220, width), Math.Max(180, height));

        for (var i = 0; i < 24 && FloatingOverlaps(id, rect); i++)
            rect = rect.WithX(rect.X + 28).WithY(rect.Y + 28);

        return new PixelPoint((int)Math.Round(rect.X), (int)Math.Round(rect.Y));
    }

    private void SnapFloatingWindow(string id)
    {
        if (_suppressFloatingSnap) return;
        if (!_floatingWindows.TryGetValue(id, out var window)) return;

        var rect = WindowRect(window);
        var snapped = SnapToMagneticTargets(id, rect);
        snapped = PushOutOfOverlaps(id, snapped);

        if (Math.Abs(snapped.X - rect.X) < 0.5 && Math.Abs(snapped.Y - rect.Y) < 0.5)
            return;

        _suppressFloatingSnap = true;
        window.Position = new PixelPoint((int)Math.Round(snapped.X), (int)Math.Round(snapped.Y));
        _suppressFloatingSnap = false;
    }

    private Rect SnapToMagneticTargets(string id, Rect rect)
    {
        foreach (var target in MagneticTargets(id))
            rect = SnapRect(rect, target);
        return rect;
    }

    private static Rect SnapRect(Rect rect, Rect target)
    {
        var x = rect.X;
        var y = rect.Y;

        if (RangesOverlap(rect.Top, rect.Bottom, target.Top, target.Bottom, SnapDistance))
        {
            if (Near(rect.Left, target.Left)) x = target.Left;
            else if (Near(rect.Right, target.Right)) x = target.Right - rect.Width;
            else if (Near(rect.Left, target.Right + FloatingGap)) x = target.Right + FloatingGap;
            else if (Near(rect.Right + FloatingGap, target.Left)) x = target.Left - FloatingGap - rect.Width;
        }

        if (RangesOverlap(rect.Left, rect.Right, target.Left, target.Right, SnapDistance))
        {
            if (Near(rect.Top, target.Top)) y = target.Top;
            else if (Near(rect.Bottom, target.Bottom)) y = target.Bottom - rect.Height;
            else if (Near(rect.Top, target.Bottom + FloatingGap)) y = target.Bottom + FloatingGap;
            else if (Near(rect.Bottom + FloatingGap, target.Top)) y = target.Top - FloatingGap - rect.Height;
        }

        return new Rect(x, y, rect.Width, rect.Height);
    }

    private Rect PushOutOfOverlaps(string id, Rect rect)
    {
        for (var i = 0; i < 8; i++)
        {
            var overlap = FloatingRects(id).FirstOrDefault(other => Intersects(rect, other));
            if (overlap.Width <= 0 || overlap.Height <= 0)
                return rect;

            var pushR = overlap.Right + FloatingGap - rect.Left;
            var pushL = rect.Right - overlap.Left + FloatingGap;
            var pushD = overlap.Bottom + FloatingGap - rect.Top;
            var pushU = rect.Bottom - overlap.Top + FloatingGap;
            var min = Math.Min(Math.Min(pushR, pushL), Math.Min(pushD, pushU));

            if (Math.Abs(min - pushR) < 0.001) rect = rect.WithX(overlap.Right + FloatingGap);
            else if (Math.Abs(min - pushL) < 0.001) rect = rect.WithX(overlap.Left - FloatingGap - rect.Width);
            else if (Math.Abs(min - pushD) < 0.001) rect = rect.WithY(overlap.Bottom + FloatingGap);
            else rect = rect.WithY(overlap.Top - FloatingGap - rect.Height);
        }
        return rect;
    }

    private IEnumerable<Rect> MagneticTargets(string movingId)
    {
        yield return WindowRect(_owner);
        foreach (var r in FloatingRects(movingId))
            yield return r;
    }

    private IEnumerable<Rect> FloatingRects(string movingId)
    {
        foreach (var (id, win) in _floatingWindows)
            if (id != movingId) yield return WindowRect(win);
    }

    private bool FloatingOverlaps(string id, Rect rect)
        => FloatingRects(id).Any(other => Intersects(rect, other));

    private void FloatingWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window) return;
        var id = _floatingWindows.FirstOrDefault(p => p.Value == window).Key;
        if (string.IsNullOrWhiteSpace(id)) return;
        SaveFloatingBounds(id);
    }

    private void SaveFloatingBounds(string id)
    {
        if (!_floatingWindows.TryGetValue(id, out var win)) return;
        if (!Layout.FloatingPanels.TryGetValue(id, out var cfg))
            cfg = Layout.FloatingPanels[id] = new FloatingPanelState();
        cfg.X = win.Position.X;
        cfg.Y = win.Position.Y;
        cfg.Width = win.Width;
        cfg.Height = win.Height;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Canvas-Only Mode
    // ═══════════════════════════════════════════════════════════════════════════

    public bool CanvasOnly => _canvasOnly;

    public void ToggleCanvasOnly()
    {
        if (_canvasOnly) ExitCanvasOnly();
        else EnterCanvasOnly();
    }

    private void EnterCanvasOnly()
    {
        if (_canvasOnly || _rootGrid.ColumnDefinitions.Count < 5) return;

        _savedColumnWidths = _rootGrid.ColumnDefinitions.Select(c => c.Width).ToArray();

        _rootGrid.ColumnDefinitions[0].Width = new GridLength(0);
        _rootGrid.ColumnDefinitions[1].Width = new GridLength(0);
        _rootGrid.ColumnDefinitions[3].Width = new GridLength(0);
        _rootGrid.ColumnDefinitions[4].Width = new GridLength(0);

        _leftRail.IsVisible = false;
        _leftSplitter.IsVisible = false;
        _rightPanelShell.IsVisible = false;
        _rightSplitter.IsVisible = false;

        _canvasOnly = true;
        CanvasOnlyChanged?.Invoke();
    }

    private void ExitCanvasOnly()
    {
        if (!_canvasOnly) return;
        _canvasOnly = false;

        if (_savedColumnWidths is { Length: >= 5 })
        {
            _rootGrid.ColumnDefinitions[0].Width = _savedColumnWidths[0];
            _rootGrid.ColumnDefinitions[1].Width = _savedColumnWidths[1];
            _rootGrid.ColumnDefinitions[3].Width = _savedColumnWidths[3];
            _rootGrid.ColumnDefinitions[4].Width = _savedColumnWidths[4];
        }

        _leftRail.IsVisible = true;
        _leftSplitter.IsVisible = true;
        _rightPanelShell.IsVisible = true;
        _rightSplitter.IsVisible = true;

        CanvasOnlyChanged?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Relayout (replaces RebuildDockers)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Re-layouts columns without rebuilding panel content.
    /// Existing panel sections are reparented — no new content is created.
    /// </summary>
    public void Relayout()
    {
        // Detach all panel sections from their parents
        foreach (var (_, section) in _panelSections)
        {
            if (section.Parent is Panel parent)
                parent.Children.Remove(section);
        }

        // Rebuild left column grid
        var leftGrid = BuildDockColumn(Layout.LeftColumn, -1);
        _leftRail.Child = leftGrid;

        // Rebuild right panel grids
        if (_dockerHostGrid != null)
        {
            // Remove all existing column content (keep splitters)
            var toRemove = _dockerHostGrid.Children
                .Where(c => c is GridSplitter == false && c != _dropIndicator)
                .ToList();
            foreach (var child in toRemove)
                _dockerHostGrid.Children.Remove(child);

            _dockerHostGrid.ColumnDefinitions.Clear();

            var columns = Layout.RightColumns;
            var split = Math.Clamp(Layout.RightDockSplit, 0.2, 0.8);

            for (var i = 0; i < columns.Count; i++)
            {
                var frac = i == 0 ? split : 1.0 - split;
                _dockerHostGrid.ColumnDefinitions.Add(new ColumnDefinition(frac, GridUnitType.Star));

                var col = BuildDockColumn(columns[i], i);
                Grid.SetColumn(col, i * 2);
                _dockerHostGrid.Children.Add(col);

                if (i < columns.Count - 1)
                {
                    _dockerHostGrid.ColumnDefinitions.Add(
                        new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
                    var sp = new GridSplitter
                    {
                        Width = SplitterWidth,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Background = new SolidColorBrush(ParseColor("#3a3a3d"))
                    };
                    sp.DragCompleted += (_, _) => PersistFromUi();
                    Grid.SetColumn(sp, i * 2 + 1);
                    _dockerHostGrid.Children.Add(sp);
                }
            }
        }

        // Update left column width based on content
        SyncLeftColumnWidth();
    }

    private void SyncLeftColumnWidth()
    {
        if (_rootGrid.ColumnDefinitions.Count < 1) return;
        var hasFullPanels = Layout.LeftColumn.PanelIds.Any(id =>
            IsPanelVisible(id) && id != "tools");
        if (hasFullPanels)
        {
            if (Layout.LeftRailWidth <= 56)
                Layout.LeftRailWidth = 280;
            _rootGrid.ColumnDefinitions[0].Width =
                new GridLength(Math.Clamp(Layout.LeftRailWidth, 120, 800), GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[0].MinWidth = 120;
        }
        else
        {
            Layout.LeftRailWidth = 48;
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(48, GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[0].MinWidth = 36;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Build All Panels (called once at init)
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildAllPanels()
    {
        // Pre-create section wrappers for all visible non-floating panels
        foreach (var id in _panelDefinitions.Keys)
        {
            if (Layout.IsFloating(id)) continue;
            if (Layout.HiddenPanelIds.Contains(id)) continue;

            if (!_panelSections.ContainsKey(id))
            {
                var def = _panelDefinitions[id];
                var content = BuildPanelContent?.Invoke(id);
                if (content == null) continue;

                _panelContentCache[id] = content;
                var section = BuildPanelSection(def, content);
                _panelSections[id] = section;
                _panelVisible[id] = true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Persistence
    // ═══════════════════════════════════════════════════════════════════════════

    public void PersistFromUi()
    {
        if (_canvasOnly) return;

        if (_rootGrid.ColumnDefinitions.Count > 4)
        {
            if (_rootGrid.ColumnDefinitions[0].ActualWidth > 0)
                Layout.LeftRailWidth = Math.Max(36, _rootGrid.ColumnDefinitions[0].ActualWidth);
            if (_rootGrid.ColumnDefinitions[4].ActualWidth > 0)
                Layout.RightPanelWidth = Math.Max(300, _rootGrid.ColumnDefinitions[4].ActualWidth);
        }

        if (_rightPanelShell.Child is Grid dockGrid && dockGrid.ColumnDefinitions.Count >= 2)
        {
            var leftW = dockGrid.ColumnDefinitions[0].ActualWidth;
            var rightW = dockGrid.ColumnDefinitions[2].ActualWidth;
            if (leftW + rightW > 1)
                Layout.RightDockSplit = leftW / (leftW + rightW);
        }

        foreach (var id in _floatingWindows.Keys.ToArray())
            SaveFloatingBounds(id);
    }

    public void ApplyLayout(WorkspaceLayout newLayout)
    {
        _suppressFloatingClosed = true;
        foreach (var win in _floatingWindows.Values.ToArray())
            win.Close();
        _suppressFloatingClosed = false;
        _floatingWindows.Clear();

        Layout = newLayout;
        Layout.Normalize(_panelDefinitions.Keys.ToList());

        if (_rootGrid.ColumnDefinitions.Count > 4)
        {
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(
                Math.Clamp(Layout.LeftRailWidth, 36, 800), GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[4].Width = new GridLength(
                Math.Clamp(Layout.RightPanelWidth, 300, 1000), GridUnitType.Pixel);
        }

        Relayout();
        OpenFloatingWindows();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Workspace Presets
    // ═══════════════════════════════════════════════════════════════════════════

    public void SavePreset(string name)
    {
        PersistFromUi();
        _presets[name] = Layout.Clone();
    }

    public void LoadPreset(string name)
    {
        if (_presets.TryGetValue(name, out var preset))
            ApplyLayout(preset.Clone());
    }

    public void DeletePreset(string name)
    {
        _presets.Remove(name);
    }

    public void ResetLayout()
    {
        var defaults = WorkspaceLayout.CreateDefault();
        defaults.Normalize(_panelDefinitions.Keys.ToList());
        ApplyLayout(defaults);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static Color ParseColor(string hex) => Color.Parse(hex);

    private static Rect WindowRect(Window w)
        => new(w.Position.X, w.Position.Y, Math.Max(1, w.Width), Math.Max(1, w.Height));

    private static bool Near(double a, double b) => Math.Abs(a - b) <= SnapDistance;

    private static bool RangesOverlap(double a0, double a1, double b0, double b1, double pad = 0)
        => a0 <= b1 + pad && b0 <= a1 + pad;

    private static bool Intersects(Rect a, Rect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
