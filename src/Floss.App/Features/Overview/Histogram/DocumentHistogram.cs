using System;

namespace Floss.App.Features.Overview.Histogram;

/// <summary>256-bin per-channel counts from a document overview composite.</summary>
public sealed class DocumentHistogram
{
    public DocumentHistogram(
        int[] red,
        int[] green,
        int[] blue,
        int[] luminance,
        int totalSamples,
        int documentWidth,
        int documentHeight)
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        ArgumentNullException.ThrowIfNull(luminance);
        if (red.Length != 256 || green.Length != 256 || blue.Length != 256 || luminance.Length != 256)
            throw new ArgumentException("Histogram bins must be length 256.");

        Red = red;
        Green = green;
        Blue = blue;
        Luminance = luminance;
        TotalSamples = totalSamples;
        DocumentWidth = documentWidth;
        DocumentHeight = documentHeight;
    }

    public ReadOnlyMemory<int> Red { get; }

    public ReadOnlyMemory<int> Green { get; }

    public ReadOnlyMemory<int> Blue { get; }

    public ReadOnlyMemory<int> Luminance { get; }

    public int TotalSamples { get; }

    public int DocumentWidth { get; }

    public int DocumentHeight { get; }

    public int PeakCount
    {
        get
        {
            var peak = 0;
            var r = Red.Span;
            var g = Green.Span;
            var b = Blue.Span;
            var l = Luminance.Span;
            for (var i = 0; i < 256; i++)
            {
                peak = Math.Max(peak, r[i]);
                peak = Math.Max(peak, g[i]);
                peak = Math.Max(peak, b[i]);
                peak = Math.Max(peak, l[i]);
            }

            return peak;
        }
    }
}
