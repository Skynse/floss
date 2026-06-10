using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace Floss.App.Input;

/// <summary>Best-effort OS cursor warp (restores cursor after brush-size gesture).</summary>
internal static class PlatformCursorWarp
{
    public static bool TrySet(PixelPoint screen)
    {
        if (OperatingSystem.IsWindows())
            return SetCursorPos(screen.X, screen.Y);

        if (OperatingSystem.IsLinux())
            return TrySetLinux(screen);

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private static bool TrySetLinux(PixelPoint screen)
    {
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return false;

            var root = XDefaultRootWindow(display);
            XWarpPointer(display, IntPtr.Zero, root, 0, 0, 0, 0, screen.X, screen.Y);
            XFlush(display);
            XCloseDisplay(display);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XWarpPointer(
        IntPtr display,
        IntPtr srcW,
        IntPtr destW,
        int srcX,
        int srcY,
        uint srcWidth,
        uint srcHeight,
        int destX,
        int destY);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);
}
