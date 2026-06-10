using Floss.App.Canvas;
using Floss.App.Config;
using Floss.App.Features.Dock.Panels;

namespace Floss.App.Features.Session;

/// <summary>Shorthand accessors for built-in dock panels over <see cref="IFeatureSession"/>.</summary>
internal readonly struct PanelSession
{
    public PanelSession(IFeatureSession session) => Session = session;

    public IFeatureSession Session { get; }

    public DrawingCanvas Canvas => Session.ActiveCanvas;

    public AppConfig Config => Session.Config;

    public DockPanelSync Sync => Session.GetService<DockPanelSync>();

    public ISessionShell Shell => Session.GetService<ISessionShell>();

    public ILayerCommands Layers => Session.GetService<ILayerCommands>();

    public IColorCommands Color => Session.GetService<IColorCommands>();

    public IToolSession Tools => Session.GetService<IToolSession>();

    public IBrushSession Brush => Session.GetService<IBrushSession>();

    public IDockLayoutCommands DockLayout => Session.GetService<IDockLayoutCommands>();

    public ToolsDockPanel ToolsPanel => Session.GetService<ToolsDockPanel>();

    public BrushDockPanel BrushPanel => Session.GetService<BrushDockPanel>();

    public LayerPropertiesDockPanel LayerPropertiesPanel => Session.GetService<LayerPropertiesDockPanel>();

    public ColorSlidersDockPanel ColorSlidersPanel => Session.GetService<ColorSlidersDockPanel>();

    public ToolPropertiesDockPanel ToolPropertiesPanel => Session.GetService<ToolPropertiesDockPanel>();

    public NodeGraphDockPanel NodeGraphPanel => Session.GetService<NodeGraphDockPanel>();
}
