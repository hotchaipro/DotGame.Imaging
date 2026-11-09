// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dsp/lossless_enc.c, lossless.c, lossless_common.h and their NEON and
// SSE2 versions), modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2015 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private const int Log2PrecisionBits = 23;
    private const ulong Log2ReciprocalFixed = 12102203;
    private const double Log2ReciprocalFixedDouble = 12102203.161561485379934310913085937500;
    private const int ApproxLogWithCorrectionMax = 65536;
    private const int ApproxLogMax = 4096;
    private const uint ArgbBlack = 0xff000000;

    // log2(v) in fixed point (VP8LFastLog2)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FastLog2(
        uint v)
    {
        return (v < 256) ? Log2Table[(int)v] : FastLog2Slow(v);
    }

    // v x log2(v) in fixed point (VP8LFastSLog2)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FastSLog2(
        uint v)
    {
        return (v < 256) ? SLog2Table[(int)v] : FastSLog2Slow(v);
    }

    // FastSLog2 of a value below 65536, inlined with no calls, so a caller's loop keeps its registers
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FastSLog2Small(
        uint v)
    {
        if (v < 256)
        {
            return Unsafe.Add(ref MemoryMarshal.GetReference(SLog2Table), (nint)v);
        }

        int logCount = BitOperations.Log2(v) - 7;
        uint y = 1u << logCount;
        ulong correction = Log2ReciprocalFixed * (v & (y - 1));
        return ((ulong)v * (Unsafe.Add(ref MemoryMarshal.GetReference(Log2Table), (nint)(v >> logCount)) + ((ulong)logCount << Log2PrecisionBits))) + correction;
    }

    [ThreadStatic]
    private static ulong[]? largeRuns;

    private static ulong FastSLog2Slow(
        uint v)
    {
        if (v < ApproxLogWithCorrectionMax)
        {
            ulong origV = v;
            int logCount = BitOperations.Log2(v) - 7;
            uint y = 1u << logCount;
            v >>= logCount;
            ulong correction = Log2ReciprocalFixed * (origV & (y - 1));
            return (origV * (Log2Table[(int)v] + ((ulong)logCount << Log2PrecisionBits))) + correction;
        }

        return (ulong)((Log2ReciprocalFixedDouble * v * Math.Log(v)) + .5);
    }

    private static uint FastLog2Slow(
        uint v)
    {
        if (v < ApproxLogWithCorrectionMax)
        {
            uint origV = v;
            int logCount = BitOperations.Log2(v) - 7;
            uint y = 1u << logCount;
            v >>= logCount;
            uint log2 = Log2Table[(int)v] + ((uint)logCount << Log2PrecisionBits);
            if (origV >= ApproxLogMax)
            {
                ulong correction = Log2ReciprocalFixed * (origV & (y - 1));
                log2 += (uint)DivRound((long)correction, origV);
            }

            return log2;
        }

        return (uint)((Log2ReciprocalFixedDouble * Math.Log(v)) + .5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DivRound(
        long a,
        long b)
    {
        return ((a < 0) == (b < 0)) ? ((a + (b / 2)) / b) : ((a - (b / 2)) / b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SubSampleSize(
        int size,
        int samplingBits)
    {
        return (int)(((uint)size + (1u << samplingBits) - 1) >> samplingBits);
    }

    private static int NearLosslessBits(
        int nearLosslessQuality)
    {
        return 5 - (nearLosslessQuality / 20);
    }

    // The prefix code and extra bits of a length or distance (VP8LPrefixEncodeBits)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefixEncodeBits(
        int distance,
        out int code,
        out int extraBits)
    {
        if (distance <= 2)
        {
            // 0 and 1 have code 0, 2 code 1, with no extra bits (libwebp's table)
            code = (distance == 2) ? 1 : 0;
            extraBits = 0;
            return;
        }

        int value = distance - 1;
        int highestBit = BitOperations.Log2((uint)value);
        int secondHighestBit = (value >> (highestBit - 1)) & 1;
        extraBits = highestBit - 1;
        code = (2 * highestBit) + secondHighestBit;
    }

    // The prefix code, extra bits and their value of a length or distance (VP8LPrefixEncode)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefixEncode(
        int distance,
        out int code,
        out int extraBits,
        out int extraBitsValue)
    {
        if (distance <= 2)
        {
            code = (distance == 2) ? 1 : 0;
            extraBits = 0;
            extraBitsValue = 0;
            return;
        }

        int value = distance - 1;
        int highestBit = BitOperations.Log2((uint)value);
        int secondHighestBit = (value >> (highestBit - 1)) & 1;
        extraBits = highestBit - 1;
        extraBitsValue = value & ((1 << extraBits) - 1);
        code = (2 * highestBit) + secondHighestBit;
    }

    // Each component's sum, mod 256
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddPixels(
        uint a,
        uint b)
    {
        uint alphaAndGreen = (a & 0xff00ff00u) + (b & 0xff00ff00u);
        uint redAndBlue = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
        return (alphaAndGreen & 0xff00ff00u) | (redAndBlue & 0x00ff00ffu);
    }

    // Each component's difference, mod 256
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SubPixels(
        uint a,
        uint b)
    {
        uint alphaAndGreen = 0x00ff00ffu + (a & 0xff00ff00u) - (b & 0xff00ff00u);
        uint redAndBlue = 0xff00ff00u + (a & 0x00ff00ffu) - (b & 0x00ff00ffu);
        return (alphaAndGreen & 0xff00ff00u) | (redAndBlue & 0x00ff00ffu);
    }

    // The color cache's hash (VP8LHashPix)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashPix(
        uint argb,
        int shift)
    {
        return (int)((argb * 0x1e35a7bdu) >> shift);
    }

    //------------------------------------------------------------------------------
    // Entropy

    private struct BitEntropy
    {
        public ulong Entropy;
        public uint Sum;
        public int Nonzeros;
        public uint MaxValue;
        public uint NonzeroCode;
    }

    private struct Streaks
    {
        public int Counts0;
        public int Counts1;
        public int Streaks00;
        public int Streaks01;
        public int Streaks10;
        public int Streaks11;
    }

    // The combined Shannon entropy of X and X + Y (CombinedShannonEntropy)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong CombinedShannonEntropy(
        ReadOnlySpan<uint> x,
        ReadOnlySpan<uint> y)
    {
        // Σ S(x) + Σ S(x + y): an x of 0 adds nothing, as S(0) is 0
        ulong result = 0;
        uint sumX = 0, sumXY = 0;
        ref uint xr = ref MemoryMarshal.GetReference(x.Slice(0, 256));
        ref uint yr = ref MemoryMarshal.GetReference(y.Slice(0, 256));
        if (Vector128.IsHardwareAccelerated)
        {
            // The sums 4 at a time, and the logs only for groups with a nonzero x + y, as most of
            // a residual histogram is zeros
            var vectorX = Vector128<uint>.Zero;
            var vectorXY = Vector128<uint>.Zero;
            for (nint i = 0; i < 256; i += 4)
            {
                var vx = Vector128.LoadUnsafe(ref xr, (nuint)i);
                var vxy = vx + Vector128.LoadUnsafe(ref yr, (nuint)i);
                vectorX += vx;
                vectorXY += vxy;
                if (vxy == Vector128<uint>.Zero)
                {
                    continue;
                }

                for (nint k = i; k < i + 4; k++)
                {
                    uint xi = Unsafe.Add(ref xr, k);
                    uint xy = xi + Unsafe.Add(ref yr, k);
                    if (xy == 0)
                    {
                        continue;
                    }

                    if (xy < ApproxLogWithCorrectionMax)
                    {
                        result += FastSLog2Small(xi) + FastSLog2Small(xy);
                    }
                    else
                    {
                        result += FastSLog2(xi) + FastSLog2Slow(xy);
                    }
                }
            }

            sumX = Vector128.Sum(vectorX);
            sumXY = Vector128.Sum(vectorXY);
            return FastSLog2(sumX) + FastSLog2(sumXY) - result;
        }

        for (nint i = 0; i < 256; i++)
        {
            uint xi = Unsafe.Add(ref xr, i);
            uint xy = xi + Unsafe.Add(ref yr, i);
            sumX += xi;
            sumXY += xy;
            if (xy < ApproxLogWithCorrectionMax)
            {
                // xi <= xy: both inline, with no calls in the loop (S(0) is 0 in the table)
                result += FastSLog2Small(xi) + FastSLog2Small(xy);
            }
            else
            {
                result += FastSLog2(xi) + FastSLog2Slow(xy);
            }
        }

        return FastSLog2(sumX) + FastSLog2(sumXY) - result;
    }

    private static ulong ShannonEntropy(
        ReadOnlySpan<uint> x)
    {
        ulong result = 0;
        uint sumX = 0;
        for (int i = 0; i < x.Length; i++)
        {
            uint xi = x[i];
            if (xi != 0)
            {
                sumX += xi;
                result += FastSLog2(xi);
            }
        }

        return FastSLog2(sumX) - result;
    }

    private static void BitsEntropyUnrefined(
        ReadOnlySpan<uint> array,
        out BitEntropy entropy)
    {
        entropy = default;
        entropy.NonzeroCode = NonTrivialSymbol;
        for (int i = 0; i < array.Length; i++)
        {
            uint value = array[i];
            if (value != 0)
            {
                entropy.Sum += value;
                entropy.NonzeroCode = (uint)i;
                entropy.Nonzeros++;
                entropy.Entropy += FastSLog2(value);
                if (entropy.MaxValue < value)
                {
                    entropy.MaxValue = value;
                }
            }
        }

        entropy.Entropy = FastSLog2(entropy.Sum) - entropy.Entropy;
    }

    // GetEntropyUnrefined
    private static void GetEntropyUnrefined(
        ReadOnlySpan<uint> x,
        out BitEntropy bitEntropy,
        out Streaks stats)
    {
        GetCombinedEntropyUnrefined<PopulationX>(x, x, out bitEntropy, out stats);
    }

    // GetCombinedEntropyUnrefined
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetCombinedEntropyUnrefined(
        ReadOnlySpan<uint> x,
        ReadOnlySpan<uint> y,
        out BitEntropy bitEntropy,
        out Streaks stats)
    {
        GetCombinedEntropyUnrefined<PopulationXY>(x, y.Slice(0, x.Length), out bitEntropy, out stats);
    }

    // The values the entropy is of: x, or x + y
    private interface IPopulation
    {
        static abstract uint At(
            ref uint x,
            ref uint y,
            nint i);
    }

    private struct PopulationX : IPopulation
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint At(
            ref uint x,
            ref uint y,
            nint i) => Unsafe.Add(ref x, i);
    }

    private struct PopulationXY : IPopulation
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint At(
            ref uint x,
            ref uint y,
            nint i) => Unsafe.Add(ref x, i) + Unsafe.Add(ref y, i);
    }

    // The runs of equal values: the bit entropy of the values, and the Huffman cost's streaks (runs
    // longer than 3 apart from the others, of zeros apart from other values). Everything is in
    // locals, written out once.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void GetCombinedEntropyUnrefined<T>(
        ReadOnlySpan<uint> x,
        ReadOnlySpan<uint> y,
        out BitEntropy bitEntropy,
        out Streaks stats)
        where T : struct, IPopulation
    {
        ulong entropy = 0;
        uint sum = 0;
        uint maxValue = 0;
        int nonzeroCode = NonTrivialSymbol;

        // The runs' statistics, of zeros and of other values, packed to keep the loop in registers:
        // bits 0 to 15 sum the lengths of runs of 3 or fewer, 16 to 31 those of longer runs, and 32 to
        // 47 count the longer runs (a population is at most 1304 long)
        ulong zeroRuns = 0, nonzeroRuns = 0;
        int length = x.Length;
        ref uint xr = ref MemoryMarshal.GetReference(x);
        ref uint yr = ref MemoryMarshal.GetReference(y);

        // Runs of values of 65536 or more, whose x log2(x) takes a call to log: done after the loop,
        // so it has no calls and keeps its values in registers (the sum is the same in any order)
        var large = largeRuns ??= new ulong[1305];
        ref ulong largeRef = ref MemoryMarshal.GetArrayDataReference(large);
        int largeCount = 0;
        uint previous = T.At(ref xr, ref yr, 0);
        int iPrevious = 0;

        // libwebp's loop: each value against the last, a run finished when it changes (scanning runs
        // with vectors is slower on real populations, whose runs are mostly short)
        for (int i = 1; ; i++)
        {
            uint value = 0;
            if (i < length)
            {
                value = T.At(ref xr, ref yr, i);
                if (value == previous)
                {
                    continue;
                }
            }

            // The run of 'previous' from iPrevious to i
            int streak = i - iPrevious;
            int isLong = (streak > 3) ? 1 : 0;
            ulong packed = ((ulong)(uint)streak << (isLong * 16)) + ((ulong)(uint)isLong << 32);
            if (previous != 0)
            {
                sum += previous * (uint)streak;
                nonzeroCode = iPrevious;
                if (previous < ApproxLogWithCorrectionMax)
                {
                    entropy += FastSLog2Small(previous) * (ulong)(uint)streak;
                }
                else
                {
                    Unsafe.Add(ref largeRef, largeCount++) = ((ulong)previous << 32) | (uint)streak;
                }

                maxValue = Math.Max(maxValue, previous);
                nonzeroRuns += packed;
            }
            else
            {
                zeroRuns += packed;
            }

            if (i >= length)
            {
                break;
            }

            previous = value;
            iPrevious = i;
        }

        for (int k = 0; k < largeCount; k++)
        {
            ulong run = large[k];
            entropy += FastSLog2Slow((uint)(run >> 32)) * (run & 0xffffffff);
        }

        stats.Streaks00 = (int)(zeroRuns & 0xffff);
        stats.Streaks01 = (int)((zeroRuns >> 16) & 0xffff);
        stats.Counts0 = (int)(zeroRuns >> 32);
        stats.Streaks10 = (int)(nonzeroRuns & 0xffff);
        stats.Streaks11 = (int)((nonzeroRuns >> 16) & 0xffff);
        stats.Counts1 = (int)(nonzeroRuns >> 32);
        bitEntropy.Entropy = FastSLog2(sum) - entropy;
        bitEntropy.Sum = sum;
        bitEntropy.Nonzeros = stats.Streaks10 + stats.Streaks11;
        bitEntropy.MaxValue = maxValue;
        bitEntropy.NonzeroCode = (uint)nonzeroCode;
    }

    private static uint ExtraCost(
        ReadOnlySpan<uint> population)
    {
        int length = population.Length;
        uint cost = population[4] + population[5];
        for (int i = 2; i < (length / 2) - 1; i++)
        {
            cost += (uint)i * (population[(2 * i) + 2] + population[(2 * i) + 3]);
        }

        return cost;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddVector(
        ReadOnlySpan<uint> a,
        ReadOnlySpan<uint> b,
        Span<uint> output)
    {
        int size = output.Length;
        a = a.Slice(0, size);
        b = b.Slice(0, size);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= size; i += 4)
            {
                (Vector128.Create(a.Slice(i, 4)) + Vector128.Create(b.Slice(i, 4))).CopyTo(output.Slice(i, 4));
            }
        }

        for (; i < size; i++)
        {
            output[i] = a[i] + b[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddVectorEq(
        ReadOnlySpan<uint> a,
        Span<uint> output)
    {
        int size = output.Length;
        a = a.Slice(0, size);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= size; i += 4)
            {
                (Vector128.Create(a.Slice(i, 4)) + Vector128.Create((ReadOnlySpan<uint>)output.Slice(i, 4))).CopyTo(output.Slice(i, 4));
            }
        }

        for (; i < size; i++)
        {
            output[i] += a[i];
        }
    }

    // The number of equal leading elements, up to length (VectorMismatch)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VectorMismatch(
        ReadOnlySpan<uint> array1,
        ReadOnlySpan<uint> array2,
        int length)
    {
        return array1.Slice(0, length).CommonPrefixLength(array2.Slice(0, length));
    }

    //------------------------------------------------------------------------------
    // Predictors (lossless.c)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average2(
        uint a0,
        uint a1)
    {
        return (((a0 ^ a1) & 0xfefefefeu) >> 1) + (a0 & a1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Clip255(
        uint a)
    {
        return (a < 256) ? a : (~a >> 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ClampedAddSubtractFull(
        uint c0,
        uint c1,
        uint c2)
    {
        uint a = Clip255((c0 >> 24) + (c1 >> 24) - (c2 >> 24));
        uint r = Clip255(((c0 >> 16) & 0xff) + ((c1 >> 16) & 0xff) - ((c2 >> 16) & 0xff));
        uint g = Clip255(((c0 >> 8) & 0xff) + ((c1 >> 8) & 0xff) - ((c2 >> 8) & 0xff));
        uint b = Clip255((c0 & 0xff) + (c1 & 0xff) - (c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddSubtractComponentHalf(
        int a,
        int b)
    {
        return Clip255((uint)(a + ((a - b) / 2)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ClampedAddSubtractHalf(
        uint c0,
        uint c1,
        uint c2)
    {
        uint ave = Average2(c0, c1);
        uint a = AddSubtractComponentHalf((int)(ave >> 24), (int)(c2 >> 24));
        uint r = AddSubtractComponentHalf((int)((ave >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        uint g = AddSubtractComponentHalf((int)((ave >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        uint b = AddSubtractComponentHalf((int)(ave & 0xff), (int)(c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sub3(
        int a,
        int b,
        int c)
    {
        int pb = b - c;
        int pa = a - c;
        return Math.Abs(pb) - Math.Abs(pa);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Select(
        uint a,
        uint b,
        uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
            Sub3((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff), (int)((c >> 16) & 0xff)) +
            Sub3((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff), (int)((c >> 8) & 0xff)) +
            Sub3((int)(a & 0xff), (int)(b & 0xff), (int)(c & 0xff));
        return (paMinusPb <= 0) ? a : b;
    }

    // The prediction of mode 0 to 13 from the pixel to the left and the row above, where
    // top is the pixel above (top[-1] above-left, top[1] above-right) (VP8LPredictors)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Predict(
        int mode,
        uint left,
        ReadOnlySpan<uint> upper,
        int x)
    {
        switch (mode)
        {
            case 0: return ArgbBlack;
            case 1: return left;
            case 2: return upper[x];
            case 3: return upper[x + 1];
            case 4: return upper[x - 1];
            case 5: return Average2(Average2(left, upper[x + 1]), upper[x]);
            case 6: return Average2(left, upper[x - 1]);
            case 7: return Average2(left, upper[x]);
            case 8: return Average2(upper[x - 1], upper[x]);
            case 9: return Average2(upper[x], upper[x + 1]);
            case 10: return Average2(Average2(left, upper[x - 1]), Average2(upper[x], upper[x + 1]));
            case 11: return Select(upper[x], left, upper[x - 1]);
            case 12: return ClampedAddSubtractFull(left, upper[x], upper[x - 1]);
            case 13: return ClampedAddSubtractHalf(left, upper[x], upper[x - 1]);
            default: return ArgbBlack;
        }
    }

    // The residuals of pixels current[start..start+count) against mode's prediction, into output
    // (VP8LPredictorsSub): the pixel to the left is current[x - 1], the row above upper.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PredictorSub(
        int mode,
        ReadOnlySpan<uint> current,
        ReadOnlySpan<uint> upper,
        int start,
        int count,
        Span<uint> output)
    {
        int done = PredictorSubVector(mode, current, upper, start, count, output);
        start += done;
        count -= done;
        output = output.Slice(done);
        switch (mode)
        {
            case 0:
                for (int i = 0; i < count; i++)
                {
                    output[i] = SubPixels(current[start + i], ArgbBlack);
                }

                break;

            case 1:
                for (int i = 0; i < count; i++)
                {
                    output[i] = SubPixels(current[start + i], current[start + i - 1]);
                }

                break;

            case 2:
                for (int i = 0; i < count; i++)
                {
                    output[i] = SubPixels(current[start + i], upper[start + i]);
                }

                break;

            case 3:
                for (int i = 0; i < count; i++)
                {
                    output[i] = SubPixels(current[start + i], upper[start + i + 1]);
                }

                break;

            case 4:
                for (int i = 0; i < count; i++)
                {
                    output[i] = SubPixels(current[start + i], upper[start + i - 1]);
                }

                break;

            default:
                for (int i = 0; i < count; i++)
                {
                    int x = start + i;
                    output[i] = SubPixels(current[x], Predict(mode, current[x - 1], upper, x));
                }

                break;
        }
    }

    //------------------------------------------------------------------------------
    // Transforms

    private static void SubtractGreenFromBlueAndRed(
        Span<uint> argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            uint pixel = argb[i];
            uint green = (pixel >> 8) & 0xff;
            uint newRed = (((pixel >> 16) & 0xff) - green) & 0xff;
            uint newBlue = ((pixel & 0xff) - green) & 0xff;
            argb[i] = (pixel & 0xff00ff00u) | (newRed << 16) | newBlue;
        }
    }

    private struct Multipliers
    {
        public byte GreenToRed;
        public byte GreenToBlue;
        public byte RedToBlue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColorTransformDelta(
        sbyte colorPred,
        sbyte color)
    {
        return (colorPred * color) >> 5;
    }

    private static void TransformColor(
        Multipliers m,
        Span<uint> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            uint argb = data[i];
            sbyte green = (sbyte)(argb >> 8);
            sbyte red = (sbyte)(argb >> 16);
            int newRed = red & 0xff;
            int newBlue = (int)(argb & 0xff);
            newRed -= ColorTransformDelta((sbyte)m.GreenToRed, green);
            newRed &= 0xff;
            newBlue -= ColorTransformDelta((sbyte)m.GreenToBlue, green);
            newBlue -= ColorTransformDelta((sbyte)m.RedToBlue, red);
            newBlue &= 0xff;
            data[i] = (argb & 0xff00ff00u) | ((uint)newRed << 16) | (uint)newBlue;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CollectColorRedTransforms(
        ReadOnlySpan<uint> argb,
        int stride,
        int tileWidth,
        int tileHeight,
        int greenToRed,
        Span<uint> histogram)
    {
        sbyte multiplier = (sbyte)greenToRed;
        ref uint h = ref MemoryMarshal.GetReference(histogram.Slice(0, 256));
        for (int y = 0; y < tileHeight; y++)
        {
            ref uint row = ref MemoryMarshal.GetReference(argb.Slice(y * stride, tileWidth));
            for (nint x = 0; x < tileWidth; x++)
            {
                uint pixel = Unsafe.Add(ref row, x);
                int newRed = (int)(pixel >> 16) - ColorTransformDelta(multiplier, (sbyte)(pixel >> 8));
                Unsafe.Add(ref h, newRed & 0xff)++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CollectColorBlueTransforms(
        ReadOnlySpan<uint> argb,
        int stride,
        int tileWidth,
        int tileHeight,
        int greenToBlue,
        int redToBlue,
        Span<uint> histogram)
    {
        sbyte greenMultiplier = (sbyte)greenToBlue;
        sbyte redMultiplier = (sbyte)redToBlue;
        ref uint h = ref MemoryMarshal.GetReference(histogram.Slice(0, 256));
        for (int y = 0; y < tileHeight; y++)
        {
            ref uint row = ref MemoryMarshal.GetReference(argb.Slice(y * stride, tileWidth));
            for (nint x = 0; x < tileWidth; x++)
            {
                uint pixel = Unsafe.Add(ref row, x);
                int newBlue = (int)(pixel & 0xff);
                newBlue -= ColorTransformDelta(greenMultiplier, (sbyte)(pixel >> 8));
                newBlue -= ColorTransformDelta(redMultiplier, (sbyte)(pixel >> 16));
                Unsafe.Add(ref h, newBlue & 0xff)++;
            }
        }
    }

    // Packs 1, 2, 4 or 8 palette indices into one pixel's green (VP8LBundleColorMap)
    private static void BundleColorMap(
        ReadOnlySpan<byte> row,
        int width,
        int xbits,
        Span<uint> destination)
    {
        ref byte source = ref MemoryMarshal.GetReference(row.Slice(0, width));
        ref uint target = ref MemoryMarshal.GetReference(destination.Slice(0, SubSampleSize(width, xbits)));
        if (xbits > 0)
        {
            // Each word packs 1 << xbits indices of bitDepth bits, the first lowest
            int bitDepth = 1 << (3 - xbits);
            int perWord = 1 << xbits;
            int words = width >> xbits;
            for (nint w = 0; w < words; w++)
            {
                ref byte group = ref Unsafe.Add(ref source, w << xbits);
                uint code = 0xff000000;
                for (int k = 0; k < perWord; k++)
                {
                    code |= (uint)Unsafe.Add(ref group, k) << (8 + (bitDepth * k));
                }

                Unsafe.Add(ref target, w) = code;
            }

            int rest = width & (perWord - 1);
            if (rest != 0)
            {
                ref byte group = ref Unsafe.Add(ref source, (nint)words << xbits);
                uint code = 0xff000000;
                for (int k = 0; k < rest; k++)
                {
                    code |= (uint)Unsafe.Add(ref group, k) << (8 + (bitDepth * k));
                }

                Unsafe.Add(ref target, words) = code;
            }
        }
        else
        {
            nint x = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                // 16 indices at a time, widened to words
                var alpha = Vector128.Create(0xff000000u);
                for (; x + 16 <= width; x += 16)
                {
                    var bytes = Vector128.LoadUnsafe(ref source, (nuint)x);
                    var (low, high) = Vector128.Widen(bytes);
                    var (w0, w1) = Vector128.Widen(low);
                    var (w2, w3) = Vector128.Widen(high);
                    ((w0 << 8) | alpha).StoreUnsafe(ref target, (nuint)x);
                    ((w1 << 8) | alpha).StoreUnsafe(ref target, (nuint)x + 4);
                    ((w2 << 8) | alpha).StoreUnsafe(ref target, (nuint)x + 8);
                    ((w3 << 8) | alpha).StoreUnsafe(ref target, (nuint)x + 12);
                }
            }

            for (; x < width; x++)
            {
                Unsafe.Add(ref target, x) = 0xff000000 | ((uint)Unsafe.Add(ref source, x) << 8);
            }
        }
    }
}
