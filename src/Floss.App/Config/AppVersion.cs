using System;
using System.Reflection;

namespace Floss.App.Config;

public static class AppVersion
{
    public static Version Current { get; } =
        typeof(AppPaths).Assembly.GetName().Version ?? new Version(0, 1, 0);

    /// <summary>Compare major.minor.build only (ignores pre-release suffix in manifest strings).</summary>
    public static bool IsAtLeast(string minVersion)
    {
        if (string.IsNullOrWhiteSpace(minVersion))
            return true;

        var dash = minVersion.IndexOf('-');
        var core = dash >= 0 ? minVersion[..dash] : minVersion;
        if (!Version.TryParse(core, out var required))
            return true;

        var current = Current;
        if (current.Major != required.Major)
            return current.Major > required.Major;
        if (current.Minor != required.Minor)
            return current.Minor > required.Minor;
        return current.Build >= required.Build;
    }
}
