using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Floss.App.Input;
using System.IO;
using System.Linq;

namespace Floss.App;

public partial class App : Application
{
    public static AppConfig      Config    { get; private set; } = new();
    public static ShortcutsConfig Shortcuts { get; private set; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppPaths.EnsureDirectories();
        Config    = AppConfig.Load();
        Shortcuts = ShortcutsConfig.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            var initialFile = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
            if (!string.IsNullOrWhiteSpace(initialFile))
                window.Opened += async (_, _) => await window.OpenDocumentFromPathAsync(initialFile);

            desktop.Exit += (_, _) =>
            {
                Config.Save();
                Shortcuts.Save();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
