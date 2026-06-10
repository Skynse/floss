using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class ColorSliderDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<ColorSlidersDockPanel>();
        DockFeature.Register(
            "color-slider", "Color Sliders",
            () => panel,
            defaultZone: "right-0",
            proportion: 0.12,
            minHeight: 120);
    }
}
