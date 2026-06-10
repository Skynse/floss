using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class LayerPropertiesDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<LayerPropertiesDockPanel>();
        DockFeature.Register(
            "layer-properties", "Layer Color",
            () => panel,
            defaultZone: "right-0",
            proportion: 0.10,
            minHeight: 64);
    }
}
