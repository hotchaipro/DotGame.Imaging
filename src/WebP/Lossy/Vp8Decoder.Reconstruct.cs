// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/frame_dec.c's ReconstructRow and src/dsp/dec.c), modified by Hot
// Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.WebP;

// The macroblocks' reconstruction, on refs into the work block (stride Bps): the predictors, the
// inverse transforms added to them, and the copies in and out
internal static partial class Vp8Decoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte At(
        ref byte p,
        int x,
        int y)
    {
        return ref Unsafe.Add(ref p, x + (y * Bps));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRef(
        ref byte p,
        int v)
    {
        p = Clip8(p + (v >> 3));
    }

    // TransformOne_C
    private static void TransformOneRef(
        ref short input,
        ref byte dst)
    {
        // The vertical pass into four columns of four, then the horizontal one onto the prediction
        int c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15;
        {
            int a = Unsafe.Add(ref input, 0) + Unsafe.Add(ref input, 8);
            int b = Unsafe.Add(ref input, 0) - Unsafe.Add(ref input, 8);
            int c = Mul2(Unsafe.Add(ref input, 4)) - Mul1(Unsafe.Add(ref input, 12));
            int d = Mul1(Unsafe.Add(ref input, 4)) + Mul2(Unsafe.Add(ref input, 12));
            c0 = a + d;
            c1 = b + c;
            c2 = b - c;
            c3 = a - d;
        }

        {
            int a = Unsafe.Add(ref input, 1) + Unsafe.Add(ref input, 9);
            int b = Unsafe.Add(ref input, 1) - Unsafe.Add(ref input, 9);
            int c = Mul2(Unsafe.Add(ref input, 5)) - Mul1(Unsafe.Add(ref input, 13));
            int d = Mul1(Unsafe.Add(ref input, 5)) + Mul2(Unsafe.Add(ref input, 13));
            c4 = a + d;
            c5 = b + c;
            c6 = b - c;
            c7 = a - d;
        }

        {
            int a = Unsafe.Add(ref input, 2) + Unsafe.Add(ref input, 10);
            int b = Unsafe.Add(ref input, 2) - Unsafe.Add(ref input, 10);
            int c = Mul2(Unsafe.Add(ref input, 6)) - Mul1(Unsafe.Add(ref input, 14));
            int d = Mul1(Unsafe.Add(ref input, 6)) + Mul2(Unsafe.Add(ref input, 14));
            c8 = a + d;
            c9 = b + c;
            c10 = b - c;
            c11 = a - d;
        }

        {
            int a = Unsafe.Add(ref input, 3) + Unsafe.Add(ref input, 11);
            int b = Unsafe.Add(ref input, 3) - Unsafe.Add(ref input, 11);
            int c = Mul2(Unsafe.Add(ref input, 7)) - Mul1(Unsafe.Add(ref input, 15));
            int d = Mul1(Unsafe.Add(ref input, 7)) + Mul2(Unsafe.Add(ref input, 15));
            c12 = a + d;
            c13 = b + c;
            c14 = b - c;
            c15 = a - d;
        }

        HorizontalPass(ref dst, c0, c4, c8, c12);
        HorizontalPass(ref Unsafe.Add(ref dst, Bps), c1, c5, c9, c13);
        HorizontalPass(ref Unsafe.Add(ref dst, 2 * Bps), c2, c6, c10, c14);
        HorizontalPass(ref Unsafe.Add(ref dst, 3 * Bps), c3, c7, c11, c15);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HorizontalPass(
        ref byte row,
        int t0,
        int t4,
        int t8,
        int t12)
    {
        int dc = t0 + 4;
        int a = dc + t8;
        int b = dc - t8;
        int c = Mul2(t4) - Mul1(t12);
        int d = Mul1(t4) + Mul2(t12);
        StoreRef(ref row, a + d);
        StoreRef(ref Unsafe.Add(ref row, 1), b + c);
        StoreRef(ref Unsafe.Add(ref row, 2), b - c);
        StoreRef(ref Unsafe.Add(ref row, 3), a - d);
    }

    // TransformAC3_C
    private static void TransformAc3Ref(
        ref short input,
        ref byte dst)
    {
        int a = Unsafe.Add(ref input, 0) + 4;
        int c4 = Mul2(Unsafe.Add(ref input, 4));
        int d4 = Mul1(Unsafe.Add(ref input, 4));
        int c1 = Mul2(Unsafe.Add(ref input, 1));
        int d1 = Mul1(Unsafe.Add(ref input, 1));
        Store2Ref(ref dst, a + d4, d1, c1);
        Store2Ref(ref Unsafe.Add(ref dst, Bps), a + c4, d1, c1);
        Store2Ref(ref Unsafe.Add(ref dst, 2 * Bps), a - c4, d1, c1);
        Store2Ref(ref Unsafe.Add(ref dst, 3 * Bps), a - d4, d1, c1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store2Ref(
        ref byte row,
        int dc,
        int d,
        int c)
    {
        StoreRef(ref row, dc + d);
        StoreRef(ref Unsafe.Add(ref row, 1), dc + c);
        StoreRef(ref Unsafe.Add(ref row, 2), dc - c);
        StoreRef(ref Unsafe.Add(ref row, 3), dc - d);
    }

    // TransformDC_C: (in[0] + 4) >> 3 added to all 16 pixels, clipped: with saturating byte adds,
    // which clip the same
    private static void TransformDcRef(
        ref short input,
        ref byte dst)
    {
        int dc = (Unsafe.Add(ref input, 0) + 4) >> 3;
        if (VectorFilters)
        {
            var rows = Vector128.Create(
                Unsafe.ReadUnaligned<uint>(ref dst),
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, Bps)),
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, 2 * Bps)),
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dst, 3 * Bps))).AsByte();
            rows = (dc >= 0) ? AddSatU(rows, Vector128.Create((byte)Math.Min(dc, 255))) : SubSatU(rows, Vector128.Create((byte)Math.Min(-dc, 255)));
            var words = rows.AsUInt32();
            Unsafe.WriteUnaligned(ref dst, words.GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Bps), words.GetElement(1));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2 * Bps), words.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3 * Bps), words.GetElement(3));
            return;
        }

        for (int j = 0; j < 4; j++)
        {
            ref byte row = ref Unsafe.Add(ref dst, j * Bps);
            for (int i = 0; i < 4; i++)
            {
                ref byte p = ref Unsafe.Add(ref row, i);
                p = Clip8(p + dc);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> SubSatU(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.SubtractSaturate(a, b) : Sse2.SubtractSaturate(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoTransformRef(
        uint bits,
        ref short src,
        ref byte dst)
    {
        switch (bits >> 30)
        {
            case 3:
                TransformOneRef(ref src, ref dst);
                break;
            case 2:
                TransformAc3Ref(ref src, ref dst);
                break;
            case 1:
                TransformDcRef(ref src, ref dst);
                break;
        }
    }

    private static void DoUvTransformRef(
        uint bits,
        ref short src,
        ref byte dst)
    {
        if ((bits & 0xff) != 0)
        {
            if ((bits & 0xaa) != 0)
            {
                TransformOneRef(ref src, ref dst);
                TransformOneRef(ref Unsafe.Add(ref src, 16), ref Unsafe.Add(ref dst, 4));
                TransformOneRef(ref Unsafe.Add(ref src, 32), ref Unsafe.Add(ref dst, 4 * Bps));
                TransformOneRef(ref Unsafe.Add(ref src, 48), ref Unsafe.Add(ref dst, (4 * Bps) + 4));
            }
            else
            {
                if (Unsafe.Add(ref src, 0) != 0)
                {
                    TransformDcRef(ref src, ref dst);
                }

                if (Unsafe.Add(ref src, 16) != 0)
                {
                    TransformDcRef(ref Unsafe.Add(ref src, 16), ref Unsafe.Add(ref dst, 4));
                }

                if (Unsafe.Add(ref src, 32) != 0)
                {
                    TransformDcRef(ref Unsafe.Add(ref src, 32), ref Unsafe.Add(ref dst, 4 * Bps));
                }

                if (Unsafe.Add(ref src, 48) != 0)
                {
                    TransformDcRef(ref Unsafe.Add(ref src, 48), ref Unsafe.Add(ref dst, (4 * Bps) + 4));
                }
            }
        }
    }

    // Rows of a block set to one value: 4, 8 or 16 wide
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillRef(
        ref byte dst,
        int size,
        int value)
    {
        if (size == 16)
        {
            var v = Vector128.Create((byte)value);
            for (int j = 0; j < 16; j++)
            {
                v.StoreUnsafe(ref Unsafe.Add(ref dst, j * Bps));
            }
        }
        else if (size == 8)
        {
            ulong v = 0x0101010101010101ul * (byte)value;
            for (int j = 0; j < 8; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, j * Bps), v);
            }
        }
        else
        {
            uint v = 0x01010101u * (byte)value;
            for (int j = 0; j < 4; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, j * Bps), v);
            }
        }
    }

    // TrueMotion: top + left - top-left, clipped; 16 lanes at a time in 16 bits
    private static void TrueMotionRef(
        ref byte dst,
        int size)
    {
        ref byte top = ref Unsafe.Subtract(ref dst, Bps);
        int topLeft = Unsafe.Subtract(ref top, 1);
        if (VectorFilters && (size >= 8))
        {
            var t = Vector128.LoadUnsafe(ref top);
            var tLow = Vector128.WidenLower(t).AsInt16();
            var tHigh = Vector128.WidenUpper(t).AsInt16();
            for (int y = 0; y < size; y++)
            {
                ref byte row = ref Unsafe.Add(ref dst, y * Bps);
                var left = Vector128.Create((short)(Unsafe.Subtract(ref row, 1) - topLeft));
                var low = tLow + left;
                if (size == 16)
                {
                    ToBytesSaturated(low, tHigh + left).StoreUnsafe(ref row);
                }
                else
                {
                    Unsafe.WriteUnaligned(ref row, ToBytesSaturated(low, low).AsUInt64().ToScalar());
                }
            }

            return;
        }

        for (int y = 0; y < size; y++)
        {
            ref byte row = ref Unsafe.Add(ref dst, y * Bps);
            int left = Unsafe.Subtract(ref row, 1) - topLeft;
            for (int x = 0; x < size; x++)
            {
                Unsafe.Add(ref row, x) = Clip8(Unsafe.Add(ref top, x) + left);
            }
        }
    }

    // Sixteen 16-bit values clamped to bytes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ToBytesSaturated(
        Vector128<short> low,
        Vector128<short> high)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(low), high);
        }

        return Sse2.PackUnsignedSaturate(low, high);
    }

    // VP8PredLuma16
    private static void PredictLuma16Ref(
        int mode,
        ref byte dst)
    {
        ref byte top = ref Unsafe.Subtract(ref dst, Bps);
        switch (mode)
        {
            case 0:
                {
                    int dc = 16;
                    for (int j = 0; j < 16; j++)
                    {
                        dc += Unsafe.Add(ref dst, (j * Bps) - 1) + Unsafe.Add(ref top, j);
                    }

                    FillRef(ref dst, 16, dc >> 5);
                    break;
                }

            case 1:
                TrueMotionRef(ref dst, 16);
                break;
            case 2:
                {
                    var t = Vector128.LoadUnsafe(ref top);
                    for (int j = 0; j < 16; j++)
                    {
                        t.StoreUnsafe(ref Unsafe.Add(ref dst, j * Bps));
                    }

                    break;
                }

            case 3:
                for (int j = 0; j < 16; j++)
                {
                    ref byte row = ref Unsafe.Add(ref dst, j * Bps);
                    Vector128.Create(Unsafe.Subtract(ref row, 1)).StoreUnsafe(ref row);
                }

                break;
            case DcPredNoTop:
                {
                    int dc = 8;
                    for (int j = 0; j < 16; j++)
                    {
                        dc += Unsafe.Add(ref dst, (j * Bps) - 1);
                    }

                    FillRef(ref dst, 16, dc >> 4);
                    break;
                }

            case DcPredNoLeft:
                {
                    int dc = 8;
                    for (int i = 0; i < 16; i++)
                    {
                        dc += Unsafe.Add(ref top, i);
                    }

                    FillRef(ref dst, 16, dc >> 4);
                    break;
                }

            default:
                FillRef(ref dst, 16, 0x80);
                break;
        }
    }

    // VP8PredChroma8
    private static void PredictChroma8Ref(
        int mode,
        ref byte dst)
    {
        ref byte top = ref Unsafe.Subtract(ref dst, Bps);
        switch (mode)
        {
            case 0:
                {
                    int dc = 8;
                    for (int i = 0; i < 8; i++)
                    {
                        dc += Unsafe.Add(ref top, i) + Unsafe.Add(ref dst, (i * Bps) - 1);
                    }

                    FillRef(ref dst, 8, dc >> 4);
                    break;
                }

            case 1:
                TrueMotionRef(ref dst, 8);
                break;
            case 2:
                {
                    ulong t = Unsafe.ReadUnaligned<ulong>(ref top);
                    for (int j = 0; j < 8; j++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, j * Bps), t);
                    }

                    break;
                }

            case 3:
                for (int j = 0; j < 8; j++)
                {
                    ref byte row = ref Unsafe.Add(ref dst, j * Bps);
                    Unsafe.WriteUnaligned(ref row, 0x0101010101010101ul * Unsafe.Subtract(ref row, 1));
                }

                break;
            case DcPredNoTop:
                {
                    int dc = 4;
                    for (int i = 0; i < 8; i++)
                    {
                        dc += Unsafe.Add(ref dst, (i * Bps) - 1);
                    }

                    FillRef(ref dst, 8, dc >> 3);
                    break;
                }

            case DcPredNoLeft:
                {
                    int dc = 4;
                    for (int i = 0; i < 8; i++)
                    {
                        dc += Unsafe.Add(ref top, i);
                    }

                    FillRef(ref dst, 8, dc >> 3);
                    break;
                }

            default:
                FillRef(ref dst, 8, 0x80);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetRef(
        ref byte dst,
        int x,
        int y,
        byte value)
    {
        At(ref dst, x, y) = value;
    }

    // VP8PredLuma4
    private static void PredictLuma4Ref(
        int mode,
        ref byte dst)
    {
        ref byte top = ref Unsafe.Subtract(ref dst, Bps);
        switch (mode)
        {
            case BDcPred:
                {
                    int dc = 4;
                    for (int i = 0; i < 4; i++)
                    {
                        dc += Unsafe.Add(ref top, i) + Unsafe.Add(ref dst, (i * Bps) - 1);
                    }

                    FillRef(ref dst, 4, dc >> 3);
                    break;
                }

            case BTmPred:
                TrueMotionRef(ref dst, 4);
                break;
            case BVePred:
                {
                    uint v = (uint)(Avg3(Unsafe.Subtract(ref top, 1), top, Unsafe.Add(ref top, 1))
                        | (Avg3(top, Unsafe.Add(ref top, 1), Unsafe.Add(ref top, 2)) << 8)
                        | (Avg3(Unsafe.Add(ref top, 1), Unsafe.Add(ref top, 2), Unsafe.Add(ref top, 3)) << 16)
                        | (Avg3(Unsafe.Add(ref top, 2), Unsafe.Add(ref top, 3), Unsafe.Add(ref top, 4)) << 24));
                    for (int i = 0; i < 4; i++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, i * Bps), v);
                    }

                    break;
                }

            case BHePred:
                {
                    int a = Unsafe.Subtract(ref dst, 1 + Bps);
                    int b = Unsafe.Subtract(ref dst, 1);
                    int c = Unsafe.Add(ref dst, Bps - 1);
                    int d = Unsafe.Add(ref dst, (2 * Bps) - 1);
                    int e = Unsafe.Add(ref dst, (3 * Bps) - 1);
                    Unsafe.WriteUnaligned(ref dst, 0x01010101u * Avg3(a, b, c));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Bps), 0x01010101u * Avg3(b, c, d));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2 * Bps), 0x01010101u * Avg3(c, d, e));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3 * Bps), 0x01010101u * Avg3(d, e, e));
                    break;
                }

            case BRdPred:
                {
                    int i = Unsafe.Subtract(ref dst, 1), j = Unsafe.Add(ref dst, Bps - 1), k = Unsafe.Add(ref dst, (2 * Bps) - 1), l = Unsafe.Add(ref dst, (3 * Bps) - 1);
                    int x = Unsafe.Subtract(ref top, 1), a = top, b = Unsafe.Add(ref top, 1), c = Unsafe.Add(ref top, 2), d = Unsafe.Add(ref top, 3);
                    SetRef(ref dst, 0, 3, Avg3(j, k, l));
                    byte v = Avg3(i, j, k);
                    SetRef(ref dst, 1, 3, v);
                    SetRef(ref dst, 0, 2, v);
                    v = Avg3(x, i, j);
                    SetRef(ref dst, 2, 3, v);
                    SetRef(ref dst, 1, 2, v);
                    SetRef(ref dst, 0, 1, v);
                    v = Avg3(a, x, i);
                    SetRef(ref dst, 3, 3, v);
                    SetRef(ref dst, 2, 2, v);
                    SetRef(ref dst, 1, 1, v);
                    SetRef(ref dst, 0, 0, v);
                    v = Avg3(b, a, x);
                    SetRef(ref dst, 3, 2, v);
                    SetRef(ref dst, 2, 1, v);
                    SetRef(ref dst, 1, 0, v);
                    v = Avg3(c, b, a);
                    SetRef(ref dst, 3, 1, v);
                    SetRef(ref dst, 2, 0, v);
                    SetRef(ref dst, 3, 0, Avg3(d, c, b));
                    break;
                }

            case BVrPred:
                {
                    int i = Unsafe.Subtract(ref dst, 1), j = Unsafe.Add(ref dst, Bps - 1), k = Unsafe.Add(ref dst, (2 * Bps) - 1);
                    int x = Unsafe.Subtract(ref top, 1), a = top, b = Unsafe.Add(ref top, 1), c = Unsafe.Add(ref top, 2), d = Unsafe.Add(ref top, 3);
                    byte v = Avg2(x, a);
                    SetRef(ref dst, 0, 0, v);
                    SetRef(ref dst, 1, 2, v);
                    v = Avg2(a, b);
                    SetRef(ref dst, 1, 0, v);
                    SetRef(ref dst, 2, 2, v);
                    v = Avg2(b, c);
                    SetRef(ref dst, 2, 0, v);
                    SetRef(ref dst, 3, 2, v);
                    SetRef(ref dst, 3, 0, Avg2(c, d));
                    SetRef(ref dst, 0, 3, Avg3(k, j, i));
                    SetRef(ref dst, 0, 2, Avg3(j, i, x));
                    v = Avg3(i, x, a);
                    SetRef(ref dst, 0, 1, v);
                    SetRef(ref dst, 1, 3, v);
                    v = Avg3(x, a, b);
                    SetRef(ref dst, 1, 1, v);
                    SetRef(ref dst, 2, 3, v);
                    v = Avg3(a, b, c);
                    SetRef(ref dst, 2, 1, v);
                    SetRef(ref dst, 3, 3, v);
                    SetRef(ref dst, 3, 1, Avg3(b, c, d));
                    break;
                }

            case BLdPred:
                {
                    int a = top, b = Unsafe.Add(ref top, 1), c = Unsafe.Add(ref top, 2), d = Unsafe.Add(ref top, 3);
                    int e = Unsafe.Add(ref top, 4), f = Unsafe.Add(ref top, 5), g = Unsafe.Add(ref top, 6), h = Unsafe.Add(ref top, 7);
                    SetRef(ref dst, 0, 0, Avg3(a, b, c));
                    byte v = Avg3(b, c, d);
                    SetRef(ref dst, 1, 0, v);
                    SetRef(ref dst, 0, 1, v);
                    v = Avg3(c, d, e);
                    SetRef(ref dst, 2, 0, v);
                    SetRef(ref dst, 1, 1, v);
                    SetRef(ref dst, 0, 2, v);
                    v = Avg3(d, e, f);
                    SetRef(ref dst, 3, 0, v);
                    SetRef(ref dst, 2, 1, v);
                    SetRef(ref dst, 1, 2, v);
                    SetRef(ref dst, 0, 3, v);
                    v = Avg3(e, f, g);
                    SetRef(ref dst, 3, 1, v);
                    SetRef(ref dst, 2, 2, v);
                    SetRef(ref dst, 1, 3, v);
                    v = Avg3(f, g, h);
                    SetRef(ref dst, 3, 2, v);
                    SetRef(ref dst, 2, 3, v);
                    SetRef(ref dst, 3, 3, Avg3(g, h, h));
                    break;
                }

            case BVlPred:
                {
                    int a = top, b = Unsafe.Add(ref top, 1), c = Unsafe.Add(ref top, 2), d = Unsafe.Add(ref top, 3);
                    int e = Unsafe.Add(ref top, 4), f = Unsafe.Add(ref top, 5), g = Unsafe.Add(ref top, 6), h = Unsafe.Add(ref top, 7);
                    SetRef(ref dst, 0, 0, Avg2(a, b));
                    byte v = Avg2(b, c);
                    SetRef(ref dst, 1, 0, v);
                    SetRef(ref dst, 0, 2, v);
                    v = Avg2(c, d);
                    SetRef(ref dst, 2, 0, v);
                    SetRef(ref dst, 1, 2, v);
                    v = Avg2(d, e);
                    SetRef(ref dst, 3, 0, v);
                    SetRef(ref dst, 2, 2, v);
                    SetRef(ref dst, 0, 1, Avg3(a, b, c));
                    v = Avg3(b, c, d);
                    SetRef(ref dst, 1, 1, v);
                    SetRef(ref dst, 0, 3, v);
                    v = Avg3(c, d, e);
                    SetRef(ref dst, 2, 1, v);
                    SetRef(ref dst, 1, 3, v);
                    v = Avg3(d, e, f);
                    SetRef(ref dst, 3, 1, v);
                    SetRef(ref dst, 2, 3, v);
                    SetRef(ref dst, 3, 2, Avg3(e, f, g));
                    SetRef(ref dst, 3, 3, Avg3(f, g, h));
                    break;
                }

            case BHdPred:
                {
                    int i = Unsafe.Subtract(ref dst, 1), j = Unsafe.Add(ref dst, Bps - 1), k = Unsafe.Add(ref dst, (2 * Bps) - 1), l = Unsafe.Add(ref dst, (3 * Bps) - 1);
                    int x = Unsafe.Subtract(ref top, 1), a = top, b = Unsafe.Add(ref top, 1), c = Unsafe.Add(ref top, 2);
                    byte v = Avg2(i, x);
                    SetRef(ref dst, 0, 0, v);
                    SetRef(ref dst, 2, 1, v);
                    v = Avg2(j, i);
                    SetRef(ref dst, 0, 1, v);
                    SetRef(ref dst, 2, 2, v);
                    v = Avg2(k, j);
                    SetRef(ref dst, 0, 2, v);
                    SetRef(ref dst, 2, 3, v);
                    SetRef(ref dst, 0, 3, Avg2(l, k));
                    SetRef(ref dst, 3, 0, Avg3(a, b, c));
                    SetRef(ref dst, 2, 0, Avg3(x, a, b));
                    v = Avg3(i, x, a);
                    SetRef(ref dst, 1, 0, v);
                    SetRef(ref dst, 3, 1, v);
                    v = Avg3(j, i, x);
                    SetRef(ref dst, 1, 1, v);
                    SetRef(ref dst, 3, 2, v);
                    v = Avg3(k, j, i);
                    SetRef(ref dst, 1, 2, v);
                    SetRef(ref dst, 3, 3, v);
                    SetRef(ref dst, 1, 3, Avg3(l, k, j));
                    break;
                }

            default:
                {
                    // BHuPred
                    int i = Unsafe.Subtract(ref dst, 1), j = Unsafe.Add(ref dst, Bps - 1), k = Unsafe.Add(ref dst, (2 * Bps) - 1), l = Unsafe.Add(ref dst, (3 * Bps) - 1);
                    SetRef(ref dst, 0, 0, Avg2(i, j));
                    byte v = Avg2(j, k);
                    SetRef(ref dst, 2, 0, v);
                    SetRef(ref dst, 0, 1, v);
                    v = Avg2(k, l);
                    SetRef(ref dst, 2, 1, v);
                    SetRef(ref dst, 0, 2, v);
                    SetRef(ref dst, 1, 0, Avg3(i, j, k));
                    v = Avg3(j, k, l);
                    SetRef(ref dst, 3, 0, v);
                    SetRef(ref dst, 1, 1, v);
                    v = Avg3(k, l, l);
                    SetRef(ref dst, 3, 1, v);
                    SetRef(ref dst, 1, 2, v);
                    byte last = (byte)l;
                    SetRef(ref dst, 3, 2, last);
                    SetRef(ref dst, 2, 2, last);
                    SetRef(ref dst, 0, 3, last);
                    SetRef(ref dst, 1, 3, last);
                    SetRef(ref dst, 2, 3, last);
                    SetRef(ref dst, 3, 3, last);
                    break;
                }
        }
    }

    private sealed partial class Decoding
    {
        // ReconstructRow: each macroblock predicted and its residuals added, in the work block, then
        // copied to the row cache
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void ReconstructRow(
            int mbY)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(this.yuvB);
            ref byte y = ref Unsafe.Add(ref b, YOff);
            ref byte u = ref Unsafe.Add(ref b, UOff);
            ref byte v = ref Unsafe.Add(ref b, VOff);
            for (int j = 0; j < 16; j++)
            {
                Unsafe.Add(ref y, (j * Bps) - 1) = 129;
            }

            for (int j = 0; j < 8; j++)
            {
                Unsafe.Add(ref u, (j * Bps) - 1) = 129;
                Unsafe.Add(ref v, (j * Bps) - 1) = 129;
            }

            if (mbY > 0)
            {
                Unsafe.Subtract(ref y, 1 + Bps) = 129;
                Unsafe.Subtract(ref u, 1 + Bps) = 129;
                Unsafe.Subtract(ref v, 1 + Bps) = 129;
            }
            else
            {
                Unsafe.InitBlockUnaligned(ref Unsafe.Subtract(ref y, Bps + 1), 127, 16 + 4 + 1);
                Unsafe.InitBlockUnaligned(ref Unsafe.Subtract(ref u, Bps + 1), 127, 8 + 1);
                Unsafe.InitBlockUnaligned(ref Unsafe.Subtract(ref v, Bps + 1), 127, 8 + 1);
            }

            ref byte topBase = ref MemoryMarshal.GetArrayDataReference(this.yuvT);
            ref short coeffBase = ref MemoryMarshal.GetArrayDataReference(this.coeffs);
            ref byte scan = ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(Scan));
            ref byte cy = ref MemoryMarshal.GetArrayDataReference(this.cacheY);
            ref byte cu = ref MemoryMarshal.GetArrayDataReference(this.cacheU);
            ref byte cv = ref MemoryMarshal.GetArrayDataReference(this.cacheV);
            int yStride = this.cacheYStride;
            int uvStride = this.cacheUvStride;
            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                if (mbX > 0)
                {
                    for (int j = -1; j < 16; j++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref y, (j * Bps) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref y, (j * Bps) + 12)));
                    }

                    for (int j = -1; j < 8; j++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref u, (j * Bps) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref u, (j * Bps) + 4)));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref v, (j * Bps) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref v, (j * Bps) + 4)));
                    }
                }

                ref byte top = ref Unsafe.Add(ref topBase, mbX * 32);
                ref short coeffs = ref Unsafe.Add(ref coeffBase, mbX * 384);
                uint bits = this.nonZeroY[mbX];
                if (mbY > 0)
                {
                    Vector128.LoadUnsafe(ref top).StoreUnsafe(ref Unsafe.Subtract(ref y, Bps));
                    Unsafe.WriteUnaligned(ref Unsafe.Subtract(ref u, Bps), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref top, 16)));
                    Unsafe.WriteUnaligned(ref Unsafe.Subtract(ref v, Bps), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref top, 24)));
                }

                if (this.isI4x4[mbX])
                {
                    ref byte topRight = ref Unsafe.Add(ref y, 16 - Bps);
                    if (mbY > 0)
                    {
                        uint tr = (mbX >= this.mbW - 1) ? 0x01010101u * Unsafe.Add(ref top, 15) : Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref top, 32));
                        Unsafe.WriteUnaligned(ref topRight, tr);
                    }

                    uint topRightValue = Unsafe.ReadUnaligned<uint>(ref topRight);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref topRight, 4 * Bps), topRightValue);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref topRight, 8 * Bps), topRightValue);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref topRight, 12 * Bps), topRightValue);
                    ref byte modes = ref this.imodes[mbX * 16];
                    for (int n = 0; n < 16; n++, bits <<= 2)
                    {
                        ref byte dst = ref Unsafe.Add(ref y, Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref scan, n * 2)));
                        PredictLuma4Ref(Unsafe.Add(ref modes, n), ref dst);
                        DoTransformRef(bits, ref Unsafe.Add(ref coeffs, n * 16), ref dst);
                    }
                }
                else
                {
                    PredictLuma16Ref(CheckMode(mbX, mbY, this.imodes[mbX * 16]), ref y);
                    if (bits != 0)
                    {
                        for (int n = 0; n < 16; n++, bits <<= 2)
                        {
                            DoTransformRef(bits, ref Unsafe.Add(ref coeffs, n * 16), ref Unsafe.Add(ref y, Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref scan, n * 2))));
                        }
                    }
                }

                uint bitsUv = this.nonZeroUv[mbX];
                int uvPred = CheckMode(mbX, mbY, this.uvMode[mbX]);
                PredictChroma8Ref(uvPred, ref u);
                PredictChroma8Ref(uvPred, ref v);
                DoUvTransformRef(bitsUv >> 0, ref Unsafe.Add(ref coeffs, 16 * 16), ref u);
                DoUvTransformRef(bitsUv >> 8, ref Unsafe.Add(ref coeffs, 20 * 16), ref v);

                if (mbY < this.mbH - 1)
                {
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref y, 15 * Bps)).StoreUnsafe(ref top);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref top, 16), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref u, 7 * Bps)));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref top, 24), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref v, 7 * Bps)));
                }

                // To the row cache
                ref byte yOut = ref Unsafe.Add(ref cy, this.cacheYOffset + (mbX * 16));
                ref byte uOut = ref Unsafe.Add(ref cu, this.cacheUvOffset + (mbX * 8));
                ref byte vOut = ref Unsafe.Add(ref cv, this.cacheUvOffset + (mbX * 8));
                for (int j = 0; j < 16; j++)
                {
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref y, j * Bps)).StoreUnsafe(ref Unsafe.Add(ref yOut, j * yStride));
                }

                for (int j = 0; j < 8; j++)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref uOut, j * uvStride), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref u, j * Bps)));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref vOut, j * uvStride), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref v, j * Bps)));
                }
            }
        }
    }
}
