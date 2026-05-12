using System;
using System.IO;
using System.Text.Json;

namespace Floss.App;

/// <summary>
/// Crash report file that survives across process restarts.
/// On crash, a small JSON file is written to disk.  On next launch,
/// App.axaml.cs checks for it and shows a dialog to the user.
///
/// This is the only reliable way to report crashes to the user when
/// Avalonia's UI thread is corrupted (Dispatcher.UnhandledException)
/// or the crash is native (SIGSEGV via DOTNET_EnableCrashReport).
/// </summary>
public static class CrashReport
{
    private static readonly string _reportPath;

    static CrashReport()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Floss");
        try { Directory.CreateDirectory(dir); } catch { }
        _reportPath = Path.Combine(dir, "last-crash.json");
    }

    public static string ReportPath => _reportPath;

    /// <summary>
    /// Write a crash report that will be displayed on next app launch.
    /// Uses fsync so the file survives even if the process dies immediately after.
    /// </summary>
    public static void Write(string context, string exceptionType, string message, string stackTrace, string? logPath = null)
    {
        try
        {
            var report = new CrashReportData
            {
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                Context = context,
                ExceptionType = exceptionType,
                Message = message,
                StackTrace = stackTrace,
                LogPath = logPath ?? string.Empty
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            using var fs = new FileStream(_reportPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs);
            sw.Write(json);
            sw.Flush();
            fs.Flush(true);
        }
        catch
        {
            try { Console.Error.WriteLine("CRASH REPORT: failed to write crash report file"); }
            catch { }
        }
    }

    /// <summary>
    /// Write a crash report from an Exception object.
    /// </summary>
    public static void Write(Exception ex, string context)
    {
        Write(context, ex.GetType().FullName ?? ex.GetType().Name, ex.Message, ex.ToString(), CrashLog.LogPath);
    }

    /// <summary>
    /// Check if a crash report exists and return it, or null.
    /// Deletes the file after reading so it's only shown once.
    /// </summary>
    public static CrashReportData? ReadAndClear()
    {
        try
        {
            if (!File.Exists(_reportPath))
                return null;
            var json = File.ReadAllText(_reportPath);
            File.Delete(_reportPath);
            return JsonSerializer.Deserialize<CrashReportData>(json);
        }
        catch
        {
            try { if (File.Exists(_reportPath)) File.Delete(_reportPath); } catch { }
            return null;
        }
    }
}

public sealed class CrashReportData
{
    public string Timestamp { get; set; } = "";
    public string Context { get; set; } = "";
    public string ExceptionType { get; set; } = "";
    public string Message { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public string LogPath { get; set; } = "";
}
