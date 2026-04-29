using SkiaSharp;

namespace Floss.App.Brushes;

public interface IBrushTip
{
    SKBitmap GenerateMask(int baseSize, float hardness);
}
