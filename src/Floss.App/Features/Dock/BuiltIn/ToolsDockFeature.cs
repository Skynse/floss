using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class ToolsDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<ToolsDockPanel>();
        DockFeature.Register(
            "tools", "Tools",
            () => panel,
            defaultZone: "left",
            proportion: 0.22,
            minHeight: 80);
    }
}
