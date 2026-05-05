using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;

namespace Floss.App;

public partial class MainWindow
{
    private const double SizePaletteItemSize = 27;
    private static readonly double[] SizePaletteValues =
    [
        0.7, 1, 1.5, 2, 2.5, 3, 4,
        5, 6, 7, 8, 10, 12, 15,
        17, 20, 25, 30, 40, 50, 60,
        70, 80, 100, 120, 150, 170, 200,
        250, 300, 400, 500, 600, 700, 800,
        1000, 1200, 1500, 1700, 2000
    ];

    private Control BuildBrushSizePalette()
    {
        const int columns = 7;
        var rows = (int)Math.Ceiling(SizePaletteValues.Length / (double)columns);

        var grid = new Grid
        {
            Margin = new Thickness(3)
        };
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int i = 0; i < SizePaletteValues.Length; i++)
        {
            var size = SizePaletteValues[i];
            var row = i / columns;
            var col = i % columns;

            var btn = CreateSizeButton(size);
            Grid.SetRow(btn, row);
            Grid.SetColumn(btn, col);
            grid.Children.Add(btn);
        }

        return grid;
    }

    private Button CreateSizeButton(double size)
    {
        var btn = new Button
        {
            Width = SizePaletteItemSize,
            Height = SizePaletteItemSize,
            Padding = new Thickness(1),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(btn, $"{size:F1}");

        var content = CreateSizeVisual(size);
        btn.Content = content;

        var sz = size;
        btn.Click += (_, _) =>
        {
            _sizeSlider.Value = sz;
            UpdateCurrentBrush(p => p with { Size = sz });
        };

        return btn;
    }

    private static Control CreateSizeVisual(double size)
    {
        // Visual dot size: logarithmic scale so small sizes are visible
        // and large sizes don't overflow the button
        var normalizedSize = Math.Log10(size + 1) / Math.Log10(2001);
        var dotSize = Math.Max(3, Math.Min(22, normalizedSize * 22));

        if (size >= 250)
        {
            // For very large sizes, show text instead of a dot
            return new TextBlock
            {
                Text = size >= 1000 ? $"{size / 1000:F0}k" : $"{size:F0}",
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.Parse("#A0AAB4")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var canvas = new Avalonia.Controls.Canvas
        {
            Width = SizePaletteItemSize - 4,
            Height = SizePaletteItemSize - 4
        };

        var ellipse = new Ellipse
        {
            Width = dotSize,
            Height = dotSize,
            Fill = new SolidColorBrush(Color.Parse("#A0AAB4"))
        };

        Avalonia.Controls.Canvas.SetLeft(ellipse, (canvas.Width - dotSize) / 2);
        Avalonia.Controls.Canvas.SetTop(ellipse, (canvas.Height - dotSize) / 2);
        canvas.Children.Add(ellipse);

        // Show small text inside for medium sizes
        if (size >= 10 && size < 100)
        {
            var text = new TextBlock
            {
                Text = $"{size:F0}",
                FontSize = 7,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Avalonia.Controls.Canvas.SetLeft(text, (canvas.Width - 16) / 2);
            Avalonia.Controls.Canvas.SetTop(text, (canvas.Height - 10) / 2);
            canvas.Children.Add(text);
        }

        return canvas;
    }
}
