using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Floss.App.Controls;

namespace Floss.App.Features.Dock.Panels;

using static Floss.App.Config.AppColors;

internal static class DockPanelUiHelpers
{
    internal static Button SmIconBtn(string icon, string tip)
    {
        var btn = new Button
        {
            Content = FlossUi.Icon(icon, FlossUi.IconPanel),
            Classes = { "icon-tool" },
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    internal static ScrubSlider MkSlider(double min, double max, double value, string tip)
        => ScrubSliderFactory.Create(min, max, value, tip);

    internal static Control LabelSlider(string label, ScrubSlider slider, string fmt = "")
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            Width = 72,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var valText = new TextBlock
        {
            Text = FormatSliderValue(slider.Value, fmt),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            Width = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                valText.Text = FormatSliderValue(slider.Value, fmt);
        };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(lbl, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(valText, Avalonia.Controls.Dock.Right);
        row.Children.Add(lbl);
        row.Children.Add(valText);
        row.Children.Add(slider);
        return row;
    }

    private static string FormatSliderValue(double v, string fmt)
        => string.IsNullOrEmpty(fmt) ? v.ToString("0.##") : $"{v:0.##}{fmt}";
}
