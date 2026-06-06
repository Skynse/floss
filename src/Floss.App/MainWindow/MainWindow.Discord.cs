namespace Floss.App;

public partial class MainWindow
{
    internal void StartDiscordPresence()
    {
        App.DiscordPresence.Start();
        UpdateDiscordPresence();
    }

    private void UpdateDiscordPresence()
    {
        string? documentName = null;
        string? canvasSize = null;

        if (_activeTab?.HasDocument == true)
        {
            documentName = _activeTab.DisplayTitle;
            var doc = _canvas.Document;
            canvasSize = $"{doc.Width}×{doc.Height}";
        }

        var toolName = _activeToolGroup?.ActivePreset?.Name
            ?? _activeToolGroup?.Name
            ?? (_canvas.ActiveTool != null ? ToolDisplayName(_canvas.ActiveTool) : null);

        App.DiscordPresence.Update(documentName, toolName, canvasSize);
    }
}
