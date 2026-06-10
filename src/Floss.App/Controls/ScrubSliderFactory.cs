using Avalonia.Controls;
using Avalonia.Layout;

namespace Floss.App.Controls;

public static class ScrubSliderFactory
{
    public static ScrubSlider Create(double min, double max, double value, string? tip = null)
    {
        var s = new ScrubSlider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (!string.IsNullOrEmpty(tip))
            ToolTip.SetTip(s, tip);
        return s;
    }
}
