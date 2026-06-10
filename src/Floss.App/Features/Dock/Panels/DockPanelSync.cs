namespace Floss.App.Features.Dock.Panels;

/// <summary>Shared sync flags for dock panels that update the same brush/layer/color state.</summary>
public sealed class DockPanelSync
{
    public bool SyncingToolPropertyPanel { get; set; }
    public bool SyncingColorSliders { get; set; }
}
