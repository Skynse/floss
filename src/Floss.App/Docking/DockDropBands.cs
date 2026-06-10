using System;

namespace Floss.App.Docking;

/// <summary>Hit-target band sizing for dock drag-and-drop zones.</summary>
public static class DockDropBands
{
    /// <summary>
    /// Clamps a band size so <paramref name="preferredMin"/> never exceeds the computed maximum
    /// (avoids <see cref="Math.Clamp"/> throwing on narrow panel bodies).
    /// </summary>
    public static double BandSize(double bodySize, double ratio, double preferredMin, double maxFraction)
    {
        var max = bodySize * maxFraction;
        if (max <= 0) return 0;
        return Math.Clamp(bodySize * ratio, Math.Min(preferredMin, max), max);
    }
}
