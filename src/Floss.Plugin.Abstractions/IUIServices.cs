namespace Floss.App.Features;

/// <summary>
/// Status bar abstraction. Replaces direct access to ISessionShell.FooterStatusText.
/// Plugins should use this instead of touching raw Avalonia controls.
/// </summary>
public interface IStatusBar
{
    /// <summary>Set the status bar text.</summary>
    void SetText(string? text);

    /// <summary>Set the busy state (shows a progress indicator).</summary>
    void SetBusy(bool busy);
}

/// <summary>
/// Dialog service abstraction. Replaces hand-rolled Window construction in plugins.
/// </summary>
public interface IDialogService
{
    /// <summary>Show a simple message dialog.</summary>
    void ShowMessage(string title, string message);

    /// <summary>Set the busy cursor for the entire application.</summary>
    void BeginBusy(string? message = null);

    /// <summary>End the busy cursor.</summary>
    void EndBusy();
}
