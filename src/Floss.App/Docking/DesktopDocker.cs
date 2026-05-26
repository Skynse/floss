using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App.Docking;

using static Floss.App.Config.AppColors;

/// <summary>
/// Manages the dockable panel layout — left rail, right panel, drag-drop reordering,
/// floating windows, visibility toggling, canvas-only mode, tab groups and horizontal splits.
///
/// Panel content is built by MainWindow via <see cref="BuildPanelContent"/>.
/// Content state is preserved across layout rebuilds via cached sections.
/// </summary>
public sealed class DesktopDocker
{
    private const double SplitterWidth = 3.0;
    private const int TabDropZone = 22;
    private const double HsplitRatio = 0.3;
    private const int SnapDist = 18;
    private const int Gap = 6;

    private readonly Window _owner;
    private readonly Action _onLayoutChanged;

    // ── Root layout ────────────────────────────────────────────────────────────
    private Grid _rootGrid = null!;
    private Grid _centerArea = null!;

    // ── Panel state ────────────────────────────────────────────────────────────
    private readonly Dictionary<string, Border> _sections = new();
    private readonly Dictionary<string, RowDefinition> _rows = new();
    private readonly Dictionary<string, Control> _contents = new();

    // ── Floating windows ──────────────────────────────────────────────────────
    private readonly Dictionary<string, Window> _floating = new();
    private bool _suppressFloatClosed;
    private bool _suppressFloatSnap;

    // ── Drag state ────────────────────────────────────────────────────────────
    private string? _dragId;
    private Point _dragStart;
    private bool _isDragging;
    private Border? _dropIndicator;
    private TextBlock? _dropHint;
    private int _dropCol = -1;
    private int _dropIdx = -1;
    private Grid? _dockerHostGrid;

    // ── Canvas-only ───────────────────────────────────────────────────────────
    private bool _canvasOnly;
    private GridLength[]? _savedColWidths;

    // ── Layout ────────────────────────────────────────────────────────────────
    public WorkspaceLayout Layout { get; private set; } = WorkspaceLayout.CreateDefault();
    public Control LeftRail { get; private set; } = null!;
    public Control RightPanel { get; private set; } = null!;
    public bool IsCanvasOnly => _canvasOnly;

    // Events
    public event Action? LayoutChanged;
    public event Action? CanvasOnlyChanged;
    public event Action<string>? PanelToggled;

    public Func<string, Control>? BuildPanelContent { get; set; }

