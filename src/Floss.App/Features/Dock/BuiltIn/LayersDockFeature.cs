using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class LayersDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<LayersDockPanel>();
        DockFeature.Register(
            "layers", "Layers",
            () => panel,
            defaultZone: "right-0",
            proportion: 0.35,
            minHeight: 200);
    }
}
