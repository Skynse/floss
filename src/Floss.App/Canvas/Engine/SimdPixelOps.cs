using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;

namespace Floss.App.Canvas.Engine;

public static unsafe class SimdPixelOps
{
    // recip255[i] = round(255 * 256 / i) for i in [1,255].
    // Used to divide by outA without integer division:
    // result = (x * recip255[outA]) >> 8 for x in [0, 255].
    private static readonly uint[] s_recip255 = BuildRecip255();
    private static uint[] BuildRecip255()
    {
        var t = new uint[256];
        for (int i = 1; i < 256; i++)
            t[i] = (uint)Math.Round(255.0 * 256.0 / i);
        return t;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SrcOverRow(byte* dst, byte* src, int pixelCount, uint opacity = 255)
    {
        if (opacity == 0) return;
        if (opacity == 255) SrcOverRowFull(dst, src, pixelCount);
        else SrcOverRowPartial(dst, src, pixelCount, (int)opacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SrcOverRegion(
        byte* dst, int dstStride, byte* src, int srcStride,
        int width, int height, uint opacity)
    {
        for (var y = 0; y < height; y++)
            SrcOverRow(dst + y * dstStride, src + y * srcStride, width, opacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearRegion(
        byte* dst, int dstStride, int width, int height, uint clearValue)
    {
        for (var y = 0; y < height; y++)
        {
            var row = (uint*)(dst + y * dstStride);
            for (var x = 0; x < width; x++)
                row[x] = clearValue;
        }
    }

    /// <summary>
    /// : blend src colors into dst
    /// colors using src alpha, but keep dst alpha unchanged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SrcOverColorOnlyRow(byte* dst, byte* src, int pixelCount, uint opacity = 255)
    {
        if (opacity == 0) return;
        bool fullOpac = opacity == 255;
        for (var i = 0; i < pixelCount; i++)
        {
            var off = i * 4;
            var sa = fullOpac ? src[off + 3] : (src[off + 3] * (int)opacity + 127) / 255;
            if (sa <= 0) continue;
            if (sa >= 255) { dst[off] = src[off]; dst[off + 1] = src[off + 1]; dst[off + 2] = src[off + 2]; continue; }
            var inv = 255 - sa;
            dst[off]     = (byte)((src[off]     * sa + dst[off]     * inv + 127) / 255);
            dst[off + 1] = (byte)((src[off + 1] * sa + dst[off + 1] * inv + 127) / 255);
            dst[off + 2] = (byte)((src[off + 2] * sa + dst[off + 2] * inv + 127) / 255);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StampSrcOver(
        byte* tilePixel, byte srcB, byte srcG, byte srcR,
        int stampA, bool alphaLocked)
    {
        if (stampA <= 0) return;
        if (stampA > 255) stampA = 255;

        byte ttda = tilePixel[3];
        if (ttda == 0)
        {
            // Match AlphaLockPixelOps: locked brushes never expand coverage.
            if (alphaLocked) return;
            tilePixel[0] = srcB;
            tilePixel[1] = srcG;
            tilePixel[2] = srcR;
            tilePixel[3] = (byte)stampA;
            return;
        }

        if (alphaLocked)
        {
            int inv = 255 - stampA;
            tilePixel[0] = (byte)((srcB * stampA + tilePixel[0] * inv + 127) / 255);
            tilePixel[1] = (byte)((srcG * stampA + tilePixel[1] * inv + 127) / 255);
            tilePixel[2] = (byte)((srcR * stampA + tilePixel[2] * inv + 127) / 255);
            return;
        }

        int invSrcA = 255 - stampA;
        int dstCont = (ttda * invSrcA + 127) / 255;
        int outA = stampA + dstCont;
        if (outA == 0) return;

        int half = outA >> 1;
        tilePixel[0] = (byte)((srcB * stampA + tilePixel[0] * dstCont + half) / outA);
        tilePixel[1] = (byte)((srcG * stampA + tilePixel[1] * dstCont + half) / outA);
        tilePixel[2] = (byte)((srcR * stampA + tilePixel[2] * dstCont + half) / outA);
        tilePixel[3] = (byte)outA;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StampSrcOverRow(
        byte* tileRow, byte srcB, byte srcG, byte srcR,
        Span<byte> maskAlpha, int count, int stampA, bool alphaLocked)
    {
        if (stampA <= 0) return;
        if (stampA > 255) stampA = 255;

        for (int x = 0; x < count; x++)
        {
            int ma = maskAlpha[x];
            if (ma == 0) continue;
            int sa = (ma * stampA + 127) / 255;
            if (sa > 255) sa = 255;
            StampSrcOver(tileRow + x * 4, srcB, srcG, srcR, sa, alphaLocked);
        }
    }

    /// <summary>
    /// Blend a solid brush color into a tile row using a greyscale mask.
    /// Layer tiles store unpremul BGRA; use associated-alpha compositing (scalar path).
    /// AVX2 is only used for alpha-lock (linear rgb blend, alpha preserved).
    /// Crucial fix for dark stroke halos — see notes/dark-brush-edge-fix.md.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void StampSrcOverMaskedRow(
        byte* dst, byte* mask, int count,
        int srcB, int srcG, int srcR, int opacity, bool alphaLocked)
    {
        if (count <= 0 || opacity <= 0) return;
        if (opacity > 255) opacity = 255;

        if (alphaLocked && Avx2.IsSupported && Sse41.IsSupported && count >= 8)
            StampSrcOverMaskedRowAvx2(dst, mask, count, srcB, srcG, srcR, opacity, alphaLocked: true);
        else
            StampSrcOverMaskedRowScalar(dst, mask, count, srcB, srcG, srcR, opacity, alphaLocked);
    }

    /// <summary>
    /// Premultiplied tile blend: dst (premul) over dst (premul) with optional
    /// per-tile opacity. Formula (all channels including alpha):
    /// out = (src * srcAO + dst * (255-srcAO)) / 255
    /// where srcAO = srcA * opacity / 255.
    /// AVX2 path processes 8 BGRA pixels per iteration via PMULHUW.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BlendTilePremultiplied(
        byte* dst, byte* src, int pixelCount, uint opacity = 255)
    {
        if (Avx2.IsSupported && pixelCount >= 8)
            BlendTilePremultipliedAvx2(dst, src, pixelCount, opacity);
        else
            BlendTilePremultipliedScalar(dst, src, pixelCount, opacity);
    }

    // ── Scalar helpers ──────────────────────────────────────────────────────

    private static void SrcOverRowFull(byte* dst, byte* src, int width)
    {
        if (Avx2.IsSupported && width >= 8)
            SrcOverRowFullAvx2(dst, src, width);
        else
            SrcOverRowFullScalar(dst, src, width);
    }

    private static void SrcOverRowPartial(byte* dst, byte* src, int width, int opacity)
    {
        for (int i = 0; i < width; i++)
        {
            int off = i * 4;
            byte ra = src[off + 3];
            if (ra == 0) continue;
            int sa = (ra * opacity + 127) / 255;
            if (sa == 0) continue;

            byte da = dst[off + 3];
            if (sa >= 255 || da == 0) { dst[off] = src[off]; dst[off + 1] = src[off + 1]; dst[off + 2] = src[off + 2]; dst[off + 3] = (byte)sa; continue; }

            int inv = 255 - sa;
            int dstCont = (da * inv + 127) / 255;
            int outA = sa + dstCont;
            if (outA == 0) continue;

            int half = outA >> 1;
            dst[off] = (byte)((src[off] * sa + dst[off] * dstCont + half) / outA);
            dst[off + 1] = (byte)((src[off + 1] * sa + dst[off + 1] * dstCont + half) / outA);
            dst[off + 2] = (byte)((src[off + 2] * sa + dst[off + 2] * dstCont + half) / outA);
            dst[off + 3] = (byte)outA;
        }
    }

    private static void SrcOverRowFullScalar(byte* dst, byte* src, int width)
    {
        for (int i = 0; i < width; i++)
        {
            int off = i * 4;
            byte sa = src[off + 3];
            if (sa == 0) continue;
            byte da = dst[off + 3];
            if (sa == 255 || da == 0) { Unsafe.WriteUnaligned(dst + off, Unsafe.ReadUnaligned<uint>(src + off)); dst[off + 3] = sa; continue; }

            int inv = 255 - sa;
            int dstCont = (da * inv + 127) / 255;
            int outA = sa + dstCont;
            if (outA == 0) continue;

            int half = outA >> 1;
            dst[off] = (byte)((src[off] * sa + dst[off] * dstCont + half) / outA);
            dst[off + 1] = (byte)((src[off + 1] * sa + dst[off + 1] * dstCont + half) / outA);
            dst[off + 2] = (byte)((src[off + 2] * sa + dst[off + 2] * dstCont + half) / outA);
            dst[off + 3] = (byte)outA;
        }
    }

    private static void BlendTilePremultipliedScalar(
        byte* dst, byte* src, int pixelCount, uint opacity)
    {
        bool fullOpac = opacity == 255;
        for (int i = 0; i < pixelCount; i++)
        {
            int off = i * 4;
            int sb = src[off], sg = src[off + 1], sr = src[off + 2], sa = src[off + 3];
            if (sa == 0) continue;

            int srcAO = fullOpac ? sa : (int)((uint)(sa * opacity + 127u) / 255u);
            if (srcAO <= 0) continue;
            if (srcAO > 255) srcAO = 255;

            int as1 = 255 - srcAO;
            dst[off] = (byte)((sb * srcAO + dst[off] * as1 + 127) / 255);
            dst[off + 1] = (byte)((sg * srcAO + dst[off + 1] * as1 + 127) / 255);
            dst[off + 2] = (byte)((sr * srcAO + dst[off + 2] * as1 + 127) / 255);
            dst[off + 3] = (byte)((srcAO * 255 + dst[off + 3] * as1 + 127) / 255);
        }
    }

    internal static void StampSrcOverMaskedRowScalar(
        byte* dst, byte* mask, int count,
        int srcB, int srcG, int srcR, int opacity, bool alphaLocked)
    {
        for (int x = 0; x < count; x++)
        {
            int ma = mask[x];
            if (ma == 0) continue;
            int sa = (ma * opacity + 127) / 255;
            if (sa <= 0) continue;
            if (sa > 255) sa = 255;

            int off = x * 4;
            byte da = dst[off + 3];
            if (da == 0)
            {
                if (alphaLocked) continue;
                dst[off] = (byte)srcB; dst[off+1] = (byte)srcG; dst[off+2] = (byte)srcR; dst[off+3] = (byte)sa;
                continue;
            }

            if (alphaLocked)
            {
                int inv = 255 - sa;
                dst[off]   = (byte)((srcB * sa + dst[off]   * inv + 127) / 255);
                dst[off+1] = (byte)((srcG * sa + dst[off+1] * inv + 127) / 255);
                dst[off+2] = (byte)((srcR * sa + dst[off+2] * inv + 127) / 255);
                continue;
            }

            int pcB = (dst[off]   * da + 127) / 255;
            int pcG = (dst[off+1] * da + 127) / 255;
            int pcR = (dst[off+2] * da + 127) / 255;
            int invSa = 255 - sa;
            int outA = (da * invSa + 255 * sa + 127) / 255;
            if (outA <= 0) { dst[off] = dst[off+1] = dst[off+2] = dst[off+3] = 0; continue; }
            int outPcB = (pcB * invSa + srcB * sa + 127) / 255;
            int outPcG = (pcG * invSa + srcG * sa + 127) / 255;
            int outPcR = (pcR * invSa + srcR * sa + 127) / 255;
            uint rec = s_recip255[outA];
            dst[off]   = (byte)Math.Min(255, (outPcB * rec + 128) >> 8);
            dst[off+1] = (byte)Math.Min(255, (outPcG * rec + 128) >> 8);
            dst[off+2] = (byte)Math.Min(255, (outPcR * rec + 128) >> 8);
            dst[off+3] = (byte)outA;
        }
    }

    // ── AVX2 implementations ────────────────────────────────────────────────

    // Byte masks used across AVX2 methods — inline as constants.
    // Alpha byte positions (3,7,11,15,19,23,27,31) set to 0xFF.
    private static readonly Vector256<byte> s_alphaByteSet = Vector256.Create(
        (byte)0,0,0,255,0,0,0,255,0,0,0,255,0,0,0,255,
        0,0,0,255,0,0,0,255,0,0,0,255,0,0,0,255);
    // Color byte positions set to 0xFF, alpha positions 0x00.
    private static readonly Vector256<byte> s_colorByteMask = Vector256.Create(
        (byte)255,255,255,0,255,255,255,0,255,255,255,0,255,255,255,0,
        255,255,255,0,255,255,255,0,255,255,255,0,255,255,255,0);

    // Perform (a * b) >> 8 unsigned via PMULHUW(a, b<<8).
    // Requires a,b ∈ [0,255] (product fits in uint16 since 255*255=65025<65535).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> MulShift8(Vector256<ushort> a, Vector256<ushort> b)
        => Avx2.MultiplyHigh(a, Avx2.ShiftLeftLogical(b, 8));

    // Build per-pixel alpha broadcast vectors from 8 alpha values in a 128-bit register.
    // Input sa16 = [sa0,sa1,sa2,sa3,sa4,sa5,sa6,sa7] (Vector128<ushort>).
    // Output aLo matches dLo pixel order (0,1,4,5); aHi matches dHi order (2,3,6,7).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildAlphaBroadcast(Vector128<ushort> sa16,
        out Vector256<ushort> aLo, out Vector256<ushort> aHi)
    {
        ushort sa0 = sa16.GetElement(0), sa1 = sa16.GetElement(1);
        ushort sa2 = sa16.GetElement(2), sa3 = sa16.GetElement(3);
        ushort sa4 = sa16.GetElement(4), sa5 = sa16.GetElement(5);
        ushort sa6 = sa16.GetElement(6), sa7 = sa16.GetElement(7);
        aLo = Vector256.Create(sa0,sa0,sa0,sa0, sa1,sa1,sa1,sa1,
                               sa4,sa4,sa4,sa4, sa5,sa5,sa5,sa5);
        aHi = Vector256.Create(sa2,sa2,sa2,sa2, sa3,sa3,sa3,sa3,
                               sa6,sa6,sa6,sa6, sa7,sa7,sa7,sa7);
    }

    // Core 8-pixel blend kernel (constant src, variable alpha per pixel):
    // result_ch = (srcCh * sa + dstCh * (255-sa)) >> 8
    // srcConst must be pre-broadcast as uint16 in BGRA layout with A=255.
    // aLo/aHi are per-pixel alpha broadcasts; dLo/dHi are widened dst pixels.
    // Does NOT write dst alpha; caller handles that.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> BlendConstSrcKernel(
        Vector256<ushort> srcConst,
        Vector256<ushort> dLo, Vector256<ushort> dHi,
        Vector256<ushort> aLo, Vector256<ushort> aHi)
    {
        var v255 = Vector256.Create((ushort)255);
        var i1Lo = Avx2.Subtract(v255, aLo);
        var i1Hi = Avx2.Subtract(v255, aHi);
        var rLo = Avx2.Add(MulShift8(srcConst, aLo),  MulShift8(dLo, i1Lo));
        var rHi = Avx2.Add(MulShift8(srcConst, aHi), MulShift8(dHi, i1Hi));
        return Avx2.PackUnsignedSaturate(rLo.AsInt16(), rHi.AsInt16());
    }

    // Core 8-pixel blend kernel (per-pixel src and alpha):
    // result_ch = (srcCh * sa + dstCh * (255-sa)) >> 8
    // src alpha positions should already be set to 255 (replace with Blend before call).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> BlendPerSrcKernel(
        Vector256<ushort> sLoMod, Vector256<ushort> sHiMod,
        Vector256<ushort> dLo,    Vector256<ushort> dHi,
        Vector256<ushort> aLo,    Vector256<ushort> aHi)
    {
        var v255 = Vector256.Create((ushort)255);
        var i1Lo = Avx2.Subtract(v255, aLo);
        var i1Hi = Avx2.Subtract(v255, aHi);
        var rLo = Avx2.Add(MulShift8(sLoMod, aLo), MulShift8(dLo, i1Lo));
        var rHi = Avx2.Add(MulShift8(sHiMod, aHi), MulShift8(dHi, i1Hi));
        return Avx2.PackUnsignedSaturate(rLo.AsInt16(), rHi.AsInt16());
    }

    // Returns true if all 8 dst pixels have alpha == 255 (opaque).
    // Uses CompareEqual+MoveMask: alpha bytes are at positions 3,7,11,...,31.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllDstOpaque(Vector256<byte> dB)
    {
        var eq = Avx2.CompareEqual(dB, Vector256.Create((byte)255));
        // Bit positions 3,7,11,15,19,23,27,31 → mask 0x88888888
        return (Avx2.MoveMask(eq) & unchecked((int)0x88888888u)) == unchecked((int)0x88888888u);
    }

    private static unsafe void StampSrcOverMaskedRowAvx2(
        byte* dst, byte* mask, int count,
        int srcB, int srcG, int srcR, int opacity, bool alphaLocked)
    {
        // Caller only routes alpha-lock rows here; normal src-over uses scalar associated-alpha.
        if (!alphaLocked) { StampSrcOverMaskedRowScalar(dst, mask, count, srcB, srcG, srcR, opacity, false); return; }

        var srcConst = Vector256.Create(
            (ushort)srcB,(ushort)srcG,(ushort)srcR,(ushort)255,
            (ushort)srcB,(ushort)srcG,(ushort)srcR,(ushort)255,
            (ushort)srcB,(ushort)srcG,(ushort)srcR,(ushort)255,
            (ushort)srcB,(ushort)srcG,(ushort)srcR,(ushort)255);

        var opacityV = Vector128.Create((short)opacity);
        int i = 0;
        int simdEnd = count & ~7;

        for (; i < simdEnd; i += 8)
        {
            // Load 8 mask bytes → uint16, apply opacity: sa = (mask * opacity) >> 8
            var maskVec = Sse2.LoadScalarVector128((long*)(mask + i)).AsByte();
            var sa16 = Sse2.ShiftRightLogical(
                Sse2.MultiplyLow(Sse41.ConvertToVector128Int16(maskVec).AsUInt16().AsInt16(),
                                 opacityV).AsUInt16(), 8);
            if (Sse41.TestZ(sa16, sa16)) continue; // all transparent

            var dB = Avx2.LoadVector256(dst + i * 4);

            if (!AllDstOpaque(dB))
            {
                StampSrcOverMaskedRowScalar(dst + i * 4, mask + i, Math.Min(8, count - i),
                    srcB, srcG, srcR, opacity, alphaLocked: true);
                continue;
            }

            BuildAlphaBroadcast(sa16, out var aLo, out var aHi);
            var dLo = Avx2.UnpackLow(dB,  Vector256<byte>.Zero).AsUInt16();
            var dHi = Avx2.UnpackHigh(dB, Vector256<byte>.Zero).AsUInt16();
            var packed = BlendConstSrcKernel(srcConst, dLo, dHi, aLo, aHi);

            // Alpha-lock: blend rgb only, preserve dst alpha.
            packed = Avx2.Or(Avx2.And(packed, s_colorByteMask), Avx2.And(dB, s_alphaByteSet));

            Avx2.Store(dst + i * 4, packed);
        }

        // Scalar tail
        for (; i < count; i++)
        {
            int ma = mask[i];
            if (ma == 0) continue;
            int sa = (ma * opacity + 127) / 255;
            if (sa <= 0) continue;
            if (sa > 255) sa = 255;
            int off = i * 4;
            if (dst[off + 3] == 0) continue; // alpha lock: never expand coverage
            int inv = 255 - sa;
            dst[off]   = (byte)((srcB * sa + dst[off]   * inv + 127) / 255);
            dst[off+1] = (byte)((srcG * sa + dst[off+1] * inv + 127) / 255);
            dst[off+2] = (byte)((srcR * sa + dst[off+2] * inv + 127) / 255);
        }
    }

    // SrcOver for full-opacity blending.
    // AVX2 fast path: all-opaque dst → (src*sa + dst*(255-sa)) >> 8, no division.
    private static unsafe void SrcOverRowFullAvx2(byte* dst, byte* src, int width)
    {
        var v255u16 = Vector256.Create((ushort)255);

        int i = 0;
        int simdEnd = width & ~7;

        for (; i < simdEnd; i += 8)
        {
            var sB = Avx2.LoadVector256(src + i * 4);
            var dB = Avx2.LoadVector256(dst + i * 4);

            // Skip if all src alpha == 0
            var eqZeroAlpha = Avx2.CompareEqual(Avx2.And(sB, s_alphaByteSet), Vector256<byte>.Zero);
            if ((Avx2.MoveMask(eqZeroAlpha) & unchecked((int)0x88888888u)) == unchecked((int)0x88888888u))
                continue;

            if (!AllDstOpaque(dB))
            {
                SrcOverRowFullScalar(dst + i * 4, src + i * 4, 8);
                continue;
            }

            var sLo = Avx2.UnpackLow(sB,  Vector256<byte>.Zero).AsUInt16();
            var dLo = Avx2.UnpackLow(dB,  Vector256<byte>.Zero).AsUInt16();
            var sHi = Avx2.UnpackHigh(sB, Vector256<byte>.Zero).AsUInt16();
            var dHi = Avx2.UnpackHigh(dB, Vector256<byte>.Zero).AsUInt16();

            // Extract per-pixel src alpha
            var sa128 = Vector128.Create(
                sLo.GetElement(3),  sLo.GetElement(7),
                sHi.GetElement(3),  sHi.GetElement(7),
                sLo.GetElement(11), sLo.GetElement(15),
                sHi.GetElement(11), sHi.GetElement(15));
            BuildAlphaBroadcast(sa128, out var aLo, out var aHi);

            // Replace src alpha with 255: out_A = (255*sa + dstA*(255-sa)) >> 8, then force to 255
            var sLoMod = Avx2.Blend(sLo.AsInt16(), v255u16.AsInt16(), 0x88).AsUInt16();
            var sHiMod = Avx2.Blend(sHi.AsInt16(), v255u16.AsInt16(), 0x88).AsUInt16();

            var packed = BlendPerSrcKernel(sLoMod, sHiMod, dLo, dHi, aLo, aHi);
            packed = Avx2.Or(packed, s_alphaByteSet); // dst was opaque → stays opaque
            Avx2.Store(dst + i * 4, packed);
        }

        if (i < width)
            SrcOverRowFullScalar(dst + i * 4, src + i * 4, width - i);
    }

    /// <summary>
    /// -style premultiplied tile blend.
    /// All channels (including alpha): out = (src * srcAO + dst * (255-srcAO)) >> 8
    /// where srcAO = srcA (opacity=255) or srcA*opacity/255.
    /// AVX2: 8 pixels per iteration via PMULHUW(a, b&lt;&lt;8) = (a*b)>>8.
    /// </summary>
    private static void BlendTilePremultipliedAvx2(
        byte* dst, byte* src, int pixelCount, uint opacity)
    {
        bool fullOpac = opacity == 255;
        var v255 = Vector256.Create((ushort)255);

        int i = 0;
        int simdEnd = pixelCount & ~7;

        for (; i < simdEnd; i += 8)
        {
            var sB = Avx2.LoadVector256(src + i * 4);
            var dB = Avx2.LoadVector256(dst + i * 4);

            // Widen bytes → uint16, preserving AVX2 lane layout:
            // sLo: [B0 G0 R0 A0 B1 G1 R1 A1 | B4 G4 R4 A4 B5 G5 R5 A5]
            // sHi: [B2 G2 R2 A2 B3 G3 R3 A3 | B6 G6 R6 A6 B7 G7 R7 A7]
            var sLo = Avx2.UnpackLow(sB,  Vector256<byte>.Zero).AsUInt16();
            var dLo = Avx2.UnpackLow(dB,  Vector256<byte>.Zero).AsUInt16();
            var sHi = Avx2.UnpackHigh(sB, Vector256<byte>.Zero).AsUInt16();
            var dHi = Avx2.UnpackHigh(dB, Vector256<byte>.Zero).AsUInt16();

            // Extract alpha from each of the 8 pixels.
            // sLo has pixels {0,1} in low 128-bit lane and {4,5} in high lane.
            // sHi has pixels {2,3} in low lane and {6,7} in high lane.
            ushort a0 = sLo.GetElement(3),  a1 = sLo.GetElement(7);
            ushort a4 = sLo.GetElement(11), a5 = sLo.GetElement(15);
            ushort a2 = sHi.GetElement(3),  a3 = sHi.GetElement(7);
            ushort a6 = sHi.GetElement(11), a7 = sHi.GetElement(15);

            if (!fullOpac)
            {
                a0 = (ushort)(a0 * opacity >> 8); a1 = (ushort)(a1 * opacity >> 8);
                a2 = (ushort)(a2 * opacity >> 8); a3 = (ushort)(a3 * opacity >> 8);
                a4 = (ushort)(a4 * opacity >> 8); a5 = (ushort)(a5 * opacity >> 8);
                a6 = (ushort)(a6 * opacity >> 8); a7 = (ushort)(a7 * opacity >> 8);
            }

            // Alpha broadcast vectors matching sLo/sHi pixel layouts
            var aLo = Vector256.Create(a0,a0,a0,a0, a1,a1,a1,a1, a4,a4,a4,a4, a5,a5,a5,a5);
            var aHi = Vector256.Create(a2,a2,a2,a2, a3,a3,a3,a3, a6,a6,a6,a6, a7,a7,a7,a7);
            var i1Lo = Avx2.Subtract(v255, aLo);
            var i1Hi = Avx2.Subtract(v255, aHi);

            // Replace src alpha channel with 255 so the alpha blend gives:
            // out_A = (255*srcAO + dstA*(255-srcAO)) >> 8
            // instead of (srcA*srcAO + dstA*(255-srcAO)) >> 8 which would double-apply alpha.
            var sLoMod = Avx2.Blend(sLo.AsInt16(), v255.AsInt16(), 0x88).AsUInt16();
            var sHiMod = Avx2.Blend(sHi.AsInt16(), v255.AsInt16(), 0x88).AsUInt16();

            // Blend: out = (src * srcAO + dst * (255-srcAO)) >> 8
            // PMULHUW(a, b<<8) = (a*b*256)>>16 = (a*b)>>8 (unsigned, values in [0,255])
            var rLo = Avx2.Add(MulShift8(sLoMod, aLo), MulShift8(dLo, i1Lo));
            var rHi = Avx2.Add(MulShift8(sHiMod, aHi), MulShift8(dHi, i1Hi));

            // Pack uint16→uint8. VPACKUSWB in each 128-bit lane:
            // lo128 = [pixels 0,1, pixels 2,3]
            // hi128 = [pixels 4,5, pixels 6,7]
            Avx2.Store(dst + i * 4, Avx2.PackUnsignedSaturate(rLo.AsInt16(), rHi.AsInt16()));
        }

        if (i < pixelCount)
            BlendTilePremultipliedScalar(dst + i * 4, src + i * 4, pixelCount - i, opacity);
    }
}