    public DesktopDocker(Window owner, Action onLayoutChanged)
    {
        _owner = owner;
        _onLayoutChanged = onLayoutChanged;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════════════

    public void Initialize(
        Grid rootGrid,
        Grid centerArea,
        WorkspaceLayout? layout = null)
    {
        _rootGrid = rootGrid;
        _centerArea = centerArea;
        Layout = layout ?? WorkspaceLayout.CreateDefault();
        Layout.Normalize(PanelRegistry.AllIds);

        // Replace the old left-rail placeholder with our own
        var oldLeft = _rootGrid.Children
            .OfType<Border>()
            .FirstOrDefault(c => Grid.GetColumn(c) == 0);
        if (oldLeft != null) _rootGrid.Children.Remove(oldLeft);

        LeftRail = BuildLeftColumn();
        Grid.SetColumn(LeftRail, 0);
        _rootGrid.Children.Add(LeftRail);

        // Replace the old right-panel placeholder with our own
        var oldRight = _rootGrid.Children
            .OfType<Border>()
            .FirstOrDefault(c => Grid.GetColumn(c) == 4 && c != LeftRail);
        if (oldRight != null) _rootGrid.Children.Remove(oldRight);

        RightPanel = BuildRightPanel();
        Grid.SetColumn(RightPanel, 4);
        _rootGrid.Children.Add(RightPanel);

        // Pre-build all visible, non-floating panel sections
        PrebuildPanels();

        // Populate columns with sections
        PopulateLeftColumn();
        PopulateRightPanel();

        // Open floating windows from config
        OpenFloatingFromConfig();

        SyncLeftColumnWidth();
    }

    public void Rebuild()
    {
        if (_rootGrid == null) return;

        CleanupDragState();
        _rows.Clear();

        // Detach all sections before rebuilding to prevent "already has parent" errors
        foreach (var kv in _sections)
            DetachFromParent(kv.Value);

        // Rebuild left column
        RebuildLeftColumn();

        // Rebuild right panel
        RebuildRightPanel();

        SyncLeftColumnWidth();
        LayoutChanged?.Invoke();
    }

    public void Shutdown()
    {
        _suppressFloatClosed = true;
        foreach (var w in _floating.Values.ToArray())
            w.Close();
        _suppressFloatClosed = false;
        _floating.Clear();
    }

    // ── Layout persistence ────────────────────────────────────────────────────

    public void SaveFromUi()
    {
        if (_canvasOnly) return;

        if (_rootGrid.ColumnDefinitions.Count > 4)
        {
            if (_rootGrid.ColumnDefinitions[0].ActualWidth > 0)
                Layout.LeftRailWidth = Math.Max(36, _rootGrid.ColumnDefinitions[0].ActualWidth);
            if (_rootGrid.ColumnDefinitions[4].ActualWidth > 0)
                Layout.RightPanelWidth = Math.Max(300, _rootGrid.ColumnDefinitions[4].ActualWidth);
        }

        SaveProportions();

        foreach (var id in _floating.Keys.ToArray())
            SaveFloatBounds(id);
    }

    public void Persist() => _onLayoutChanged();

    public void ApplyLayout(WorkspaceLayout newLayout)
    {
        _suppressFloatClosed = true;
        foreach (var w in _floating.Values.ToArray())
            w.Close();
        _suppressFloatClosed = false;
        _floating.Clear();

        Layout = newLayout;
        Layout.Normalize(PanelRegistry.AllIds);

        if (_rootGrid.ColumnDefinitions.Count > 4)
        {
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(
                Math.Clamp(Layout.LeftRailWidth, 36, 800), GridUnitType.Pixel);
            _rootGrid.ColumnDefinitions[4].Width = new GridLength(
                Math.Clamp(Layout.RightPanelWidth, 300, 1000), GridUnitType.Pixel);
        }

        Rebuild();
        OpenFloatingFromConfig();
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    public bool IsVisible(string id)
        => !Layout.HiddenPanelIds.Contains(id) && !Layout.IsFloating(id);

    public void ToggleVisible(string id)
    {
        if (Layout.HiddenPanelIds.Contains(id))
            Layout.HiddenPanelIds.Remove(id);
        else
            Layout.HiddenPanelIds.Add(id);

        Rebuild();
        PanelToggled?.Invoke(id);
    }

    public bool IsFloating(string id)
        => Layout.FloatingPanels.TryGetValue(id, out var f) && f.IsFloating;

    public static string PanelTitle(string id)
        => PanelRegistry.Get(id)?.Title ?? id;

    // ── Context menu ──────────────────────────────────────────────────────────

    public ContextMenu BuildContextMenu(string id)
    {
        var placement = Layout.FindPanel(id);
        var isOnLeft = placement?.ColumnIndex == -1;
        var isFloating = IsFloating(id);

        var floatItem = new MenuItem { Header = isFloating ? "_Dock" : "_Detach" };
        floatItem.Click += (_, _) =>
        {
            if (IsFloating(id)) DockPanel(id);
            else FloatPanel(id);
        };

        var moveLeft = new MenuItem { Header = "Move to _Left Side", IsEnabled = !isFloating && !isOnLeft };
        moveLeft.Click += (_, _) => MoveToColumn(id, -1);

        var moveRight = new MenuItem { Header = "Move to _Right Side", IsEnabled = !isFloating && isOnLeft };
        moveRight.Click += (_, _) => MoveToColumn(id, 0);

        var moveUp = new MenuItem { Header = "Move _Up", IsEnabled = !isFloating };
        moveUp.Click += (_, _) => ShiftPanel(id, -1);

        var moveDown = new MenuItem { Header = "Move _Down", IsEnabled = !isFloating };
        moveDown.Click += (_, _) => ShiftPanel(id, 1);

        var splitMenu = new MenuItem { Header = "Split _Right", IsEnabled = !isFloating };
        splitMenu.SubmenuOpened += (_, _) =>
        {
            splitMenu.ItemsSource = PanelRegistry.AllIds
                .Where(pid => pid != id && !IsFloating(pid) && IsVisible(pid))
                .Select(pid =>
                {
                    var item = new MenuItem { Header = PanelRegistry.Get(pid)?.Title ?? pid };
                    item.Click += (_, _) => SplitHorizontal(id, pid);
                    return (object)item;
                }).ToList();
        };

        var reset = new MenuItem { Header = "_Reset Panel Size" };
        reset.Click += (_, _) => { Layout.PanelProportions.Remove(id); Rebuild(); };

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                floatItem, new Separator(),
                moveLeft, moveRight, new Separator(),
                moveUp, moveDown, new Separator(),
                splitMenu, new Separator(),
                reset
            }
        };
    }

    // ── Panel move / shift / split ────────────────────────────────────────────

    public void ShiftPanel(string id, int delta)
    {
        var placement = Layout.FindPanel(id);
        if (placement == null) return;
        var (colIdx, panelIdx) = placement.Value;
        var col = colIdx < 0 ? Layout.LeftColumn : Layout.RightColumns[colIdx];

        var target = Math.Clamp(panelIdx + delta, 0, col.PanelIds.Count - 1);
        if (target == panelIdx) return;
        col.PanelIds.RemoveAt(panelIdx);
        col.PanelIds.Insert(target, id);

        Rebuild();
    }

    public void MoveToColumn(string id, int colIdx)
    {
        Layout.RemovePanel(id);

        if (colIdx < 0)
            Layout.LeftColumn.PanelIds.Add(id);
        else if (colIdx < Layout.RightColumns.Count)
            Layout.RightColumns[colIdx].PanelIds.Add(id);

        if (Layout.FloatingPanels.TryGetValue(id, out var fs))
            fs.IsFloating = false;
        if (_floating.Remove(id, out var w))
        {
            w.Closing -= FloatClosing;
            w.Close();
        }

        Rebuild();
    }

    public void SplitHorizontal(string sourceId, string targetId)
    {
        DockColumnLayout? srcCol = null;
        if (Layout.LeftColumn.ContainsPanel(sourceId)) srcCol = Layout.LeftColumn;
        else
            foreach (var rc in Layout.RightColumns)
                if (rc.ContainsPanel(sourceId)) { srcCol = rc; break; }
        if (srcCol == null) return;

        Layout.RemovePanel(targetId);
        SplitHorizontalImpl(srcCol, sourceId, targetId);
        Rebuild();
    }

