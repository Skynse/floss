using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Controls;

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

    public static ScrubSlider FilterSlider(double minimum, double maximum, double value)
    {
        var s = ScrubSliderFactory.Create(minimum, maximum, value);
        s.Width = 240;
        s.Margin = new Avalonia.Thickness(0, 4, 0, 0);
        return s;
    }

    public static Control FilterRow(TextBlock label, ScrubSlider slider, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }
}
