namespace Floss.App.Docking;

/// <summary>
/// Encoded column index for <see cref="DockLayoutOps.GetColumn"/> and drag-drop commit.
/// </summary>
public static class DockColumnIndices
{
    public const int BottomBase = -10_000;

    public static int Left(int index) => -(index + 1);

    public static int Right(int index) => index;

    public static int Bottom(int index) => BottomBase - index;

    public static int? TryParseLeft(int columnIndex)
        => columnIndex is < 0 and > BottomBase ? -columnIndex - 1 : null;

    public static bool IsLeft(int columnIndex) => TryParseLeft(columnIndex) is not null;

    public static bool IsRight(int columnIndex) => columnIndex >= 0;

    public static int? TryParseBottom(int columnIndex)
        => columnIndex <= BottomBase ? BottomBase - columnIndex : null;

    public static bool IsBottom(int columnIndex) => TryParseBottom(columnIndex) is not null;

    public static DockZone ZoneOf(int columnIndex)
    {
        if (TryParseLeft(columnIndex) != null) return DockZone.Left;
        if (TryParseBottom(columnIndex) != null) return DockZone.Bottom;
        return DockZone.Right;
    }

    public static int Encode(DockZone zone, int index) => zone switch
    {
        DockZone.Left => Left(index),
        DockZone.Right => Right(index),
        DockZone.Bottom => Bottom(index),
        _ => Right(index)
    };
}
