using System;
using Avalonia;
using Avalonia.Controls;

namespace Floss.App.Controls;

public static class ScrollHelper
{
    public static ScrollViewer Create(Action<ScrollViewer>? configure = null)
    {
        var sv = new ScrollViewer { Padding = new Thickness(0, 0, 12, 0) };
        configure?.Invoke(sv);
        return sv;
    }
}
