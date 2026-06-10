using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Floss.App.Input;
using Floss.App.Windows;

namespace Floss.App.Features.Session;

/// <summary>Window-level shell: dialogs, storage, busy overlay, status line.</summary>
public interface ISessionShell
{
    Window Owner { get; }

    KeyboardInputScope KeyboardInputScope { get; }

    IStorageProvider StorageProvider { get; }

    TextBlock FooterStatusText { get; }

    ToolPropertiesWindow? ToolPropertiesWindow { get; set; }

    BusyScope BeginBusy(string message);

    Task ShowMessageAsync(string title, string message);

    void UpdateStatus();
}
