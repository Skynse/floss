using Floss.App.Docking;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

public sealed class NodeGraphDockFeature : IFeatureModule
{
    public void Register(IFeatureSession session)
    {
        var panel = session.GetService<NodeGraphDockPanel>();
        DockFeature.Register(new DockPanelDef(
            "node-graph", "Node Graph",
            () => panel,
            AllowFloat: true,
            AllowHide: true,
            Proportion: 0.35,
            MinHeight: 160,
            DefaultZone: "bottom",
            OnVisibilityChanged: panel.OnVisibilityChanged));
    }
}
