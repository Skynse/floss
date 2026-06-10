using SkiaSharp;

namespace Floss.App.Brushes;

public static class BrushQualityPolicy
{
    public static readonly BrushQuality[] AllLevels =
    [
        BrushQuality.PixelArt,
        BrushQuality.Low,
        BrushQuality.Medium,
        BrushQuality.High
    ];

    public static string DisplayName(BrushQuality quality) => quality switch
    {
        BrushQuality.PixelArt => "Pixel Art",
        BrushQuality.Low => "Low",
        BrushQuality.Medium => "Medium",
        BrushQuality.High => "High",
        _ => "High"
    };

    public static bool IsAntialiasEnabled(BrushQuality quality)
        => quality != BrushQuality.PixelArt;

#pragma warning disable CS0618
    public static SKFilterQuality FilterQuality(BrushQuality quality) => quality switch
    {
        BrushQuality.PixelArt => SKFilterQuality.None,
        BrushQuality.Low => SKFilterQuality.Low,
        BrushQuality.Medium => SKFilterQuality.Medium,
        BrushQuality.High => SKFilterQuality.High,
        _ => SKFilterQuality.High
    };
#pragma warning restore CS0618

    public static bool SnapStampCenterToPixel(BrushQuality quality)
        => quality == BrushQuality.PixelArt;

    /// <summary>Migrate brush files v8–14 (2-level enum) to the 4-level model.</summary>
    public static BrushQuality FromLegacyFileValue(int raw) => raw switch
    {
        0 => BrushQuality.Low,
        1 => BrushQuality.High,
        _ => BrushQuality.High
    };
}
