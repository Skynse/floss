using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Floss.App;

using static Floss.App.Config.AppColors;

internal static class FilterControls
{
    public static TextBlock FilterLabel(string text) => new()
    {
        Text = text,
        Width = 60,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
    };

    public static TextBlock FilterValueLabel(string text) => new()
    {
        Text = text,
        Width = 44,
        FontSize = 11,
        TextAlignment = Avalonia.Media.TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse(TextSecondary))
    };

    public static Slider FilterSlider(double minimum, double maximum, double value) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Width = 240,
        Margin = new Avalonia.Thickness(0, 4, 0, 0)
    };

    public static Control FilterRow(TextBlock label, Slider slider, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }
}
