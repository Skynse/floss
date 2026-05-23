using Floss.App.Canvas;
using Floss.App.Native;

namespace Floss.App.Tests;

public sealed class NativeCompositeTests
{
    [Fact]
    public void NativeNormalRow_MatchesManagedReference()
    {
        unsafe
        {
            var dst = new byte[16];
            var src = new byte[] { 10, 20, 30, 255, 40, 50, 60, 128, 0, 0, 0, 0, 200, 0, 0, 255 };
            var expected = (byte[])dst.Clone();

            fixed (byte* dstPtr = dst)
            fixed (byte* srcPtr = src)
            fixed (byte* expectedPtr = expected)
            {
                CompositeNormalRowManaged.Composite(expectedPtr, srcPtr, 4, 255);
                FlossCompositeNative.CompositeNormalRow(dstPtr, srcPtr, 4, 255);
            }

            TestAssertions.SequenceEqual(expected, dst);
        }
    }
}
