using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Floss.App.Input;

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
            desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, _) =>
            {
                Config.Save();
                Shortcuts.Save();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
