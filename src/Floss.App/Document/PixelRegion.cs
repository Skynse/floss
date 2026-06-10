using System;

namespace Floss.App.Document;

public readonly record struct PixelRegion(int X, int Y, int Width, int Height)
{
    public static PixelRegion Empty => new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRegion Intersect(PixelRegion other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);
        return x2 <= x1 || y2 <= y1 ? Empty : new PixelRegion(x1, y1, x2 - x1, y2 - y1);
    }

    public PixelRegion Union(PixelRegion other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;

        var x1 = Math.Min(X, other.X);
        var y1 = Math.Min(Y, other.Y);
        var x2 = Math.Max(Right, other.Right);
        var y2 = Math.Max(Bottom, other.Bottom);
        return new PixelRegion(x1, y1, x2 - x1, y2 - y1);
    }

    public PixelRegion Inflate(int amount)
        => IsEmpty ? Empty : new PixelRegion(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public PixelRegion Translate(int dx, int dy)
        => IsEmpty ? Empty : new PixelRegion(X + dx, Y + dy, Width, Height);

    public PixelRegion ClipTo(int width, int height)
        => Intersect(new PixelRegion(0, 0, width, height));
}
