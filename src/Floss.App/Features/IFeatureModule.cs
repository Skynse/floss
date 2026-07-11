namespace Floss.App.Features;

/// <summary>
/// Plugin/feature module entry point. Implement this to contribute tools, panels,
/// menu items, actions, overlays, or filters to Floss.
/// </summary>
public interface IFeatureModule
{
    /// <summary>Optional: register services before dock panels are built.</summary>
    void RegisterServices(FeatureServices services, IFeatureSession session) { }

    void Register(IFeatureSession session);
}
