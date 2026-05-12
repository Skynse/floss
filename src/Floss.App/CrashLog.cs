using System;
using System.IO;
using System.Runtime.ExceptionServices;

namespace Floss.App;

/// <summary>
/// Writes unhandled exception details to appdata/crash.log.
/// Since the app runs as WinExe (no console), this is the primary
/// way to capture crash diagnostics.
///
/// Four-tier capture strategy:
///   1. FirstChanceException    – logs EVERY exception at throw-time, even if caught.
///   2. Dispatcher.Unhandled     – Avalonia UI thread crashes.
///   3. AppDomain.Unhandled      – background thread crashes.
///   4. DOTNET_EnableCrashReport – native crashes (SIGSEGV, stack overflow) via .NET runtime.
///                                 Writes a JSON crash dump to ~/.dotnet/core/crash-reports/.
/// </summary>
public static class CrashLog
{
    private static readonly string _logPath;

    static CrashLog()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Floss");
        try { Directory.CreateDirectory(dir); } catch { }
        _logPath = Path.Combine(dir, "crash.log");
    }

    public static void Write(Exception ex, string context, bool flushToDisk = false)
    {
        try
        {
            var msg = $"""
                
                === {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ===
                Context: {context}
                {ex}
                """;
            if (flushToDisk)
            {
                // Force fsync for crash-critical writes so the log survives
                // even if the process dies immediately after.
                using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs);
                sw.Write(msg);
                sw.Flush();
                fs.Flush(true);
            }
            else
            {
                File.AppendAllText(_logPath, msg);
            }
        }
        catch
        {
            // Last resort — nothing we can do.  Try stderr as a Hail Mary.
            try { Console.Error.WriteLine($"CRASH [{context}]: logging failed"); }
            catch { }
        }
    }

    /// <summary>Write a raw message (no exception) to the crash log, with fsync.</summary>
    public static void WriteRaw(string message)
    {
        try
        {
            var msg = $"""
                
                === {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ===
                {message}
                """;
            using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs);
            sw.Write(msg);
            sw.Flush();
            fs.Flush(true);
        }
        catch { }
    }

    /// <summary>Path to the crash log file for user reference in error messages.</summary>
    public static string LogPath => _logPath;
}
