using System.IO;

namespace Floss.App.Tests;

internal static class TestPaths
{
    public static string KraTestFile =>
        Environment.GetEnvironmentVariable("FLOSS_TEST_KRA_PATH")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "electrichearts_20250824A_kiki.kra");
}
