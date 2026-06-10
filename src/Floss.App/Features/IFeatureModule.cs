namespace Floss.App.Features;

/// <summary>
/// A feature module registers dockers, tools, or actions at startup (plugin ctor pattern).
/// </summary>
public interface IFeatureModule
{
    /// <summary>Optional: register services before dock panels are built.</summary>
    void RegisterServices(FeatureServices services, IFeatureSession session) { }

    void Register(IFeatureSession session);
}
