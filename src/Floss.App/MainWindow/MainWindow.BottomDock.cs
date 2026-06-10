using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Docking;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow
{
    private Border? _bottomDockPanel;
    private Grid? _bottomDockerHostGrid;
    private GridSplitter? _bottomDockSplitter;
    private RowDefinition? _bottomDockRow;
    private RowDefinition? _bottomDockSplitterRow;
    private Grid? _centerAreaGrid;

    private void AttachBottomDockToCenter(Grid centerArea)
    {
        _centerAreaGrid = centerArea;
        centerArea.RowDefinitions.Clear();
        centerArea.RowDefinitions.Add(new RowDefinition(26, GridUnitType.Pixel));
        centerArea.RowDefinitions.Add(new RowDefinition(22, GridUnitType.Pixel));
        centerArea.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        _bottomDockSplitterRow = new RowDefinition(0, GridUnitType.Pixel);
        centerArea.RowDefinitions.Add(_bottomDockSplitterRow);
        _bottomDockRow = new RowDefinition(0, GridUnitType.Pixel) { MinHeight = 0, MaxHeight = 800 };
        centerArea.RowDefinitions.Add(_bottomDockRow);
        centerArea.RowDefinitions.Add(new RowDefinition(20, GridUnitType.Pixel));

        foreach (var child in centerArea.Children.ToList())
        {
            var row = Grid.GetRow(child);
            if (row >= 3)
                Grid.SetRow(child, row + 2);
        }

        _bottomDockSplitter = new GridSplitter
        {
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
            Background = new SolidColorBrush(Color.Parse(Stroke)),
            IsVisible = false
        };
        _bottomDockSplitter.DragCompleted += (_, _) => PersistWorkspaceLayout();

        _bottomDockPanel = BuildBottomDockPanel();
        Grid.SetRow(_bottomDockSplitter, 3);
        Grid.SetRow(_bottomDockPanel, 4);
        centerArea.Children.Add(_bottomDockSplitter);
        centerArea.Children.Add(_bottomDockPanel);

        SyncBottomDockVisibility();
    }

    private Border BuildBottomDockPanel()
    {
        var layout = App.Config.WorkspaceLayout;
        var columns = layout.BottomColumns
            .Select((column, index) => (Column: column, Index: index))
            .Where(entry => HasVisibleDockerRows(entry.Column))
            .ToList();

        var grid = BuildMultiColumnDockHost(
            columns.Select(c => (c.Column, DockColumnIndices.Bottom(c.Index))).ToList(),
            BuildDockColumn,
            PersistWorkspaceLayout);
        _bottomDockerHostGrid = grid;

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            ClipToBounds = true,
            Child = grid,
            IsVisible = false
        };
    }

    private bool HasVisibleBottomDock()
        => App.Config.WorkspaceLayout.BottomColumns.Any(HasVisibleDockerRows);

    private void SyncBottomDockVisibility()
    {
        if (_bottomDockRow == null || _bottomDockSplitter == null || _bottomDockPanel == null)
            return;

        var visible = HasVisibleBottomDock();
        _bottomDockPanel.IsVisible = visible;
        _bottomDockSplitter.IsVisible = visible;

        if (visible)
        {
            _bottomDockRow.MinHeight = 120;
            var layout = App.Config.WorkspaceLayout;
            var height = layout.BottomDockHeight > 120 ? layout.BottomDockHeight : 280;
            _bottomDockRow.Height = new GridLength(height, GridUnitType.Pixel);
            _bottomDockSplitterRow!.Height = new GridLength(3);
        }
        else
        {
            _bottomDockRow.MinHeight = 0;
            _bottomDockRow.Height = new GridLength(0);
            _bottomDockSplitterRow!.Height = new GridLength(0);
        }
    }

    private void RebuildBottomDock()
    {
        if (_centerAreaGrid == null || _bottomDockPanel == null) return;

        var row = Grid.GetRow(_bottomDockPanel);
        _centerAreaGrid.Children.Remove(_bottomDockPanel);
        _bottomDockPanel = BuildBottomDockPanel();
        Grid.SetRow(_bottomDockPanel, row);
        _centerAreaGrid.Children.Add(_bottomDockPanel);
        SyncBottomDockVisibility();
    }

    private void UpdateBottomDockHeight()
    {
        if (_bottomDockRow == null || !HasVisibleBottomDock()) return;
        var h = _bottomDockRow.ActualHeight;
        if (h > 80)
            App.Config.WorkspaceLayout.BottomDockHeight = h;
    }
}
