using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Docking;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow
{
    private void SaveColumnProportionsFromHost(Grid hostGrid, IReadOnlyList<string> columnIds)
    {
        if (columnIds.Count == 0) return;

        var widths = new List<double>();
        for (var i = 0; i < columnIds.Count; i++)
        {
            var colDefIndex = i * 2;
            if (colDefIndex >= hostGrid.ColumnDefinitions.Count) break;
            var w = hostGrid.ColumnDefinitions[colDefIndex].ActualWidth;
            if (w > 0) widths.Add(w);
        }

        if (widths.Count == 0) return;
        var total = widths.Sum();
        if (total <= 0) return;

        var layout = App.Config.WorkspaceLayout;
        for (var i = 0; i < widths.Count && i < columnIds.Count; i++)
            layout.ColumnProportions[columnIds[i]] = Math.Max(0.05, widths[i] / total);
    }

    private Grid BuildMultiColumnDockHost(
        IReadOnlyList<(DockColumnLayout Column, int ColumnIndex)> columns,
        Func<DockColumnLayout, int, Grid> buildColumn,
        Action persistLayout)
    {
        var grid = new Grid { ClipToBounds = true };
        if (columns.Count == 0)
            return grid;

        var layout = App.Config.WorkspaceLayout;
        var weights = new double[columns.Count];
        var sum = 0.0;
        for (var i = 0; i < columns.Count; i++)
        {
            var id = columns[i].Column.Id;
            var w = layout.ColumnProportions.TryGetValue(id, out var saved)
                ? Math.Max(0.05, saved)
                : 1.0;
            weights[i] = w;
            sum += w;
        }

        sum = Math.Max(sum, 0.01);

        for (var i = 0; i < columns.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(weights[i] / sum, GridUnitType.Star));

            var dock = buildColumn(columns[i].Column, columns[i].ColumnIndex);
            Grid.SetColumn(dock, i * 2);
            grid.Children.Add(dock);

            if (i == columns.Count - 1) continue;

            grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel));
            var splitter = new GridSplitter
            {
                Width = 3,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse(Bg0))
            };
            splitter.DragCompleted += (_, _) => persistLayout();
            Grid.SetColumn(splitter, i * 2 + 1);
            grid.Children.Add(splitter);
        }

        return grid;
    }
}
