using System;

namespace Floss.App.Document;

/// <summary>
/// Precomputed RGB→RGB transform (33³ cube). Rebuilt when adjustment parameters change;
/// compositing uses lookup only — same model as GPU 3D LUTs in Clip Studio Paint.
/// </summary>
public sealed class AdjustmentLayerLutCache
{
    public const int CubeSize = 33;
    private const int CubeCells = CubeSize * CubeSize * CubeSize;
    private const int CubeBytes = CubeCells * 3;

    private byte[]? _rgbCube;
    private ulong _signature;

    public void Invalidate() => _signature = 0;

    public void Ensure(AdjustmentLayerData adj)
    {
        var sig = ComputeSignature(adj);
        if (sig == _signature && _rgbCube != null) return;
        _signature = sig;
        RebuildCube(adj);
    }

    public void Lookup(byte bIn, byte gIn, byte rIn, out byte bOut, out byte gOut, out byte rOut)
    {
        var cube = _rgbCube!;
        var ri = (rIn * (CubeSize - 1) + 127) / 255;
        var gi = (gIn * (CubeSize - 1) + 127) / 255;
        var bi = (bIn * (CubeSize - 1) + 127) / 255;
        var idx = (ri + gi * CubeSize + bi * CubeSize * CubeSize) * 3;
        bOut = cube[idx];
        gOut = cube[idx + 1];
        rOut = cube[idx + 2];
    }

    private void RebuildCube(AdjustmentLayerData adj)
    {
        _rgbCube = new byte[CubeBytes];
        Span<byte> px = stackalloc byte[4];
        for (var bi = 0; bi < CubeSize; bi++)
        {
            for (var gi = 0; gi < CubeSize; gi++)
            {
                for (var ri = 0; ri < CubeSize; ri++)
                {
                    px[2] = (byte)((ri * 255 + (CubeSize - 1) / 2) / (CubeSize - 1));
                    px[1] = (byte)((gi * 255 + (CubeSize - 1) / 2) / (CubeSize - 1));
                    px[0] = (byte)((bi * 255 + (CubeSize - 1) / 2) / (CubeSize - 1));
                    px[3] = 255;
                    Floss.App.Canvas.Compositing.AdjustmentLayerProcessor.TransformPixel(px, adj);
                    var idx = (ri + gi * CubeSize + bi * CubeSize * CubeSize) * 3;
                    _rgbCube[idx] = px[0];
                    _rgbCube[idx + 1] = px[1];
                    _rgbCube[idx + 2] = px[2];
                }
            }
        }
    }

    internal static ulong ComputeSignature(AdjustmentLayerData adj)
    {
        static ulong Mix(ulong h, ulong v) => h * 6364136223846793005UL + v;
        ulong h = (ulong)adj.Kind;
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.Brightness));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.Contrast));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.Hue));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.Saturation));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.Luminosity));
        h = Mix(h, (ulong)adj.Levels);
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.LevelInBlack));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.LevelInWhite));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.LevelGamma));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.LevelOutBlack));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.LevelOutWhite));
        h = Mix(h, HashFloatArray(adj.CurveAll));
        h = Mix(h, HashFloatArray(adj.CurveR));
        h = Mix(h, HashFloatArray(adj.CurveG));
        h = Mix(h, HashFloatArray(adj.CurveB));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.ShadowR));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.ShadowG));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.ShadowB));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.MidtoneR));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.MidtoneG));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.MidtoneB));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.HighlightR));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.HighlightG));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.HighlightB));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(adj.Threshold));
        h = Mix(h, HashFloatArray(adj.GradientStops));
        return h;
    }

    private static ulong HashFloatArray(float[] a)
    {
        ulong h = (ulong)a.Length;
        for (var i = 0; i < a.Length; i++)
            h = h * 6364136223846793005UL + (ulong)BitConverter.SingleToUInt32Bits(a[i]);
        return h;
    }
}
