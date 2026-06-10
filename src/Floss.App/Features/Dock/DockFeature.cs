using System;
using Avalonia.Controls;
using Floss.App.Docking;

namespace Floss.App.Features.Dock;

/// <summary>Helper for feature modules that add dock panels (<c>dock registry</c> pattern).</summary>
public static class DockFeature
{
    public static void Register(DockPanelDef panel) => PanelRegistry.Register(panel);

    public static void Register(
        string id,
        string title,
        Func<Control> buildContent,
        string defaultZone = "right-0",
        double proportion = 0.25,
        double minHeight = 64,
        DockPanelSizing sizing = DockPanelSizing.Fill,
        bool allowFloat = true,
        bool allowHide = true,
        DockBodyChrome bodyChrome = DockBodyChrome.None,
        Action<bool>? onVisibilityChanged = null)
    {
        Register(new DockPanelDef(
            id, title, buildContent,
            AllowFloat: allowFloat,
            AllowHide: allowHide,
            Proportion: proportion,
            MinHeight: minHeight,
            Sizing: sizing,
            DefaultZone: defaultZone,
            BodyChrome: bodyChrome,
            OnVisibilityChanged: onVisibilityChanged));
    }
}
