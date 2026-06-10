using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class ToolPropertiesDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<ToolPropertiesDockPanel>();
        DockFeature.Register(
            "tool-properties", "Tool Properties",
            () => panel,
            defaultZone: "left",
            proportion: 0.22,
            minHeight: 120);
    }
}
