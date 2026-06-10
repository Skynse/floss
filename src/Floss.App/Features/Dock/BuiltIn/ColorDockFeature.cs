using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class ColorDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<ColorDockPanel>();
        DockFeature.Register(
            "color", "Color",
            () => panel,
            defaultZone: "right-0",
            proportion: 0.18,
            minHeight: 180,
            sizing: DockPanelSizing.Auto);
    }
}
