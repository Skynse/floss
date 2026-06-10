using Avalonia.Controls;
using Floss.App.Controls;
using Floss.App.Docking;

namespace Floss.App.Features.Dock;

/// <summary>Shared wrapper for dock panel bodies (border, scroll policy).</summary>
public static class DockPanelChrome
{
    public static Control WrapBody(string panelId, Control content)
    {
        var panel = PanelRegistry.Get(panelId);
        return WrapBody(panel?.BodyChrome ?? DockBodyChrome.None, content);
    }

    public static Control WrapBody(DockBodyChrome chrome, Control content)
    {
        content.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        content.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        content.ClipToBounds = true;

        Control child = chrome switch
        {
            DockBodyChrome.VerticalScroll => ScrollHelper.Create(sv =>
            {
                ScrollHelper.UseVisibleScrollBars(sv, horizontal: false, vertical: true);
                sv.ClipToBounds = true;
                sv.Content = content;
            }),
            _ => content
        };

        return new Border
        {
            ClipToBounds = true,
            Child = child
        };
    }
}
