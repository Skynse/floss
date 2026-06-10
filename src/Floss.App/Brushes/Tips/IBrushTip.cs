using SkiaSharp;

namespace Floss.App.Brushes.Tips;

public interface IBrushTip
{
    SKBitmap GenerateMask(int baseSize, float hardness);

    /// <summary>
    /// Whether this tip image has actual color data (not just grayscale/alpha).
    /// When true, the tip is treated as a stamp — its RGBA image is composited
    /// directly at each stamp position instead of using an alpha mask + paint color.
    /// </summary>
    bool HasColor => false;

    /// <summary>
    /// Returns the tip scaled to <paramref name="baseSize"/> as an RGBA bitmap,
    /// or null if the tip has no color data (see <see cref="HasColor"/>).
    /// </summary>
    SKBitmap? GenerateColorStamp(int baseSize) => null;
}
