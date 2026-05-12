using Avalonia;
using Avalonia.Skia;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Floss.App;

class Program
{
    /// <summary>
    /// Module initializer runs at the earliest possible point — before any
    /// type initializer, before Main, before the JIT compiles anything else.
    /// This is the only reliable place to set DOTNET_EnableCrashReport so
    /// the .NET runtime's native crash handler is armed before any native
    /// code (SkiaSharp, etc.) has a chance to fault.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Enable .NET runtime crash reports for native/fatal errors (SIGSEGV,
        // stack overflow, etc.).  The runtime writes a JSON crash dump before
        // exiting.  This MUST be set before any native interop runs.
        try { Environment.SetEnvironmentVariable("DOTNET_EnableCrashReport", "1"); } catch { }

        // Redirect crash reports to our app data directory so we can find them.
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Floss", "crash-reports");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("DOTNET_CrashReportDirectory", dir);
        }
        catch { }
    }

    [STAThread]
    public static void Main(string[] args)
    {
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
            CrashLog.Write(e.Exception, $"TaskScheduler.UnobservedTaskException (observed={e.Observed})", flushToDisk: true);
            CrashReport.Write(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        // ── Shutdown marker ────────────────────────────────────────────────
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            CrashLog.WriteRaw("ProcessExit — clean shutdown");
        };

        try
        {
            CrashLog.WriteRaw($"Floss {GetVersion()} starting (args: {string.Join(" ", args)})");
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
