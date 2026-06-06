using System;

namespace Floss.App.Canvas.FloodFill;

/// <summary>Reusable per-pixel visit stamps (epoch bump avoids clearing docW×docH each click).</summary>
public sealed class VisitEpoch
{
    private int[] _stamp = [];
    private int _epoch = 1;

    public int[] Stamp => _stamp;
    public int Epoch => _epoch;

    public void BeginPass(int pixelCount)
    {
        if (_stamp.Length < pixelCount)
            _stamp = new int[pixelCount];
        if (++_epoch == int.MaxValue)
        {
            Array.Clear(_stamp);
            _epoch = 1;
        }
    }
}
