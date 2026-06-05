using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Config;

namespace Floss.App.Docking;

using static AppColors;

/// <summary>
/// Tab strip + content for a dock row. Tabs can be dragged out to form new docker rows.
/// </summary>
public sealed class DockTabGroup : Grid
{
    private const int DragThreshold = 6;

    private readonly List<string> _panelIds;
    private readonly Dictionary<string, Control> _content;
    private readonly Dictionary<string, Border> _tabs;
    private readonly StackPanel _tabStrip;
    private readonly Border _contentArea;
    private string _activeId;

    private string? _dragTabId;
    private Point _dragStart;
    private bool _dragActive;

    public event Action<string>? TabChanged;
    public event Action<string, PointerPressedEventArgs>? TabDragStarted;
    public event Action<string, PointerEventArgs>? TabDragMoved;
    public event Action<string, PointerReleasedEventArgs>? TabDragEnded;

    public string ActivePanelId => _activeId;
    public IReadOnlyList<string> PanelIds => _panelIds;

    public DockTabGroup(IEnumerable<string> panelIds, Dictionary<string, Control> content,
        Dictionary<string, string> titles, string? activeId = null)
    {
        _panelIds = panelIds.ToList();
        _content = content;
        _tabs = new();
        _activeId = activeId ?? _panelIds[0];

        RowDefinitions = new RowDefinitions("Auto,*");
        ClipToBounds = true;

        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Spacing = 0,
            Margin = new Thickness(0)
        };

        for (var i = 0; i < _panelIds.Count; i++)
        {
            var id = _panelIds[i];
            var title = titles.GetValueOrDefault(id, id);
            var isLast = i == _panelIds.Count - 1;
            var tab = new Border
            {
                Padding = new Thickness(12, 6),
                CornerRadius = new CornerRadius(0),
                BorderBrush = new SolidColorBrush(Color.Parse(StrokeSubtle)),
                BorderThickness = new Thickness(0, 0, isLast ? 1 : 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = title,
                    FontSize = 11,
                    FontWeight = FontWeight.Normal,
                    Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            tab.PointerPressed += (_, e) => OnTabPointerPressed(id, tab, e);
            tab.PointerMoved += (_, e) => OnTabPointerMoved(id, e);
            tab.PointerReleased += (_, e) => OnTabPointerReleased(id, e);
            tab.PointerCaptureLost += (_, _) => EndTabDrag(cancelOnly: true);

            _tabs[id] = tab;
            _tabStrip.Children.Add(tab);
        }

        _contentArea = new Border
        {
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse(Bg1))
        };

        Grid.SetRow(_tabStrip, 0);
        Grid.SetRow(_contentArea, 1);
        Children.Add(_tabStrip);
        Children.Add(_contentArea);

        RefreshActiveTab();
    }

    private void OnTabPointerPressed(string id, Border tab, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed) return;
        _dragTabId = id;
        _dragStart = e.GetPosition(this);
        _dragActive = false;
        TabDragStarted?.Invoke(id, e);
        e.Pointer.Capture(tab);
        e.Handled = true;
    }

    private void OnTabPointerMoved(string id, PointerEventArgs e)
    {
        if (_dragTabId != id) return;
        var pt = e.GetPosition(this);
        if (!_dragActive && DragDistance(pt, _dragStart) < DragThreshold)
            return;

        if (!_dragActive)
        {
            _dragActive = true;
            SetActivePanel(id);
        }

        TabDragMoved?.Invoke(id, e);
        e.Handled = true;
    }

    private void OnTabPointerReleased(string id, PointerReleasedEventArgs e)
    {
        if (_dragTabId != id) return;
        if (!_dragActive)
            SetActivePanel(id);
        else
            TabDragEnded?.Invoke(id, e);

        EndTabDrag(cancelOnly: false);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EndTabDrag(bool cancelOnly)
    {
        _dragTabId = null;
        _dragActive = false;
    }

    private static double DragDistance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public void SetActivePanel(string id)
    {
        if (!_panelIds.Contains(id) || _activeId == id) return;
        _activeId = id;
        RefreshActiveTab();
        TabChanged?.Invoke(id);
    }

    private void RefreshActiveTab()
    {
        for (var i = 0; i < _panelIds.Count; i++)
        {
            var id = _panelIds[i];
            var tab = _tabs[id];
            var isActive = id == _activeId;
            var isLast = i == _panelIds.Count - 1;
            tab.Background = new SolidColorBrush(Color.Parse(isActive ? Bg1 : Bg2));
            tab.BorderBrush = new SolidColorBrush(Color.Parse(StrokeSubtle));
            tab.BorderThickness = new Thickness(0, 0, isLast ? 1 : 0, 0);
            if (tab.Child is TextBlock tb)
            {
                tb.Foreground = new SolidColorBrush(Color.Parse(isActive ? TextPrimary : TextMuted));
                tb.FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }

        if (_content.TryGetValue(_activeId, out var content))
        {
            if (content.Parent is Panel parent)
                parent.Children.Remove(content);
            _contentArea.Child = content;
        }
    }
}
