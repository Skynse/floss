using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input.GestureRecognizers;

namespace Floss.App.Controls;

public static class ScrollHelper
{
    public static ScrollViewer Create(Action<ScrollViewer>? configure = null)
    {
        var sv = new ScrollViewer { Padding = new Thickness(0, 0, 12, 0) };
        configure?.Invoke(sv);
        DisablePointerPanScroll(sv);
        return sv;
    }

    // Disables pointer-drag (pan) scrolling on the ScrollViewer while keeping
    // mouse wheel and scrollbar interactions intact.
    public static void DisablePointerPanScroll(ScrollViewer sv)
    {
        sv.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollContentPresenter>("PART_ContentPresenter") is not { } p)
                return;
            var recognizer = p.GestureRecognizers.OfType<ScrollGestureRecognizer>().FirstOrDefault();
            if (recognizer != null)
            {
                recognizer.CanHorizontallyScroll = false;
                recognizer.CanVerticallyScroll = false;
            }
        };
    }
}
