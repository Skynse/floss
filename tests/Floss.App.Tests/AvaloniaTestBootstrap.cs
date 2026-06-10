using Avalonia;
using Avalonia.Headless;

namespace Floss.App.Tests;

/// <summary>Single-process Avalonia headless setup for tests that touch UI types.</summary>
internal static class AvaloniaTestBootstrap
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized || Application.Current != null)
            {
                _initialized = true;
                return;
            }

            try
            {
                AppBuilder.Configure<Floss.App.App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
            }
            catch (InvalidOperationException)
            {
            }

            _initialized = true;
        }
    }
}
