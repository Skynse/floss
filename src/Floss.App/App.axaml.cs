using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Input;
using Floss.App.Windows;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Floss.App;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();
    public static ShortcutsConfig Shortcuts { get; private set; } = new();
    public static ToolGroupConfig ToolGroups { get; private set; } = new();
    public static ModifierKeySettings ModifierKeys { get; private set; } = ModifierKeySettings.CreateDefaults();
    public static PenPressureSettings PenPressure { get; private set; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ── Check for crash reports from previous session ─────────────────
        // Two sources: our CrashReport (last-crash.json) for managed
        // exceptions, and .NET runtime crash dumps for native crashes
        // (SIGSEGV, stack overflow) that no managed handler could catch.
        var crashDirs = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Floss", "crash-reports"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Floss")
        };

        CrashReportData? crash = CrashReport.ReadAndClear();
        if (crash == null)
        {
            // Check for .NET runtime crash dumps
            foreach (var dir in crashDirs)
            {
                crash = ReadRuntimeCrashDump(dir);
                if (crash != null) break;
            }
        }

        if (crash != null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
        {
            ShowCrashDialog(crash, desk);
        }

        // ── UI thread crash handler ────────────────────────────────────────
        // Avalonia maintainer advice: after Dispatcher.UnhandledException
        // fires, Avalonia is in an inconsistent state.  DO NOT try to keep
        // the app alive (e.Handled = true).  Instead, write the crash report
        // for next startup and let the process terminate.
        Dispatcher.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "Dispatcher.UnhandledException", flushToDisk: true);
            CrashReport.Write(e.Exception, "Dispatcher.UnhandledException");
            // Don't set e.Handled — Avalonia can't recover.
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartDesktopWithSplash(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void StartDesktopWithSplash(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var splash = new SplashWindow();
        desktop.MainWindow = splash;
        splash.Show();

        desktop.Exit += (_, _) =>
        {
            if (desktop.MainWindow is MainWindow mainWindow)
                mainWindow.FlushLayoutToConfig();
            Config.Save();
            Shortcuts.Save();
            ToolGroups.Save();
            ModifierKeys.Save();
        };

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                splash.SetStatus("Preparing workspace...");
                AppPaths.EnsureDirectories();

                splash.SetStatus("Loading configuration...");
                Config = AppConfig.Load();
                Shortcuts = ShortcutsConfig.Load();
                ModifierKeys = ModifierKeySettings.Load();
                PenPressure = PenPressureSettings.Load(AppPaths.PenPressureSettingsPath);

                splash.SetStatus("Loading tools and brushes...");
                ToolGroups = ToolGroupConfig.Load();

                splash.SetStatus("Opening window...");
                var window = new MainWindow();
                desktop.MainWindow = window;

                var initialFile = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
                if (!string.IsNullOrWhiteSpace(initialFile))
                    window.Opened += async (_, _) => await window.OpenDocumentFromPathAsync(initialFile);

                window.Opened += (_, _) => splash.Close();
                window.Closed += (_, _) =>
                {
                    if (splash.IsVisible)
                        splash.Close();
                };

                window.Show();
            }
            catch
            {
                if (splash.IsVisible)
                    splash.Close();
                throw;
            }
        }, DispatcherPriority.Background);
    }

    private static void ShowCrashDialog(CrashReportData crash, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var msg = $"Floss crashed on the previous session.\n\n" +
                      $"Context: {crash.Context}\n" +
                      $"Exception: {crash.ExceptionType}\n" +
                      $"Message: {crash.Message}\n\n" +
                      $"Crash log: {crash.LogPath}";

            var logPath = crash.LogPath;
            var dialog = new Window
            {
                Title = "Floss — previous session crashed",
                Width = 520,
                Height = 320,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.Parse("#161618")),
                Foreground = new SolidColorBrush(Color.Parse("#dde1e8")),
                SizeToContent = SizeToContent.Height,
                Padding = new Thickness(20),
            };
            var okBtn = new Button
            {
                Content = "OK",
                FontSize = 12,
                Padding = new Thickness(12, 6)
            };
            okBtn.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel { Spacing = 16, Children =
            {
                new TextBlock
                {
                    Text = "💥 Floss recovered from a crash",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#da3633"))
                },
                new TextBlock
                {
                    Text = msg,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#9ea8b4"))
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        new Button
                        {
                            Content = "Open crash log",
                            FontSize = 12,
                            Padding = new Thickness(12, 6)
                        }.WithClick(() => OpenCrashLog(logPath)),
                        okBtn
                    }
                }
            }};
            dialog.Show();
            dialog.Show();
        }
        catch
        {
            // Can't show UI — at least the crash log was written.
        }
    }

    private static void OpenCrashLog(string logPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
            {
                using var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = logPath,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
            }
        }
        catch
        {
            try
            {
                // Fallback: try to open with the default editor
                System.Diagnostics.Process.Start(logPath);
            }
            catch { }
        }
    }

    /// <summary>
    /// Read a .NET runtime crash dump from the given directory.
    /// The runtime writes JSON files named crash-{pid}-{timestamp}.json
    /// when DOTNET_EnableCrashReport=1 and a fatal native error occurs.
    /// </summary>
    private static CrashReportData? ReadRuntimeCrashDump(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var files = Directory.GetFiles(dir, "crash-*.json");
            if (files.Length == 0) return null;

            // Take the most recent crash dump
            var latest = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            var json = File.ReadAllText(latest);
            File.Delete(latest);

            // Parse the runtime crash dump format
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
            var threadName = root.TryGetProperty("thread_name", out var tn) ? tn.GetString() ?? "" : "";
            var signal = root.TryGetProperty("signal", out var sig) ? sig.GetString() ?? "" : "";
            var description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";

            // Build a readable summary
            var stackTrace = TryGetRuntimeStackTrace(root);

            return new CrashReportData
            {
                Timestamp = timestamp,
                Context = $".NET Runtime Crash Report (signal: {signal}, thread: {threadName})",
                ExceptionType = description,
                Message = $"Native crash: {description}",
                StackTrace = stackTrace,
                LogPath = CrashLog.LogPath
            };
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetRuntimeStackTrace(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("payload", out var payload)) return "";
            if (!payload.TryGetProperty("frames", out var frames)) return "";
            var lines = new System.Text.StringBuilder();
            foreach (var frame in frames.EnumerateArray())
            {
                var ip = frame.TryGetProperty("ip", out var ipVal) ? ipVal.GetString() ?? "" : "";
                var module = frame.TryGetProperty("module", out var modVal) ? modVal.GetString() ?? "" : "";
                lines.AppendLine($"  0x{ip} {module}");
            }
            return lines.ToString();
        }
        catch
        {
            return "(see crash report file for full native stack)";
        }
    }
}

internal static class ButtonExtensions
{
    public static Button WithClick(this Button btn, Action action)
    {
        btn.Click += (_, _) => action();
        return btn;
    }
}
