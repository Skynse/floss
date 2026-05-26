using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App.Docking;

/// <summary>
/// A row that shows multiple panels behind tabs, like Dock library's tab groups.
/// Clicking a tab switches the active panel. The tab strip appears above the content.
/// </summary>
public sealed class DockTabGroup : Grid
{
    private readonly List<string> _panelIds;
    private readonly Dictionary<string, Control> _content;
    private readonly Dictionary<string, Border> _tabs;
    private readonly StackPanel _tabStrip;
    private readonly Border _contentArea;
    private string _activeId;

    public event Action<string>? TabChanged;

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

        // Tab strip
        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = new SolidColorBrush(Color.Parse("#292929")),
            Margin = new Thickness(0, 0, 0, 0)
        };

        foreach (var id in _panelIds)
        {
            var title = titles.GetValueOrDefault(id, id);
            var tab = new Border
            {
                Padding = new Thickness(8, 3),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = title,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.Parse("#8a8a8e")),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            tab.PointerPressed += (_, e) =>
            {
                SetActivePanel(id);
                e.Handled = true;
            };

            _tabs[id] = tab;
            _tabStrip.Children.Add(tab);
        }

        // Content area
        _contentArea = new Border { ClipToBounds = true };

        Grid.SetRow(_tabStrip, 0);
        Grid.SetRow(_contentArea, 1);
        Children.Add(_tabStrip);
        Children.Add(_contentArea);

        RefreshActiveTab();
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
        foreach (var (id, tab) in _tabs)
        {
            var isActive = id == _activeId;
            tab.Background = new SolidColorBrush(Color.Parse(isActive ? "#1a1a1a" : "#292929"));
            tab.BorderBrush = new SolidColorBrush(Color.Parse(isActive ? "#4f78b8" : "Transparent"));
            tab.BorderThickness = new Thickness(0, 0, 0, isActive ? 2 : 0);
            if (tab.Child is TextBlock tb)
                tb.Foreground = new SolidColorBrush(Color.Parse(isActive ? "#e0e0e0" : "#787878"));
        }

        if (_content.TryGetValue(_activeId, out var content))
        {
            if (content.Parent is Panel parent)
                parent.Children.Remove(content);
            _contentArea.Child = content;
        }
    }
}
