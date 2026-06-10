using System;

namespace Floss.App.Canvas.FloodFill;

/// <summary>Select/fill all similar pixels in bounds (ContiguousFill off).</summary>
public static class FloodFillNonContiguous
{
    public static void FillInBounds(
        int x0,
        int y0,
        int x1,
        int y1,
        Func<int, int, bool> similar,
        Action<int, int> onPixel)
    {
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (similar(x, y))
                    onPixel(x, y);
            }
        }
    }
}
