using Floss.App.Kra;

namespace Floss.App.Tests;

public class KraChannelFlagsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1111", false)]
    [InlineData("1110", true)]
    [InlineData("1100", true)]
    public void HasInheritAlpha_MatchesKraAlphaChannelFlag(string? flags, bool expected)
        => Assert.Equal(expected, KraChannelFlags.HasInheritAlpha(flags));

    [Fact]
    public void HasAlphaLock_UsesChannelLockFlags()
        => Assert.True(KraChannelFlags.HasAlphaLock("1110"));
}
