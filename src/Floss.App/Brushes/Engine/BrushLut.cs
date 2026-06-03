using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes.Engine;

public sealed partial class BrushEngine
{
    // Precomputed LUTs for sRGB gamma decode/encode and cube root to avoid
    // MathF.Pow in the hot RgbToLCh/LChToRgb color mixing path.
    private const int CsLutSize = 4096;
    private static readonly float[] s_srgbToLinearLut = CreateSrgbToLinearLut(CsLutSize);
    private static readonly float[] s_linearToSrgbLut = CreateLinearToSrgbLut(CsLutSize);
    private static readonly float[] s_cubeRootLut = CreateCubeRootLut(CsLutSize);
    private static readonly float[] s_cubeLut = CreateCubeLut(CsLutSize);

    private static float[] CreateSrgbToLinearLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x > 0.04045f ? MathF.Pow((x + 0.055f) / 1.055f, 2.4f) : x / 12.92f;
        }
        return lut;
    }

    private static float[] CreateLinearToSrgbLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x > 0.0031308f ? 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f : 12.92f * x;
        }
        return lut;
    }

    private static float[] CreateCubeRootLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x > 0.008856f ? MathF.Pow(x, 1f / 3f) : 7.787f * x + 16f / 116f;
        }
        return lut;
    }

    private static float[] CreateCubeLut(int size)
    {
        var lut = new float[size];
        float inv = 1f / (size - 1);
        for (int i = 0; i < size; i++)
        {
            float x = i * inv;
            lut[i] = x * x * x;
        }
        return lut;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SrgbToLinear(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_srgbToLinearLut[(int)(x * (CsLutSize - 1) + 0.5f)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToSrgb(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_linearToSrgbLut[(int)(x * (CsLutSize - 1) + 0.5f)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FAST_Cbrt(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_cubeRootLut[(int)(x * (CsLutSize - 1) + 0.5f)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FAST_Cube(float x)
        => x < 0f ? 0f : x >= 1f ? 1f : s_cubeLut[(int)(x * (CsLutSize - 1) + 0.5f)];
}
