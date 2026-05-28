using System;
using System.Runtime.CompilerServices;

namespace Floss.App.Canvas.Engine;

/// <summary>
/// 15-bit fixed-point color per channel (0..32767 range).
/// Modeled on Drawpile's DP_Pixel15 for drift-free compositing.
/// Allows 128 internal blend steps between each 8-bit output level,
/// eliminating the integer rounding errors documented in the brush engine.
/// </summary>
public readonly struct Pixel15 : IEquatable<Pixel15>
{
    public readonly ushort B, G, R, A;

    public Pixel15(ushort b, ushort g, ushort r, ushort a)
    {
        B = b; G = g; R = r; A = a;
    }

    public static readonly Pixel15 Zero = default;
    public static readonly Pixel15 OpaqueWhite = new(32767, 32767, 32767, 32767);

    /// <summary>Convert from 8-bit BGRA.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Pixel15 FromBgra(byte b, byte g, byte r, byte a)
        => new(
            Channel8To15(b),
            Channel8To15(g),
            Channel8To15(r),
            Channel8To15(a));

    /// <summary>Convert to 8-bit BGRA with rounding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ToBgra(out byte b, out byte g, out byte r, out byte a)
    {
        b = Channel15To8(B);
        g = Channel15To8(G);
        r = Channel15To8(R);
        a = Channel15To8(A);
    }

    /// <summary>Convert to BGRA8888 packed uint.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ToBgraUint()
    {
        ToBgra(out var b, out var g, out var r, out var a);
        return (uint)(b | (g << 8) | (r << 16) | (a << 24));
    }

    // ── Fixed-point arithmetic (matching Drawpile's fix15_mul/fix15_div) ─────

    /// <summary>Multiplication in 15-bit fixed point: (a * b) >> 15</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort FixMul(ushort a, ushort b)
        => (ushort)((uint)(a * b) >> 15);

    /// <summary>Division in 15-bit fixed point, saturated.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort FixDiv(ushort a, ushort b)
    {
        if (b == 0) return 32767;
        var result = ((uint)a << 15) / b;
        return result > 32767 ? (ushort)32767 : (ushort)result;
    }

    /// <summary>Premultiply: multiply all color channels by alpha/MAX.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pixel15 Premultiply()
    {
        if (A == 0) return Zero;
        if (A == 32767) return this;
        return new Pixel15(FixMul(B, A), FixMul(G, A), FixMul(R, A), A);
    }

    /// <summary>Unpremultiply with saturation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pixel15 Unpremultiply()
    {
        if (A == 0) return Zero;
        if (A == 32767) return this;
        return new Pixel15(
            Math.Min(FixDiv(B, A), (ushort)32767),
            Math.Min(FixDiv(G, A), (ushort)32767),
            Math.Min(FixDiv(R, A), (ushort)32767),
            A);
    }

    /// <summary>
    /// SrcOver blend with premultiplied alpha (Drawpile formula).
    /// dst = src + dst * (1 - srcAO) where srcAO = srcA * opacity / MAX
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Pixel15 BlendNormalPremultiplied(
        Pixel15 src, Pixel15 dst, ushort opacity = 32767)
    {
        if (src.A == 0) return dst;
        var srcAO = FixMul(src.A, opacity);
        if (srcAO == 0) return dst;
        if (srcAO >= 32767) return src;

        var as1 = (ushort)(32767u - srcAO);
        return new Pixel15(
            (ushort)(FixMul(src.B, srcAO) + FixMul(dst.B, as1)),
            (ushort)(FixMul(src.G, srcAO) + FixMul(dst.G, as1)),
            (ushort)(FixMul(src.R, srcAO) + FixMul(dst.R, as1)),
            (ushort)(FixMul(srcAO, srcAO) + FixMul(dst.A, as1)));
    }

    // ── Channel conversion ────────────────────────────────────────────────────

    /// <summary>8-bit to 15-bit: c * 257 (≈ c * 32767/255).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Channel8To15(byte c)
        => (ushort)((c << 8) | c);

    /// <summary>15-bit to 8-bit with rounding: (c * 255 + 16384) >> 15.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Channel15To8(ushort c)
        => (byte)(((uint)(c * 255u) + 16384u) >> 15);

    // ── Equality ──────────────────────────────────────────────────────────────

    public bool Equals(Pixel15 other)
        => B == other.B && G == other.G && R == other.R && A == other.A;

    public override bool Equals(object? obj)
        => obj is Pixel15 other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(B, G, R, A);

    public static bool operator ==(Pixel15 left, Pixel15 right) => left.Equals(right);
    public static bool operator !=(Pixel15 left, Pixel15 right) => !left.Equals(right);
}
