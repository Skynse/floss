using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class BrushDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<BrushDockPanel>();
        DockFeature.Register(
            "brush", "Brushes",
            () => panel,
            defaultZone: "left",
            proportion: 0.28,
            minHeight: 160);
    }
}