    private static void SplitHorizontalImpl(DockColumnLayout col, string sourceId, string targetId)
    {
        if (col.Rows is not { Count: > 0 })
        {
            var resolved = col.ResolvedRows();
            col.Rows = resolved.Select(r => new DockRowLayout
            {
                PanelIds = r.PanelIds.ToList(),
                Orientation = r.Orientation,
                ActiveIndex = r.ActiveTabIndex
            }).ToList();
        }

        foreach (var row in col.Rows!)
        {
            if (row.PanelIds.Contains(sourceId))
            {
                row.PanelIds.Add(targetId);
                row.Orientation = DockOrientation.Horizontal;
                break;
            }
        }
    }

    // ── Floating ──────────────────────────────────────────────────────────────

    public void FloatPanel(string id)
    {
        if (!Layout.FloatingPanels.TryGetValue(id, out var fs))
            fs = Layout.FloatingPanels[id] = new FloatingPanelState { IsFloating = true };
        fs.IsFloating = true;
        if (fs.Width <= 0) fs.Width = 320;
        if (fs.Height <= 0) fs.Height = 480;

        Rebuild();
        OpenFloat(id);
    }

    public void DockPanel(string id)
    {
        SaveFloatBounds(id);

        if (Layout.FloatingPanels.TryGetValue(id, out var fs))
            fs.IsFloating = false;
        if (_floating.Remove(id, out var w))
        {
            w.Closing -= FloatClosing;
            w.Close();
        }

        Rebuild();
    }

    private void OpenFloatingFromConfig()
    {
        foreach (var id in Layout.FloatingPanels
            .Where(p => p.Value.IsFloating).Select(p => p.Key).ToArray())
            OpenFloat(id);
    }

    private void OpenFloat(string id)
    {
        if (_floating.ContainsKey(id)) return;
        var def = PanelRegistry.Get(id);
        if (def == null) return;

        var cfg = Layout.FloatingPanels.GetValueOrDefault(id) ?? new FloatingPanelState();

        var section = _sections.TryGetValue(id, out var cached)
            ? cached
            : BuildSection(def);

        DetachFromParent(section);

        var w = new Window
        {
            Title = def.Title,
            Width = Math.Max(220, cfg.Width),
            Height = Math.Max(180, cfg.Height),
            MinWidth = 220,
            MinHeight = 140,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(Color.Parse(Bg0)),
            Content = section
        };

        w.Position = FindFloatPos(id, cfg, w.Width, w.Height);
        w.PositionChanged += (_, _) => SnapFloat(id);
        w.Closing += FloatClosing;
        w.Closed += (_, _) =>
        {
            _floating.Remove(id);
            if (_suppressFloatClosed) return;
            if (Layout.FloatingPanels.TryGetValue(id, out var fl))
            {
                fl.IsFloating = false;
                Rebuild();
            }
        };

        _floating[id] = w;
        w.Show(_owner);
    }

    private void FloatClosing(object? s, WindowClosingEventArgs e)
    {
        if (s is not Window w) return;
        var id = _floating.FirstOrDefault(kv => kv.Value == w).Key;
        if (!string.IsNullOrWhiteSpace(id)) SaveFloatBounds(id);
    }

    private void SaveFloatBounds(string id)
    {
        if (!_floating.TryGetValue(id, out var w)) return;
        if (!Layout.FloatingPanels.TryGetValue(id, out var cfg))
            cfg = Layout.FloatingPanels[id] = new FloatingPanelState();
        cfg.X = w.Position.X;
        cfg.Y = w.Position.Y;
        cfg.Width = w.Width;
        cfg.Height = w.Height;
    }

    private PixelPoint FindFloatPos(string id, FloatingPanelState cfg, double w, double h)
    {
        var r = new Rect(cfg.X, cfg.Y, Math.Max(220, w), Math.Max(180, h));
        for (var i = 0; i < 24 && FloatOverlaps(id, r); i++)
            r = r.WithX(r.X + 28).WithY(r.Y + 28);
        return new PixelPoint((int)Math.Round(r.X), (int)Math.Round(r.Y));
    }

    private void SnapFloat(string id)
    {
        if (_suppressFloatSnap) return;
        if (!_floating.TryGetValue(id, out var w)) return;

        var r = WinRect(w);
        var snapped = SnapToMags(id, r);
        snapped = PushOut(id, snapped);

        if (Math.Abs(snapped.X - r.X) < 0.5 && Math.Abs(snapped.Y - r.Y) < 0.5) return;

        _suppressFloatSnap = true;
        w.Position = new PixelPoint((int)Math.Round(snapped.X), (int)Math.Round(snapped.Y));
        _suppressFloatSnap = false;
    }

    private Rect SnapToMags(string id, Rect r)
    {
        foreach (var t in MagTargets(id)) r = Snap(r, t);
        return r;
    }

