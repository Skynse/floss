namespace Floss.App.Kra;

/// <summary>
/// Parses KRA <c>channelflags</c> / <c>channellockflags</c> attributes.
/// </summary>
internal static class KraChannelFlags
{
    private const int RgbaChannelCount = 4;
    private const int AlphaChannelIndex = 3;

    /// <summary>
    /// KRA inherit alpha — alpha channel disabled in <c>channelflags</c>.
    /// </summary>
    public static bool HasInheritAlpha(string? channelFlags, int channelCount = RgbaChannelCount)
    {
        if (string.IsNullOrEmpty(channelFlags))
            return false;

        if (channelCount < 1)
            return false;

        var alphaIndex = channelCount - 1;
        if (channelFlags.Length <= alphaIndex)
            return false;

        // KRA::stringToFlags: default true, '0' disables the channel.
        return channelFlags[alphaIndex] == '0';
    }

    /// <summary>
    /// Paint-layer alpha lock (<c>channellockflags</c> alpha bit off).
    /// </summary>
    public static bool HasAlphaLock(string? channelLockFlags, int channelCount = RgbaChannelCount)
        => HasInheritAlpha(channelLockFlags, channelCount);
}
