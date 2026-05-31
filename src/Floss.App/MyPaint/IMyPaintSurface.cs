using System;
using System.Collections.Generic;

namespace Floss.App.MyPaint;

/// <summary>
/// Port of mypaint-surface.h: abstract surface for dab rendering and color picking.
/// </summary>
public interface IMyPaintSurface
{
    /// <summary>Draw a dab. Returns nonzero if the tile was modified.</summary>
    int DrawDab(float x, float y, float radius,
                float colorR, float colorG, float colorB,
                float opaque, float hardness, float softness,
                float alphaEraser,
                float aspectRatio, float angle,
                float lockAlpha,
                float colorize,
                float posterize,
                float posterizeNum,
                float paint);

    /// <summary>Pick up color from the canvas.</summary>
    void GetColor(float x, float y, float radius,
                  out float colorR, out float colorG, out float colorB, out float colorA,
                  float paint);

    void BeginAtomic();
    void EndAtomic(out List<MyPaintRectangle> roi);
}

public struct MyPaintRectangle
{
    public int X;
    public int Y;
    public int Width;
    public int Height;

    public void ExpandToIncludePoint(int x, int y)
    {
        if (Width == 0 && Height == 0)
        {
            X = x; Y = y; Width = 1; Height = 1;
            return;
        }
        int x0 = Math.Min(X, x);
        int y0 = Math.Min(Y, y);
        int x1 = Math.Max(X + Width, x + 1);
        int y1 = Math.Max(Y + Height, y + 1);
        X = x0; Y = y0; Width = x1 - x0; Height = y1 - y0;
    }

    public void ExpandToIncludeRect(MyPaintRectangle other)
    {
        if (other.Width == 0 || other.Height == 0) return;
        if (Width == 0 || Height == 0)
        {
            this = other;
            return;
        }
        int x0 = Math.Min(X, other.X);
        int y0 = Math.Min(Y, other.Y);
        int x1 = Math.Max(X + Width, other.X + other.Width);
        int y1 = Math.Max(Y + Height, other.Y + other.Height);
        X = x0; Y = y0; Width = x1 - x0; Height = y1 - y0;
    }
}
