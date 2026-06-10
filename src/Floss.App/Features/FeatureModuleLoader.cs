using System.Collections.Generic;
using Floss.App.Docking;
using Floss.App.Features.Dock;
using Floss.App.Features.Dock.BuiltIn;
using Floss.App.Features.Plugins;

namespace Floss.App.Features;

/// <summary>Runs feature modules at startup (before workspace layout normalize).</summary>
public static class FeatureModuleLoader
{
    private static IFeatureModule[]? _modules;

    public static void RegisterServices(IFeatureSession session, FeatureServices services)
    {
        foreach (var module in AllModules())
            module.RegisterServices(services, session);
    }

    public static void RegisterAll(IFeatureSession session)
    {
        if (PanelRegistry.AllIds.Count > 0)
            return;

        foreach (var module in AllModules())
            module.Register(session);
    }

    /// <summary>Built-in modules plus DLL plugins from <see cref="Config.AppPaths.PluginsDirectory"/>.</summary>
    public static IReadOnlyList<IFeatureModule> AllModules()
    {
        if (_modules != null)
            return _modules;

        var list = new List<IFeatureModule>
        {
            new BuiltInDockPanelsModule(),
            new ToolsDockFeature(),
            new BrushDockFeature(),
            new ToolPropertiesDockFeature(),
            new LayerPropertiesDockFeature(),
            new LayersDockFeature(),
            new ColorDockFeature(),
            new ColorSliderDockFeature(),
            new NodeGraphDockFeature(),
        new MiniViewportDockFeature(),
        new HistogramDockFeature(),
        new UndoHistoryDockFeature(),
        };

        list.AddRange(FeaturePluginLoader.LoadAll());
        _modules = list.ToArray();
        return _modules;
    }

    /// <summary>Clear cached module list (tests).</summary>
    public static void ResetForTests()
    {
        _modules = null;
        FeaturePluginLoader.ResetForTests();
    }
}
