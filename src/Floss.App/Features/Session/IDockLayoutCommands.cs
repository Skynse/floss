namespace Floss.App.Features.Session;

/// <summary>Workspace docker layout: visibility, rebuild, persist.</summary>
public interface IDockLayoutCommands
{
    bool IsDockerVisible(string id);

    void ToggleDockerVisibility(string id);

    void RebuildDockers();

    void SyncBottomDockVisibility();

    void PersistWorkspaceLayout();
}
