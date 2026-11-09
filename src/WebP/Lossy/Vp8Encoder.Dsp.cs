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

namespace DotGame.Imaging.WebP;

// The encoder's transforms, predictors, distortion metrics and quantization, on blocks of stride Bps
internal static partial class Vp8Encoder
{
    private const int Bps = 32;
    private const int MaxLevel = 2047;
    private const int QFix = 17;
    private const int MaxCoeffThresh = 31;

    // The work buffers' layout (vp8i_enc.h)
    private const int YuvSizeEnc = Bps * 16;
    private const int PredSizeEnc = (32 * Bps) + (16 * Bps) + (8 * Bps);
    private const int YOffEnc = 0;
    private const int UOffEnc = 16;
    private const int VOffEnc = 16 + 8;

    // Where each prediction goes in the prediction buffer
    private const int I16DC16 = 0 * 16 * Bps;
    private const int I16TM16 = I16DC16 + 16;
    private const int I16VE16 = 1 * 16 * Bps;
    private const int I16HE16 = I16VE16 + 16;
    private const int C8DC8 = 2 * 16 * Bps;
    private const int C8TM8 = C8DC8 + (1 * 16);
    private const int C8VE8 = (2 * 16 * Bps) + (8 * Bps);
    private const int C8HE8 = C8VE8 + (1 * 16);
    private const int I4DC4 = (3 * 16 * Bps) + 0;
    private const int I4TM4 = I4DC4 + 4;
    private const int I4VE4 = I4DC4 + 8;
    private const int I4HE4 = I4DC4 + 12;
    private const int I4RD4 = I4DC4 + 16;
    private const int I4VR4 = I4DC4 + 20;
    private const int I4LD4 = I4DC4 + 24;
    private const int I4VL4 = I4DC4 + 28;
    private const int I4HD4 = (3 * 16 * Bps) + (4 * Bps);
    private const int I4HU4 = I4HD4 + 4;
    private const int I4TMP = I4HD4 + 8;

    private static ReadOnlySpan<ushort> I16ModeOffsets => [I16DC16, I16TM16, I16VE16, I16HE16];

    private static ReadOnlySpan<ushort> UvModeOffsets => [C8DC8, C8TM8, C8VE8, C8HE8];

    private static ReadOnlySpan<ushort> I4ModeOffsets => [I4DC4, I4TM4, I4VE4, I4HE4, I4RD4, I4VR4, I4LD4, I4VL4, I4HD4, I4HU4];

    private static ReadOnlySpan<ushort> Scan =>
    [
        0 + (0 * Bps), 4 + (0 * Bps), 8 + (0 * Bps), 12 + (0 * Bps),
        0 + (4 * Bps), 4 + (4 * Bps), 8 + (4 * Bps), 12 + (4 * Bps),
        0 + (8 * Bps), 4 + (8 * Bps), 8 + (8 * Bps), 12 + (8 * Bps),
        0 + (12 * Bps), 4 + (12 * Bps), 8 + (12 * Bps), 12 + (12 * Bps),
    ];

    // Luma, then U, then V (VP8DspScan); the chroma alone is ScanUv
    private static ReadOnlySpan<ushort> DspScan =>
    [
        0 + (0 * Bps), 4 + (0 * Bps), 8 + (0 * Bps), 12 + (0 * Bps),
        0 + (4 * Bps), 4 + (4 * Bps), 8 + (4 * Bps), 12 + (4 * Bps),
        0 + (8 * Bps), 4 + (8 * Bps), 8 + (8 * Bps), 12 + (8 * Bps),
        0 + (12 * Bps), 4 + (12 * Bps), 8 + (12 * Bps), 12 + (12 * Bps),
        0 + (0 * Bps), 4 + (0 * Bps), 0 + (4 * Bps), 4 + (4 * Bps),
        8 + (0 * Bps), 12 + (0 * Bps), 8 + (4 * Bps), 12 + (4 * Bps),
    ];

    private static ReadOnlySpan<ushort> ScanUv =>
    [
        0 + (0 * Bps), 4 + (0 * Bps), 0 + (4 * Bps), 4 + (4 * Bps),
        8 + (0 * Bps), 12 + (0 * Bps), 8 + (4 * Bps), 12 + (4 * Bps),
    ];

    private static ReadOnlySpan<byte> Zigzag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    private static ReadOnlySpan<ushort> WeightY => [38, 32, 20, 9, 32, 28, 17, 7, 20, 17, 10, 4, 9, 7, 4, 2];

    private static ReadOnlySpan<ushort> WeightTrellis => [30, 27, 19, 11, 27, 24, 17, 10, 19, 17, 12, 8, 11, 10, 8, 6];

    internal static ReadOnlySpan<ushort> GammaToLinearSpan => GammaToLinearTab;

    internal static ReadOnlySpan<int> LinearToGammaSpan => LinearToGammaTab;

    internal static ushort GammaToLinearAt(
        int v)
    {
        return GammaToLinearTab[v];
    }

    internal static int LinearToGammaAt(
        int v)
    {
        return LinearToGammaTab[v];
    }

