using Avalonia.Controls;
using Avalonia.Media;
using Floss.App.Config;

namespace Floss.App;

/// <summary>Shared UI metrics for flat Floss chrome. See notes/ui-visual-refresh.md.</summary>
public static class FlossUi
{
    public const double IconRail = 20;
    public const double IconPanel = 14;
    public const double IconDense = 13;

    public const int HitRailW = 40;
    public const int HitRailH = 36;
    public const int HitPanel = 28;
    public const int HitDense = 26;

    public static PathIcon Icon(string path, double size, string color = AppColors.TextSecondary)
        => Icons.Make(path, size, new SolidColorBrush(Color.Parse(color)));

    public static PathIcon IconPrimary(string path, double size)
        => Icon(path, size, AppColors.TextPrimary);

    public static PathIcon IconMuted(string path, double size)
        => Icon(path, size, AppColors.TextMuted);
}