    private static Rect Snap(Rect r, Rect t)
    {
        var (x, y) = (r.X, r.Y);
        if (Overlap(r.Top, r.Bottom, t.Top, t.Bottom, SnapDist))
        {
            if (Near(r.Left, t.Left)) x = t.Left;
            else if (Near(r.Right, t.Right)) x = t.Right - r.Width;
            else if (Near(r.Left, t.Right + Gap)) x = t.Right + Gap;
            else if (Near(r.Right + Gap, t.Left)) x = t.Left - Gap - r.Width;
        }
        if (Overlap(r.Left, r.Right, t.Left, t.Right, SnapDist))
        {
            if (Near(r.Top, t.Top)) y = t.Top;
            else if (Near(r.Bottom, t.Bottom)) y = t.Bottom - r.Height;
            else if (Near(r.Top, t.Bottom + Gap)) y = t.Bottom + Gap;
            else if (Near(r.Bottom + Gap, t.Top)) y = t.Top - Gap - r.Height;
        }
        return new Rect(x, y, r.Width, r.Height);
    }

    private Rect PushOut(string id, Rect r)
    {
        for (var i = 0; i < 8; i++)
        {
            var o = FloatRects(id).FirstOrDefault(other => Isect(r, other));
            if (o.Width <= 0) return r;
            var (pr, pl, pd, pu) = (o.Right + Gap - r.Left,
                r.Right - o.Left + Gap, o.Bottom + Gap - r.Top, r.Bottom - o.Top + Gap);
            var min = Math.Min(Math.Min(pr, pl), Math.Min(pd, pu));
            if (Math.Abs(min - pr) < 0.001) r = r.WithX(o.Right + Gap);
            else if (Math.Abs(min - pl) < 0.001) r = r.WithX(o.Left - Gap - r.Width);
            else if (Math.Abs(min - pd) < 0.001) r = r.WithY(o.Bottom + Gap);
            else r = r.WithY(o.Top - Gap - r.Height);
        }
        return r;
    }

    private IEnumerable<Rect> MagTargets(string id)
    {
        yield return WinRect(_owner);
        foreach (var r in FloatRects(id)) yield return r;
    }

    private IEnumerable<Rect> FloatRects(string id)
        => _floating.Where(kv => kv.Key != id).Select(kv => WinRect(kv.Value));

    private bool FloatOverlaps(string id, Rect r)
        => FloatRects(id).Any(o => Isect(r, o));

    // ── Canvas-only ───────────────────────────────────────────────────────────

    public void ToggleCanvasOnly()
    {
        if (_canvasOnly) ExitCanvasOnly();
        else EnterCanvasOnly();
    }

    private void EnterCanvasOnly()
    {
        if (_canvasOnly || _rootGrid.ColumnDefinitions.Count < 5) return;
        _savedColWidths = _rootGrid.ColumnDefinitions.Select(c => c.Width).ToArray();

        _rootGrid.ColumnDefinitions[0].Width = new GridLength(0);
        _rootGrid.ColumnDefinitions[1].Width = new GridLength(0);
        _rootGrid.ColumnDefinitions[3].Width = new GridLength(0);
        _rootGrid.ColumnDefinitions[4].Width = new GridLength(0);

        LeftRail.IsVisible = false;
        RightPanel.IsVisible = false;

        _canvasOnly = true;
        CanvasOnlyChanged?.Invoke();
    }