    internal static uint InvAlphaAt(
        int v)
    {
        return InvAlpha[v];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8(
        int v)
    {
        return ((v & ~0xff) == 0) ? (byte)v : (v < 0) ? (byte)0 : (byte)255;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul1(
        int a)
    {
        return ((a * 20091) >> 16) + a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul2(
        int a)
    {
        return (a * 35468) >> 16;
    }

    // ITransformOne: the inverse transform of in onto ref, to dst
    private static void ITransformOne(
        ReadOnlySpan<byte> reference,
        int r,
        ReadOnlySpan<short> input,
        Span<byte> dst,
        int d)
    {
        Span<int> c = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = input[i] + input[8 + i];
            int b = input[i] - input[8 + i];
            int cc = Mul2(input[4 + i]) - Mul1(input[12 + i]);
            int dd = Mul1(input[4 + i]) + Mul2(input[12 + i]);
            c[(4 * i) + 0] = a + dd;
            c[(4 * i) + 1] = b + cc;
            c[(4 * i) + 2] = b - cc;
            c[(4 * i) + 3] = a - dd;
        }

        for (int i = 0; i < 4; i++)
        {
            int dc = c[i] + 4;
            int a = dc + c[8 + i];
            int b = dc - c[8 + i];
            int cc = Mul2(c[4 + i]) - Mul1(c[12 + i]);
            int dd = Mul1(c[4 + i]) + Mul2(c[12 + i]);
            int row = i * Bps;
            dst[d + row + 0] = Clip8(reference[r + row + 0] + ((a + dd) >> 3));
            dst[d + row + 1] = Clip8(reference[r + row + 1] + ((b + cc) >> 3));
            dst[d + row + 2] = Clip8(reference[r + row + 2] + ((b - cc) >> 3));
            dst[d + row + 3] = Clip8(reference[r + row + 3] + ((a - dd) >> 3));
        }
    }

    private static void ITransform(
        ReadOnlySpan<byte> reference,
        int r,
        ReadOnlySpan<short> input,
        Span<byte> dst,
        int d,
        bool doTwo)
    {
        if (SimdKernels)
        {
            ITransformSimd(ref Unsafe.Add(ref MemoryMarshal.GetReference(reference), r), ref MemoryMarshal.GetReference(input), ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), d), doTwo);
            return;
        }

        ITransformOne(reference, r, input, dst, d);
        if (doTwo)
        {
            ITransformOne(reference, r + 4, input.Slice(16), dst, d + 4);
        }
    }

    // FTransform_C
    private static void FTransform(
        ReadOnlySpan<byte> src,
        int s,
        ReadOnlySpan<byte> reference,
        int r,
        Span<short> output)
    {
        if (SimdKernels)
        {
            FTransformSimd(ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s), ref Unsafe.Add(ref MemoryMarshal.GetReference(reference), r), ref MemoryMarshal.GetReference(output), two: false);
            return;
        }

        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++, s += Bps, r += Bps)
        {
            int d0 = src[s + 0] - reference[r + 0];
            int d1 = src[s + 1] - reference[r + 1];
            int d2 = src[s + 2] - reference[r + 2];
            int d3 = src[s + 3] - reference[r + 3];
            int a0 = d0 + d3;
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;
            tmp[0 + (i * 4)] = (a0 + a1) * 8;
            tmp[1 + (i * 4)] = ((a2 * 2217) + (a3 * 5352) + 1812) >> 9;
            tmp[2 + (i * 4)] = (a0 - a1) * 8;
            tmp[3 + (i * 4)] = ((a3 * 2217) - (a2 * 5352) + 937) >> 9;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[0 + i] + tmp[12 + i];
            int a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i];
            int a3 = tmp[0 + i] - tmp[12 + i];
            output[0 + i] = (short)((a0 + a1 + 7) >> 4);
            output[4 + i] = (short)((((a2 * 2217) + (a3 * 5352) + 12000) >> 16) + ((a3 != 0) ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)(((a3 * 2217) - (a2 * 5352) + 51000) >> 16);
        }
    }

    private static void FTransform2(
        ReadOnlySpan<byte> src,
        int s,
        ReadOnlySpan<byte> reference,
        int r,
        Span<short> output)
    {
        if (SimdKernels)
        {
            FTransformSimd(ref Unsafe.Add(ref MemoryMarshal.GetReference(src), s), ref Unsafe.Add(ref MemoryMarshal.GetReference(reference), r), ref MemoryMarshal.GetReference(output), two: true);
            return;
        }

        FTransform(src, s, reference, r, output);
        FTransform(src, s + 4, reference, r + 4, output.Slice(16));
    }

    // FTransformWHT_C: the 16 luma blocks' DC coefficients (in[0], in[16], ...) to the second order
    private static void FTransformWht(
        ReadOnlySpan<short> input,
        Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        int at = 0;
        for (int i = 0; i < 4; i++, at += 64)
        {
            int a0 = input[at + (0 * 16)] + input[at + (2 * 16)];
            int a1 = input[at + (1 * 16)] + input[at + (3 * 16)];
            int a2 = input[at + (1 * 16)] - input[at + (3 * 16)];
            int a3 = input[at + (0 * 16)] - input[at + (2 * 16)];
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
            output[0 + i] = (short)(b0 >> 1);
            output[4 + i] = (short)(b1 >> 1);
            output[8 + i] = (short)(b2 >> 1);
            output[12 + i] = (short)(b3 >> 1);
        }
    }

    // The decoder's inverse WHT (TransformWHT_C): to each block's coefficient 0
    private static void TransformWht(
        ReadOnlySpan<short> input,
        Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[0 + i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[0 + i] - input[12 + i];
            tmp[0 + i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        int o = 0;
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[0 + (i * 4)] + 3;
            int a0 = dc + tmp[3 + (i * 4)];
            int a1 = tmp[1 + (i * 4)] + tmp[2 + (i * 4)];
            int a2 = tmp[1 + (i * 4)] - tmp[2 + (i * 4)];
            int a3 = dc - tmp[3 + (i * 4)];
            output[o + 0] = (short)((a0 + a1) >> 3);
            output[o + 16] = (short)((a3 + a2) >> 3);
            output[o + 32] = (short)((a0 - a1) >> 3);
            output[o + 48] = (short)((a3 - a2) >> 3);
            o += 64;
        }
    }
}
