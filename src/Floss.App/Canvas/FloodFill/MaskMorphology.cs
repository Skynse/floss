using System;

namespace Floss.App.Canvas.FloodFill;

/// <summary>4-neighbor grow/shrink for selection masks and fill bleed (Area Scaling).</summary>
public static class MaskMorphology
{
    public static void ApplyAreaScaling(byte[] mask, int width, int height, int deltaPixels)
    {
        if (deltaPixels == 0 || mask.Length < width * height)
            return;

        int steps = Math.Abs(deltaPixels);
        for (int i = 0; i < steps; i++)
        {
            if (deltaPixels > 0)
                Dilate4(mask, width, height);
            else
                Erode4(mask, width, height);
        }
    }

    private static void Dilate4(byte[] mask, int width, int height)
    {
        var src = (byte[])mask.Clone();
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (src[row + x] == 0)
                    continue;
                mask[row + x] = 255;
                if (x > 0) mask[row + x - 1] = 255;
                if (x + 1 < width) mask[row + x + 1] = 255;
                if (y > 0) mask[row - width + x] = 255;
                if (y + 1 < height) mask[row + width + x] = 255;
            }
        }
    }

    private static void Erode4(byte[] mask, int width, int height)
    {
        var src = (byte[])mask.Clone();
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (src[row + x] == 0)
                {
                    mask[row + x] = 0;
                    continue;
                }

                bool keep = true;
                if (x == 0 || src[row + x - 1] == 0) keep = false;
                else if (x + 1 >= width || src[row + x + 1] == 0) keep = false;
                else if (y == 0 || src[row - width + x] == 0) keep = false;
                else if (y + 1 >= height || src[row + width + x] == 0) keep = false;

                mask[row + x] = keep ? (byte)255 : (byte)0;
            }
        }
    }
}
