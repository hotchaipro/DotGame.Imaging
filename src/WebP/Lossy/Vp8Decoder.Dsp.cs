// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dsp/dec.c and dec_clip_tables.c), modified by Hot Chai Games LLC:
// written in C#. libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;

namespace DotGame.Imaging.WebP;

// The inverse transforms, the intra predictors and the loop filters, on the work block (stride Bps)
// and the row cache
internal static partial class Vp8Decoder
{
    private const int TransformC1 = 20091;
    private const int TransformC2 = 35468;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul1(
        int a)
    {
        return ((a * TransformC1) >> 16) + a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mul2(
        int a)
    {
        return (a * TransformC2) >> 16;
    }

    // clip_8b
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip8(
        int v)
    {
        return ((v & ~0xff) == 0) ? (byte)v : (v < 0) ? (byte)0 : (byte)255;
    }

    // TransformWHT_C: the 16 luma DC coefficients to each block's coefficient 0
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg3(
        int a,
        int b,
        int c)
    {
        return (byte)((a + (2 * b) + c + 2) >> 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg2(
        int a,
        int b)
    {
        return (byte)((a + b + 1) >> 1);
    }

    // The clip tables of dec_clip_tables.c, as arithmetic
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SClip1(
        int v)
    {
        return (v < -128) ? -128 : (v > 127) ? 127 : v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SClip2(
        int v)
    {
        return (v < -16) ? -16 : (v > 15) ? 15 : v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip1(
        int v)
    {
        return (v < 0) ? (byte)0 : (v > 255) ? (byte)255 : (byte)v;
    }

    // DoFilter2_C
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter2(
        Span<byte> buf,
        int p,
        int step)
    {
        int p1 = buf[p - (2 * step)], p0 = buf[p - step], q0 = buf[p], q1 = buf[p + step];
        int a = (3 * (q0 - p0)) + SClip1(p1 - q1);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        buf[p - step] = Clip1(p0 + a2);
        buf[p] = Clip1(q0 - a1);
    }

    // DoFilter4_C
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter4(
        Span<byte> buf,
        int p,
        int step)
    {
        int p1 = buf[p - (2 * step)], p0 = buf[p - step], q0 = buf[p], q1 = buf[p + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2((a + 4) >> 3);
        int a2 = SClip2((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;
        buf[p - (2 * step)] = Clip1(p1 + a3);
        buf[p - step] = Clip1(p0 + a2);
        buf[p] = Clip1(q0 - a1);
        buf[p + step] = Clip1(q1 - a3);
    }

    // DoFilter6_C
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter6(
        Span<byte> buf,
        int p,
        int step)
    {
        int p2 = buf[p - (3 * step)], p1 = buf[p - (2 * step)], p0 = buf[p - step];
        int q0 = buf[p], q1 = buf[p + step], q2 = buf[p + (2 * step)];
        int a = SClip1((3 * (q0 - p0)) + SClip1(p1 - q1));
        int a1 = ((27 * a) + 63) >> 7;
        int a2 = ((18 * a) + 63) >> 7;
        int a3 = ((9 * a) + 63) >> 7;
        buf[p - (3 * step)] = Clip1(p2 + a3);
        buf[p - (2 * step)] = Clip1(p1 + a2);
        buf[p - step] = Clip1(p0 + a1);
        buf[p] = Clip1(q0 - a1);
        buf[p + step] = Clip1(q1 - a2);
        buf[p + (2 * step)] = Clip1(q2 - a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Hev(
        Span<byte> buf,
        int p,
        int step,
        int threshold)
    {
        int p1 = buf[p - (2 * step)], p0 = buf[p - step], q0 = buf[p], q1 = buf[p + step];
        return (Math.Abs(p1 - p0) > threshold) || (Math.Abs(q1 - q0) > threshold);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter(
        Span<byte> buf,
        int p,
        int step,
        int t)
    {
        int p1 = buf[p - (2 * step)], p0 = buf[p - step], q0 = buf[p], q1 = buf[p + step];
        return ((4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1)) <= t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilter2(
        Span<byte> buf,
        int p,
        int step,
        int t,
        int it)
    {
        int p3 = buf[p - (4 * step)], p2 = buf[p - (3 * step)], p1 = buf[p - (2 * step)];
        int p0 = buf[p - step], q0 = buf[p];
        int q1 = buf[p + step], q2 = buf[p + (2 * step)], q3 = buf[p + (3 * step)];
        if (((4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1)) > t)
        {
            return false;
        }

        return (Math.Abs(p3 - p2) <= it) && (Math.Abs(p2 - p1) <= it) && (Math.Abs(p1 - p0) <= it)
            && (Math.Abs(q3 - q2) <= it) && (Math.Abs(q2 - q1) <= it) && (Math.Abs(q1 - q0) <= it);
    }

    // SimpleVFilter16_C: the edge above row p
    private static void SimpleVFilter16(
        Span<byte> buf,
        int p,
        int stride,
        int threshold)
    {
        int threshold2 = (2 * threshold) + 1;
        for (int i = 0; i < 16; i++)
        {
            if (NeedsFilter(buf, p + i, stride, threshold2))
            {
                DoFilter2(buf, p + i, stride);
            }
        }
    }

    // SimpleHFilter16_C: the edge left of column p
    private static void SimpleHFilter16(
        Span<byte> buf,
        int p,
        int stride,
        int threshold)
    {
        int threshold2 = (2 * threshold) + 1;
        for (int i = 0; i < 16; i++)
        {
            if (NeedsFilter(buf, p + (i * stride), 1, threshold2))
            {
                DoFilter2(buf, p + (i * stride), 1);
            }
        }
    }

    private static void SimpleVFilter16i(
        Span<byte> buf,
        int p,
        int stride,
        int threshold)
    {
        for (int k = 1; k <= 3; k++)
        {
            SimpleVFilter16(buf, p + (4 * k * stride), stride, threshold);
        }
    }

    private static void SimpleHFilter16i(
        Span<byte> buf,
        int p,
        int stride,
        int threshold)
    {
        for (int k = 1; k <= 3; k++)
        {
            SimpleHFilter16(buf, p + (4 * k), stride, threshold);
        }
    }

    // FilterLoop26_C: a macroblock edge, across hstride, along vstride
    private static void FilterLoop26(
        Span<byte> buf,
        int p,
        int hstride,
        int vstride,
        int size,
        int threshold,
        int innerThreshold,
        int hevThreshold)
    {
        int threshold2 = (2 * threshold) + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(buf, p, hstride, threshold2, innerThreshold))
            {
                if (Hev(buf, p, hstride, hevThreshold))
                {
                    DoFilter2(buf, p, hstride);
                }
                else
                {
                    DoFilter6(buf, p, hstride);
                }
            }

            p += vstride;
        }
    }

    // FilterLoop24_C: an inner edge
    private static void FilterLoop24(
        Span<byte> buf,
        int p,
        int hstride,
        int vstride,
        int size,
        int threshold,
        int innerThreshold,
        int hevThreshold)
    {
        int threshold2 = (2 * threshold) + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(buf, p, hstride, threshold2, innerThreshold))
            {
                if (Hev(buf, p, hstride, hevThreshold))
                {
                    DoFilter2(buf, p, hstride);
                }
                else
                {
                    DoFilter4(buf, p, hstride);
                }
            }

            p += vstride;
        }
    }
}
