// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dsp/enc.c, the inverse WHT of src/dsp/dec.c, and src/dsp/quant.h),
// modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Word(
        int b0,
        int b1,
        int b2,
        int b3)
    {
        return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rows(
        ref byte p,
        uint row0,
        uint row1,
        uint row2,
        uint row3)
    {
        Unsafe.WriteUnaligned(ref p, row0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Bps), row1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 2 * Bps), row2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 3 * Bps), row3);
    }

    // GetSSE
    private static int Sse(
        ReadOnlySpan<byte> a,
        int ai,
        ReadOnlySpan<byte> b,
        int bi,
        int w,
        int h)
    {
        if (SimdKernels)
        {
            ref byte ra = ref Unsafe.Add(ref MemoryMarshal.GetReference(a), ai);
            ref byte rb = ref Unsafe.Add(ref MemoryMarshal.GetReference(b), bi);
            return (w == 16) ? Sse16Simd(ref ra, ref rb, h) : Sse4x4Simd(ref ra, ref rb);
        }

        int count = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int diff = a[ai + x] - b[bi + x];
                count += diff * diff;
            }

            ai += Bps;
            bi += Bps;
        }

        return count;
    }

    // Mean16x4_C: the sums of four 4x4 blocks across
    private static void Mean16x4(
        ReadOnlySpan<byte> reference,
        int r,
        Span<uint> dc)
    {
        for (int k = 0; k < 4; k++)
        {
            uint average = 0;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    average += reference[r + x + (y * Bps)];
                }
            }

            dc[k] = average;
            r += 4;
        }
    }

    // TTransform: the Hadamard transform's weighted absolute sum
    private static int TTransform(
        ReadOnlySpan<byte> input,
        int at,
        ReadOnlySpan<ushort> w)
    {
        int sum = 0;
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++, at += Bps)
        {
            int a0 = input[at + 0] + input[at + 2];
            int a1 = input[at + 1] + input[at + 3];
            int a2 = input[at + 1] - input[at + 3];
            int a3 = input[at + 0] - input[at + 2];
            tmp[0 + (i * 4)] = a0 + a1;
            tmp[1 + (i * 4)] = a3 + a2;
            tmp[2 + (i * 4)] = a3 - a2;
            tmp[3 + (i * 4)] = a0 - a1;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[0 + i] + tmp[8 + i];
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[0 + i] - tmp[8 + i];
            int b0 = a0 + a1;
            int b1 = a3 + a2;
            int b2 = a3 - a2;
            int b3 = a0 - a1;
            sum += w[0 + i] * Math.Abs(b0);
            sum += w[4 + i] * Math.Abs(b1);
            sum += w[8 + i] * Math.Abs(b2);
            sum += w[12 + i] * Math.Abs(b3);
        }

        return sum;
    }

    private static int Disto4x4(
        ReadOnlySpan<byte> a,
        int ai,
        ReadOnlySpan<byte> b,
        int bi,
        ReadOnlySpan<ushort> w)
    {
        if (SimdKernels)
        {
            // (the SIMD form needs symmetric weights, as kWeightY is)
            return Disto4x4Simd(ref Unsafe.Add(ref MemoryMarshal.GetReference(a), ai), ref Unsafe.Add(ref MemoryMarshal.GetReference(b), bi), w);
        }

        int sum1 = TTransform(a, ai, w);
        int sum2 = TTransform(b, bi, w);
        return Math.Abs(sum2 - sum1) >> 5;
    }

    private static int Disto16x16(
        ReadOnlySpan<byte> a,
        int ai,
        ReadOnlySpan<byte> b,
        int bi,
        ReadOnlySpan<ushort> w)
    {
        int d = 0;
        for (int y = 0; y < 16 * Bps; y += 4 * Bps)
        {
            for (int x = 0; x < 16; x += 4)
            {
                d += Disto4x4(a, ai + x + y, b, bi + x + y, w);
            }
        }

        return d;
    }

    // QuantizeBlock_C: in (the coefficients) quantized to out (the levels, zigzagged), and in set to
    // the dequantized values; whether any level isn't zero
    private static bool QuantizeBlock(
        Span<short> input,
        Span<short> output,
        Matrix mtx)
    {
        if (SimdKernels)
        {
            return QuantizeBlockSimd(ref MemoryMarshal.GetReference(input), ref MemoryMarshal.GetReference(output), mtx);
        }

        int last = -1;
        var zigzag = Zigzag;
        for (int n = 0; n < 16; n++)
        {
            int j = zigzag[n];
            bool sign = input[j] < 0;
            uint coeff = (uint)(sign ? -input[j] : input[j]) + mtx.Sharpen[j];
            if (coeff > mtx.ZThresh[j])
            {
                uint q = mtx.Q[j];
                uint iq = mtx.Iq[j];
                uint bias = mtx.Bias[j];
                int level = QuantDiv(coeff, iq, bias);
                if (level > MaxLevel)
                {
                    level = MaxLevel;
                }

                if (sign)
                {
                    level = -level;
                }

                input[j] = (short)(level * (int)q);
                output[n] = (short)level;
                if (level != 0)
                {
                    last = n;
                }
            }
            else
            {
                output[n] = 0;
                input[j] = 0;
            }
        }

        return last >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int QuantDiv(
        uint n,
        uint iq,
        uint bias)
    {
        return (int)(((n * iq) + bias) >> QFix);
    }

    private static int Quantize2Blocks(
        Span<short> input,
        Span<short> output,
        Matrix mtx)
    {
        if (SimdKernels)
        {
            ref short i = ref MemoryMarshal.GetReference(input);
            ref short o = ref MemoryMarshal.GetReference(output);
            return (QuantizeBlockSimd(ref i, ref o, mtx) ? 1 : 0) | ((QuantizeBlockSimd(ref Unsafe.Add(ref i, 16), ref Unsafe.Add(ref o, 16), mtx) ? 1 : 0) << 1);
        }

        int nz = QuantizeBlock(input.Slice(0, 16), output.Slice(0, 16), mtx) ? 1 : 0;
        nz |= (QuantizeBlock(input.Slice(16, 16), output.Slice(16, 16), mtx) ? 1 : 0) << 1;
        return nz;
    }

    // CollectHistogram_C
    private static void CollectHistogram(
        ReadOnlySpan<byte> reference,
        int r,
        ReadOnlySpan<byte> pred,
        int p,
        int startBlock,
        int endBlock,
        out int maxValue,
        out int lastNonZero)
    {
        Span<int> distribution = stackalloc int[MaxCoeffThresh + 1];
        distribution.Clear();
        Span<short> output = stackalloc short[32];
        var scan = DspScan;

        // (the blocks go in pairs side by side: luma 0 to 16, chroma 16 to 24)
        ref int dist = ref MemoryMarshal.GetReference(distribution);
        ref short outRef = ref MemoryMarshal.GetReference(output);
        for (int j = startBlock; j < endBlock; j += 2)
        {
            FTransform2(reference, r + scan[j], pred, p + scan[j], output);
            if (Vector128.IsHardwareAccelerated)
            {
                // The bins, min(|v| >> 3, 31), in place
                var max = Vector128.Create((short)MaxCoeffThresh);
                for (int k = 0; k < 32; k += 8)
                {
                    Vector128.Min(Vector128.Abs(Vector128.LoadUnsafe(ref outRef, (nuint)k)) >> 3, max).StoreUnsafe(ref outRef, (nuint)k);
                }

                for (int k = 0; k < 32; k++)
                {
                    Unsafe.Add(ref dist, Unsafe.Add(ref outRef, k))++;
                }
            }
            else
            {
                for (int k = 0; k < 32; k++)
                {
                    int v = output[k];
                    v = ((v ^ (v >> 31)) - (v >> 31)) >> 3;
                    distribution[(v > MaxCoeffThresh) ? MaxCoeffThresh : v]++;
                }
            }
        }

        // VP8SetHistogramData
        maxValue = 0;
        lastNonZero = 1;
        for (int k = 0; k <= MaxCoeffThresh; k++)
        {
            int value = distribution[k];
            if (value > 0)
            {
                if (value > maxValue)
                {
                    maxValue = value;
                }

                lastNonZero = k;
            }
        }
    }

    // IsFlat_C: whether the blocks' AC levels that aren't zero are at most thresh
    private static bool IsFlat(
        ReadOnlySpan<short> levels,
        int numBlocks,
        int thresh)
    {
        if (SimdKernels)
        {
            return IsFlatSimd(ref MemoryMarshal.GetReference(levels), numBlocks, thresh);
        }

        int score = 0;
        int at = 0;
        while (numBlocks-- > 0)
        {
            for (int i = 1; i < 16; i++)
            {
                score += (levels[at + i] != 0) ? 1 : 0;
                if (score > thresh)
                {
                    return false;
                }
            }

            at += 16;
        }

        return true;
    }

    // IsFlatSource16: whether all 16x16 samples are the same
    private static bool IsFlatSource16(
        ReadOnlySpan<byte> src,
        int at)
    {
        byte v = src[at];
        for (int i = 0; i < 16; i++)
        {
            if (src.Slice(at + (i * Bps), 16).IndexOfAnyExcept(v) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