    private void ExitCanvasOnly()
    {
        if (!_canvasOnly) return;
        _canvasOnly = false;

        if (_savedColWidths is { Length: >= 5 })
        {
            _rootGrid.ColumnDefinitions[0].Width = _savedColWidths[0];
            _rootGrid.ColumnDefinitions[1].Width = _savedColWidths[1];
            _rootGrid.ColumnDefinitions[3].Width = _savedColWidths[3];
            _rootGrid.ColumnDefinitions[4].Width = _savedColWidths[4];
        }

        LeftRail.IsVisible = true;
        RightPanel.IsVisible = true;
        CanvasOnlyChanged?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Drag and drop
    // ═══════════════════════════════════════════════════════════════════════════

    public void HeaderPressed(string id, Control header, PointerPressedEventArgs e)
    {
        if (IsFloating(id)) return;
        var pt = e.GetCurrentPoint(header);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _dragId = id;
        _dragStart = pt.Position;
        _isDragging = false;
        _dropCol = -1;
        _dropIdx = -1;
        e.Pointer.Capture(header);
        e.Handled = true;
    }

    public void HeaderMoved(string id, Control header, PointerEventArgs e)
    {
        if (_dragId != id) return;
        var local = e.GetPosition(header);
        var d = local - _dragStart;
        if (!_isDragging && d.X * d.X + d.Y * d.Y < 36) return;

        _isDragging = true;
        if (_rootGrid == null) return;

        UpdateDropPreview(id, e.GetPosition(_rootGrid));
        e.Handled = true;
    }

    public void HeaderReleased(string id, Control header, PointerReleasedEventArgs e)
    {
        if (_dragId != id) return;
        if (_isDragging && _dropCol >= -1)
            ApplyDrop(id, _dropCol, _dropIdx);
        CancelDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    public void CancelDrag()
    {
        _dragId = null;
        _isDragging = false;
        _dropCol = -1;
        _dropIdx = -1;
        if (_dropIndicator != null) _dropIndicator.IsVisible = false;
        if (_dropHint != null) _dropHint.IsVisible = false;
    }

    private void CleanupDragState()
    {
        if (_dropIndicator != null)
        {
            if (_dropIndicator.Parent is Panel p) p.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }
        if (_dropHint != null)
        {
            if (_dropHint.Parent is Panel p) p.Children.Remove(_dropHint);
            _dropHint = null;
        }
    }

    private void UpdateDropPreview(string movingId, Point rootPt)
    {
        if (_rootGrid == null) return;
        var target = ResolveDrop(movingId, rootPt);
        if (target == null)
        {
            if (_dropIndicator != null) _dropIndicator.IsVisible = false;
            if (_dropHint != null) _dropHint.IsVisible = false;
            _dropCol = -1;
            _dropIdx = -1;
            return;
        }

        var (col, idx, x, y, w) = target.Value;
        _dropCol = col;
        _dropIdx = idx;

        _dropIndicator ??= new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1a0078d4")),
            BorderBrush = new SolidColorBrush(Color.Parse(Accent)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            ZIndex = 1000
        };

        _dropHint ??= new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            Background = new SolidColorBrush(Color.Parse("#cc1e1e20")),
            FontSize = 11, FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(8, 3),
            IsHitTestVisible = false,
            ZIndex = 1001
        };

        Panel host;
        string hint;
        if (col < 0)
        {
            host = (Panel)((Border)LeftRail).Child!;
            hint = idx <= -1000 ? "Split beside" : idx < 0 ? "Tab with" : "Move to left";
        }
        else
        {
            host = _dockerHostGrid!;
            hint = idx <= -1000 ? "Split beside" : idx < 0 ? "Tab with" : "Move";
        }

        if (_dropIndicator.Parent is Panel dp && dp != host) dp.Children.Remove(_dropIndicator);
        if (!host.Children.Contains(_dropIndicator)) host.Children.Add(_dropIndicator);

        if (_dropHint.Parent is Panel hp && hp != host) hp.Children.Remove(_dropHint);
        if (!host.Children.Contains(_dropHint)) host.Children.Add(_dropHint);

        var rowH = Math.Max(40, host.Bounds.Height / Math.Max(1, host.Children.OfType<GridSplitter>().Count() + 1));
        _dropIndicator.Width = Math.Max(16, w - 8);
        _dropIndicator.Height = Math.Max(28, rowH * 0.6);
        _dropIndicator.Margin = new Thickness(Math.Round(x + 4), Math.Round(y), 0, 0);

        _dropHint.Text = hint;
        _dropHint.Margin = new Thickness(Math.Round(x + w / 2 - 40), Math.Round(y + rowH * 0.2), 0, 0);

        _dropIndicator.IsVisible = true;
        _dropHint.IsVisible = true;
    }

    private (int col, int idx, double x, double y, double w)? ResolveDrop(
        string movingId, Point rootPt)
    {
        if (_rootGrid == null) return null;
        var rw = _rootGrid.Bounds.Width;
        var rh = _rootGrid.Bounds.Height;
        if (rw <= 0 || rootPt.X < 0 || rootPt.Y < 0 || rootPt.X > rw || rootPt.Y > rh) return null;

        var leftW = _rootGrid.ColumnDefinitions is { Count: > 0 }
            ? Math.Max(_rootGrid.ColumnDefinitions[0].ActualWidth, 48) : 48;

        // Right panel start X in root grid coordinates
        var rightStart = _rootGrid.ColumnDefinitions is { Count: > 4 }
            ? _rootGrid.ColumnDefinitions.Take(4).Sum(cd => cd.ActualWidth) : rw;

        // Left column drop zone
        if (rootPt.X <= leftW + 40)
            return ResolveColumn(-1, Layout.LeftColumn, movingId, rootPt, (Grid)((Border)LeftRail).Child!, 0, leftW);

        // Right panel drop zone
        if (_dockerHostGrid == null) return null;
        var rightW = rw - rightStart;
        if (rightW <= 0 || rootPt.X < rightStart - 40) return null;

        var hostOff = _dockerHostGrid.TranslatePoint(new Point(0, 0), _rootGrid);
        if (hostOff == null) return null;

        var relX = rootPt.X - hostOff.Value.X;
        var colCount = Layout.RightColumns.Count;
        if (colCount == 0) return null;

        var colW = rightW / colCount;
        var ci = Math.Clamp((int)(relX / colW), 0, colCount - 1);

        return ResolveColumn(ci, Layout.RightColumns[ci], movingId, rootPt,
            _dockerHostGrid, hostOff.Value.X + ci * colW, colW);
    }

    private (int ci, int idx, double x, double y, double w)?
        ResolveColumn(int ci, DockColumnLayout col, string movingId,
            Point rootPt, Grid hostGrid, double xOff, double width)
    {
        if (hostGrid == null) return null;

        var resolved = col.ResolvedRows();
        var visible = resolved.Where(r => r.PanelIds.Any(id => id != movingId)).ToList();

        var hostOffY = 0.0;
        if (hostGrid.Parent is Visual pv)
        {
            var off = hostGrid.TranslatePoint(new Point(0, 0), pv);
            if (off != null) hostOffY = off.Value.Y;
        }

        var localY = rootPt.Y - hostOffY;
        var insertIdx = 0;
        var y = 0.0;
        var sectionY = 0.0;

        for (var i = 0; i < visible.Count; i++)
        {
            var row = visible[i];
            var pid = row.PanelIds[0];
            var section = FindSectionForRow(hostGrid, row);

            if (section != null)
            {
                var pos = section.TranslatePoint(new Point(0, 0), hostGrid);
                sectionY = pos?.Y ?? sectionY;
                var sectionH = section.Bounds.Height;
                if (sectionH <= 0) sectionH = 80;

                // H-split zone: right portion of section
                if (localY >= sectionY && localY < sectionY + sectionH
                    && (rootPt.X - xOff) > width * (1 - HsplitRatio))
                    return (ci, -(1000 + i + 1), xOff, sectionY, width);

                // Tab-drop zone: within header
                if (row.Orientation == DockOrientation.Vertical
                    && localY >= sectionY && localY < sectionY + TabDropZone)
                    return (ci, -(i + 1), xOff, sectionY, width);

                if (localY < sectionY + sectionH * 0.5)
                {
                    y = sectionY;
                    insertIdx = i;
                    return (ci, insertIdx, xOff, Math.Max(0, y), width);
                }
                insertIdx = i + 1;
                y = sectionY + sectionH;
            }
            else
            {
                // Estimate
                var estH = 80 + i * 83;
                if (localY < estH) { y = i * 83; insertIdx = i; return (ci, insertIdx, xOff, y, width); }
                insertIdx = i + 1;
                y = (i + 1) * 83;
            }
        }

        return (ci, insertIdx, xOff, Math.Max(0, y), width);
    }

    /// <summary>
    /// Finds the outermost Border section for a resolved row within a host Grid.
    /// Recurses into nested grids (horizontal rows) and DockTabGroup wrappers.
    /// </summary>
    private static Border? FindSectionForRow(Grid hostGrid, ResolvedRow row)
    {
        var pid = row.PanelIds[0];

        foreach (var child in hostGrid.Children)
        {
            if (child is Border b && b.Tag is string t && t == pid) return b;

            // Horizontal row: the section is inside an inner Grid
            if (child is Grid inner)
            {
                foreach (var ic in inner.Children)
                    if (ic is Border ib && ib.Tag is string it && it == pid) return ib;
            }

            // Tab group: find DockTabGroup and return it for position lookup
            if (child is DockTabGroup tg && tg.PanelIds.SequenceEqual(row.PanelIds))
                return child as Border ?? null;
        }

        // Fallback: find in _sections dict
        return null;
    }

    private void ApplyDrop(string id, int ci, int idx)
    {
        SaveFromUi();

        foreach (var c in Layout.RightColumns) c.RemovePanel(id);
        Layout.LeftColumn.RemovePanel(id);
        Layout.BottomColumn.RemovePanel(id);

        if (idx <= -1000)
        {
            var ht = -idx - 1001;
            var tc = ci < 0 ? Layout.LeftColumn : Layout.RightColumns[ci];
            var cids = tc.PanelIds;
            if ((uint)ht >= (uint)cids.Count) return;
            SplitHorizontalImpl(tc, cids[ht], id);
        }
        else if (idx < 0)
        {
            var tt = -idx - 1;
            var tc = ci < 0 ? Layout.LeftColumn : Layout.RightColumns[ci];
            var cids = tc.PanelIds;
            if ((uint)tt >= (uint)cids.Count) return;
            var tk = "tab:" + Guid.NewGuid().ToString()[..8];
            tc.TabGroups[tk] = new TabGroupLayout { PanelIds = new List<string> { cids[tt], id }, ActiveIndex = 1 };
            cids[tt] = tk;
        }
        else if (ci < 0)
        {
            idx = Math.Clamp(idx, 0, Layout.LeftColumn.PanelIds.Count);
            Layout.LeftColumn.PanelIds.Insert(idx, id);
        }
        else
        {
            if ((uint)ci >= (uint)Layout.RightColumns.Count) return;
            var t = Layout.RightColumns[ci].PanelIds;
            idx = Math.Clamp(idx, 0, t.Count);
            t.Insert(idx, id);
        }

        if (Layout.FloatingPanels.TryGetValue(id, out var fl))
            fl.IsFloating = false;

        Rebuild();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Panel sections
    // ═══════════════════════════════════════════════════════════════════════════

    private void PrebuildPanels()
    {
        foreach (var id in PanelRegistry.AllIds)
        {
            if (Layout.IsFloating(id) || Layout.HiddenPanelIds.Contains(id)) continue;
            if (_sections.ContainsKey(id)) continue;

            var def = PanelRegistry.Get(id);
            if (def == null) continue;
            var c = BuildPanelContent?.Invoke(id);
            if (c == null) continue;
            _contents[id] = c;
            _sections[id] = BuildSection(def);
        }
    }

    private Border BuildSection(IDockPanel panel)
    {
        var id = panel.Id;
        var content = _contents.GetValueOrDefault(id) ?? BuildPanelContent?.Invoke(id);
        if (content == null)
            return new Border { Child = new TextBlock { Text = id } };

        content.VerticalAlignment = VerticalAlignment.Stretch;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.ClipToBounds = true;

        var body = BuildBody(id, content);

        var title = new TextBlock
        {
            Text = panel.Title, FontSize = 9, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new Border
        {
            Padding = new Thickness(7, 2, 7, 2),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { title } },
            ContextMenu = BuildContextMenu(id)
        };

        header.PointerPressed += (_, e) => HeaderPressed(id, header, e);
        header.PointerMoved += (_, e) => HeaderMoved(id, header, e);
        header.PointerReleased += (_, e) => HeaderReleased(id, header, e);
        header.PointerCaptureLost += (_, _) => CancelDrag();

        var outer = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ClipToBounds = true
        };
        outer.Children.Add(header);
        outer.Children.Add(body);
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);

        return new Border
        {
            Tag = id,
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            ClipToBounds = true,
            Child = outer
        };
    }

