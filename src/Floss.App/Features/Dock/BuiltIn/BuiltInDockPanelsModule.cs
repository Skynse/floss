using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Dock.BuiltIn;

/// <summary>Registers built-in dock panel controls as session services.</summary>
public sealed class BuiltInDockPanelsModule : IFeatureModule
{
    public void RegisterServices(FeatureServices services, IFeatureSession session)
    {
        services.Register(new DockPanelSync());
        services.Register(new LayerPropertiesDockPanel(session));
        services.Register(new ColorSlidersDockPanel(session));
        services.Register(new BrushDockPanel(session));
        services.Register(new NodeGraphDockPanel(session));
        services.Register(new ToolsDockPanel(session));
        services.Register(new ToolPropertiesDockPanel(session));
        services.Register(new ColorDockPanel(session));
        services.Register(new LayersDockPanel(session));
    }

    public void Register(IFeatureSession session) { }
}
