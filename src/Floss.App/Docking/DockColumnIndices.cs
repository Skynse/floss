namespace Floss.App.Docking;

/// <summary>
/// Column index convention for <see cref="DockLayoutOps.GetColumn"/>.
/// Left stacks: -1 = left-0 (outer), -2 = left-1 (inner, toward canvas), …
/// Bottom: <see cref="Bottom"/>.
/// Right stacks: 0, 1, …
/// </summary>
public static class DockColumnIndices
{
    public const int Bottom = -100;

    public static int Left(int index) => -(index + 1);

    public static int? TryParseLeft(int columnIndex)
        => columnIndex is < 0 and not Bottom ? -columnIndex - 1 : null;

    public static bool IsLeft(int columnIndex)
        => TryParseLeft(columnIndex) is not null;

    public static bool IsRight(int columnIndex) => columnIndex >= 0;
}
