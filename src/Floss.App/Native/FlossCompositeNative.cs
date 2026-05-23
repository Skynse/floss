using System;
using System.Runtime.InteropServices;
using Floss.App.Canvas;

namespace Floss.App.Native;

/// <summary>
/// Facade over csbindgen-produced <see cref="FlossCompositeNativeMethods"/>.
/// Falls back to managed code when the native library is missing.
/// </summary>
internal static unsafe class FlossCompositeNative
{
    private static bool? _available;

    public static bool IsAvailable => _available ??= ProbeNative();

    public static void CompositeNormalRow(byte* dst, byte* src, int width, uint opacity)
    {
        if (width <= 0) return;
        if (IsAvailable)
            FlossCompositeNativeMethods.floss_composite_normal_row(dst, src, width, opacity);
        else
            CompositeNormalRowManaged.Composite(dst, src, width, opacity);
    }

    public static void CompositeNormalBgraRegion(
        byte* dst, int dstStride, byte* src, int srcStride, int width, int height, uint opacity)
    {
        if (width <= 0 || height <= 0) return;
        if (IsAvailable)
            FlossCompositeNativeMethods.floss_composite_normal_bgra_region(
                dst, dstStride, src, srcStride, width, height, opacity);
        else
            CompositeNormalRowManaged.CompositeRegion(dst, dstStride, src, srcStride, width, height, opacity);
    }

    public static void ClearBgraRegion(byte* dst, int dstStride, int width, int height, uint clearValue)
    {
        if (width <= 0 || height <= 0) return;
        if (IsAvailable)
            FlossCompositeNativeMethods.floss_clear_bgra_region(dst, dstStride, width, height, clearValue);
        else
            CompositeNormalRowManaged.ClearRegion(dst, dstStride, width, height, clearValue);
    }

    private static bool ProbeNative()
    {
        try
        {
            return FlossCompositeNativeMethods.floss_composite_version() == FlossCompositeNativeMethods.FLOSS_COMPOSITE_VERSION;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
