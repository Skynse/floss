using Avalonia;
using Avalonia.Skia;
using System;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Velopack;

namespace Floss.App;

class Program
{
    /// <summary>
    /// IMPORTANT: setting DOTNET_EnableCrashReport / DOTNET_DbgEnableMiniDump
    /// from a ModuleInitializer DOES NOT WORK. The runtime reads those env vars
    /// during its own initialization, before any managed code (including module
    /// initializers) runs. To capture native crashes (SIGSEGV in SkiaSharp etc.)
    /// you MUST launch the app via the run.sh launcher, which sets the env vars
    /// in the parent shell before invoking dotnet.
    ///
    /// We still create the crash-reports directory here so it exists when a
    /// properly-launched run produces dumps.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Floss", "crash-reports");
            Directory.CreateDirectory(dir);
        }
        catch { }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // ── First-chance exceptions ────────────────────────────────────────
        // Fires for EVERY managed exception at throw-time, before any catch.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception?.StackTrace?.Contains("Floss.App") == true)
                CrashLog.Write(e.Exception, "FirstChanceException");
        };

        // ── Non-UI-thread unhandled exceptions ─────────────────────────────
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("Unknown non-UI exception");
            CrashLog.Write(ex, $"AppDomain.UnhandledException (terminating={e.IsTerminating})", flushToDisk: true);
            CrashReport.Write(ex, "AppDomain.UnhandledException");
        };

        // ── Unobserved task exceptions ─────────────────────────────────────
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            CrashLog.Write(e.Exception, $"TaskScheduler.UnobservedTaskException (observed={e.Observed})", flushToDisk: true);
        };

        // ── Shutdown marker ────────────────────────────────────────────────
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            CrashLog.WriteRaw("ProcessExit — clean shutdown");
        };

        try
        {
            CrashLog.WriteRaw($"Floss {GetVersion()} starting (args: {string.Join(" ", args)})");
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Main (top-level catch)", flushToDisk: true);
            CrashReport.Write(ex, "Main (top-level catch)");
            throw;
        }
    }

    private static string GetVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            return ver?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Debug)
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 512 * 1024 * 1024 });
}