    private static Control BuildBody(string id, Control content)
    {
        if (id == "tool-properties")
            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ClipToBounds = true, Content = content
            };
        return new Border { ClipToBounds = true, Child = content };
    }

    private Border GetOrCreate(string id)
    {
        if (_sections.TryGetValue(id, out var cached))
        {
            DetachFromParent(cached);
            return cached;
        }

        var def = PanelRegistry.Get(id);
        return def == null
            ? new Border { Child = new TextBlock { Text = $"Missing: {id}" } }
            : BuildSection(def);
    }

    /// <summary>
    /// Detaches a control from its visual parent regardless of parent type
    /// (Panel, Border, ContentControl, Decorator).
    /// </summary>
    public static void DetachFromParent(Control ctrl)
    {
        var parent = ctrl.Parent;
        if (parent == null) return;

        if (parent is Panel panel)
            panel.Children.Remove(ctrl);
        else if (parent is Border b && ReferenceEquals(b.Child, ctrl))
            b.Child = null;
        else if (parent is ContentControl cc && ReferenceEquals(cc.Content, ctrl))
            cc.Content = null;
        else if (parent is Decorator d && ReferenceEquals(d.Child, ctrl))
            d.Child = null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Column building
    // ═══════════════════════════════════════════════════════════════════════════

    private Border BuildLeftColumn()
    {
        var grid = new Grid { ClipToBounds = true };
        PopulateColumn(grid, Layout.LeftColumn);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            CacheMode = new BitmapCache(),
            ClipToBounds = true,
            Child = grid
        };
    }

    private Border BuildRightPanel()
    {
        _dockerHostGrid = new Grid { ClipToBounds = true };
        PopulateRightPanel();
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            ClipToBounds = true,
            Child = _dockerHostGrid
        };
    }

    private void RebuildLeftColumn()
    {
        var oldLeft = _rootGrid.Children
            .OfType<Border>()
            .FirstOrDefault(c => Grid.GetColumn(c) == 0);
        if (oldLeft == null) return;

        var leftCol = Grid.GetColumn(oldLeft);
        _rootGrid.Children.Remove(oldLeft);
        LeftRail = BuildLeftColumn();
        Grid.SetColumn(LeftRail, leftCol);
        _rootGrid.Children.Add(LeftRail);
    }

    private void RebuildRightPanel()
    {
        var oldRight = _rootGrid.Children
            .OfType<Border>()
            .FirstOrDefault(c => Grid.GetColumn(c) == 4 && c != LeftRail);
        if (oldRight == null) return;

        var rightCol = Grid.GetColumn(oldRight);
        _rootGrid.Children.Remove(oldRight);
        RightPanel = BuildRightPanel();
        Grid.SetColumn(RightPanel, rightCol);
        _rootGrid.Children.Add(RightPanel);
    }

    private void PopulateLeftColumn()
    {
        var grid = (Grid)((Border)LeftRail).Child!;
        PopulateColumn(grid, Layout.LeftColumn);
    }

    private void PopulateRightPanel()
    {
        if (_dockerHostGrid == null) return;
        _dockerHostGrid.Children.Clear();
        _dockerHostGrid.ColumnDefinitions.Clear();

        var columns = Layout.RightColumns;
        if (columns.Count == 0) return;

        var split = Math.Clamp(Layout.RightDockSplit, 0.2, 0.8);

        for (var i = 0; i < columns.Count; i++)
        {
            var frac = i == 0 ? split : 1.0 - split;
            _dockerHostGrid.ColumnDefinitions.Add(new ColumnDefinition(frac, GridUnitType.Star));

            var wrapper = new Grid { ClipToBounds = true };
            PopulateColumn(wrapper, columns[i]);
            Grid.SetColumn(wrapper, i * 2);
            _dockerHostGrid.Children.Add(wrapper);

            if (i < columns.Count - 1)
            {
                _dockerHostGrid.ColumnDefinitions.Add(new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
                var sp = new GridSplitter
                {
                    Width = SplitterWidth,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.Parse(Stroke))
                };
                sp.DragCompleted += (_, _) => { SaveFromUi(); Persist(); };
                Grid.SetColumn(sp, i * 2 + 1);
                _dockerHostGrid.Children.Add(sp);
            }
        }
    }

    private void PopulateColumn(Grid grid, DockColumnLayout colLayout)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();

        var resolved = colLayout.ResolvedRows();
        var visible = resolved
            .Where(r => r.PanelIds.Any(id => IsVisible(id) && !IsFloating(id)))
            .ToList();

        for (var i = 0; i < visible.Count; i++)
        {
            var row = visible[i];
            var pids = row.PanelIds;
            var primaryId = pids[0];
            var isTab = row.Orientation == DockOrientation.Vertical && pids.Count > 1;
            var isH = row.Orientation == DockOrientation.Horizontal;

            var proportion = Layout.PanelProportions.TryGetValue(
                    isTab ? "tab:" + string.Join("|", pids) : primaryId, out var saved)
                ? Math.Max(0.05, saved)
                : (pids.Min(pid => PanelRegistry.Get(pid)?.Proportion ?? 0.2));
            var minH = pids.Min(pid => PanelRegistry.Get(pid)?.MinHeight ?? 64);

            var rowDef = new RowDefinition(new GridLength(proportion, GridUnitType.Star)) { MinHeight = minH };
            _rows[primaryId] = rowDef;
            grid.RowDefinitions.Add(rowDef);

            Control rowContent;
            if (isH)
                rowContent = BuildHorizontalRow(pids);
            else if (isTab)
                rowContent = BuildTabRow(pids, colLayout);
            else
                rowContent = GetOrCreate(primaryId);

            Grid.SetRow(rowContent, grid.RowDefinitions.Count - 1);
            grid.Children.Add(rowContent);

            if (i == visible.Count - 1) continue;

            grid.RowDefinitions.Add(new RowDefinition(SplitterWidth, GridUnitType.Pixel));
            var sp = new GridSplitter
            {
                Height = SplitterWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Rows,
                Background = new SolidColorBrush(Color.Parse(Stroke))
            };
            sp.DragCompleted += (_, _) => { SaveFromUi(); Persist(); };
            Grid.SetRow(sp, grid.RowDefinitions.Count - 1);
            grid.Children.Add(sp);
        }
    }

    private Grid BuildHorizontalRow(IReadOnlyList<string> pids)
    {
        var hg = new Grid { ClipToBounds = true };
        for (var ci = 0; ci < pids.Count; ci++)
        {
            var id = pids[ci];
            var section = GetOrCreate(id);
            hg.ColumnDefinitions.Add(new ColumnDefinition(1.0 / pids.Count, GridUnitType.Star));
            Grid.SetColumn(section, ci * 2);
            hg.Children.Add(section);

            if (ci < pids.Count - 1)
            {
                hg.ColumnDefinitions.Add(new ColumnDefinition(SplitterWidth, GridUnitType.Pixel));
                var sp = new GridSplitter
                {
                    Width = SplitterWidth,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.Parse(Stroke))
                };
                sp.DragCompleted += (_, _) => { SaveFromUi(); Persist(); };
                Grid.SetColumn(sp, ci * 2 + 1);
                hg.Children.Add(sp);
            }
        }
        return hg;
    }

    private DockTabGroup BuildTabRow(IReadOnlyList<string> pids, DockColumnLayout col)
    {
        var content = new Dictionary<string, Control>();
        var titles = new Dictionary<string, string>();
        foreach (var pid in pids)
        {
            content[pid] = GetOrCreate(pid);
            titles[pid] = PanelRegistry.Get(pid)?.Title ?? pid;
        }

        var activeIdx = 0;
        var key = col.PanelIds.FirstOrDefault(p =>
            col.TabGroups.TryGetValue(p, out var t) && t.PanelIds.SequenceEqual(pids));
        if (key != null && col.TabGroups.TryGetValue(key, out var tab))
            activeIdx = Math.Clamp(tab.ActiveIndex, 0, pids.Count - 1);

        var tg = new DockTabGroup(pids, content, titles, pids[activeIdx]);
        tg.TabChanged += pid =>
        {
            var gk = col.PanelIds.FirstOrDefault(p =>
                col.TabGroups.TryGetValue(p, out var t) && t.PanelIds.SequenceEqual(pids));
            if (gk != null && col.TabGroups.TryGetValue(gk, out var tgl))
                tgl.ActiveIndex = pids.ToList().IndexOf(pid);
        };
        return tg;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Left column width
    // ═══════════════════════════════════════════════════════════════════════════

    private void SyncLeftColumnWidth()
    {
        if (_rootGrid.ColumnDefinitions.Count < 1) return;
        var hasFull = Layout.LeftColumn.PanelIds.Any(id => IsVisible(id) && id != "tools");

        if (hasFull)
        {
            if (Layout.LeftRailWidth <= 56) Layout.LeftRailWidth = 280;
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
    // Proportions
    // ═══════════════════════════════════════════════════════════════════════════

    private void SaveProportions()
    {
        var allCols = new List<(int ci, IReadOnlyList<ResolvedRow> rows)>();
        allCols.Add((-1, Layout.LeftColumn.ResolvedRows()));
        for (var c = 0; c < Layout.RightColumns.Count; c++)
            allCols.Add((c, Layout.RightColumns[c].ResolvedRows()));
        allCols.Add((-2, Layout.BottomColumn.ResolvedRows()));

        foreach (var (ci, rows) in allCols)
        {
            double totalH = 0;
            var heights = new Dictionary<string, double>();
            foreach (var row in rows)
            {
                var pid = row.PanelIds[0];
                if (!_rows.TryGetValue(pid, out var rd)) continue;
                var h = rd.ActualHeight > 0 ? rd.ActualHeight : rd.Height.Value;
                if (h <= 0) continue;
                heights[pid] = h;
                totalH += h;
            }
            if (totalH <= 0) continue;
            foreach (var (id, h) in heights)
                Layout.PanelProportions[id] = Math.Max(0.05, h / totalH);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Rect WinRect(Window w) =>
        new(w.Position.X, w.Position.Y, Math.Max(1, w.Width), Math.Max(1, w.Height));

    private static bool Near(double a, double b) => Math.Abs(a - b) <= SnapDist;
    private static bool Overlap(double a0, double a1, double b0, double b1, double pad = 0) =>
        a0 <= b1 + pad && b0 <= a1 + pad;
    private static bool Isect(Rect a, Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
