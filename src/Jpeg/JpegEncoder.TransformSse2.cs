// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C# with SSE2, as
// libjpeg-turbo's simd/x86_64 forms work (multiply-adds of pairs, a quotient by two multiply-highs).
//
// The integer DCT's arithmetic and the quantization follow jfdctint.c and jcdctmgr.c, which carry
// these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1996, Thomas G. Lane.
//   libjpeg-turbo Modifications:
//   Copyright (C) 1999-2006, MIYASAKA Masaru.
//   Copyright 2009 Pierre Ossman <ossman@cendio.se> for Cendio AB
//   Copyright (C) 2011, 2014-2015, 2022, 2024, D. R. Commander.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // The integer DCT and quantization with SSE2 (libjpeg-turbo's jfdctint-sse2 and jquanti-sse2
    // approach): products as pairs by multiply-adds, the quotient by two multiply-highs
    private static void TransformBlockSse2(
        ref byte samples,
        int stride,
        ushort[] table,
        Span<short> natural)
    {
        var center = Vector128.Create((short)128);
        var zero = Vector128<byte>.Zero;
        var r0 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref samples)).AsByte(), zero).AsInt16(), center);
        var r1 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, stride))).AsByte(), zero).AsInt16(), center);
        var r2 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, 2 * stride))).AsByte(), zero).AsInt16(), center);
        var r3 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, 3 * stride))).AsByte(), zero).AsInt16(), center);
        var r4 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, 4 * stride))).AsByte(), zero).AsInt16(), center);
        var r5 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, 5 * stride))).AsByte(), zero).AsInt16(), center);
        var r6 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, 6 * stride))).AsByte(), zero).AsInt16(), center);
        var r7 = Sse2.Subtract(Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref samples, 7 * stride))).AsByte(), zero).AsInt16(), center);

        TransposeSse2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        IntegerPassSse2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, false);
        TransposeSse2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        IntegerPassSse2(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, true);

        ref ushort divisors = ref MemoryMarshal.GetArrayDataReference(table);
        ref short target = ref MemoryMarshal.GetReference(natural);
        QuantizeSse2(r0, ref divisors, 0).StoreUnsafe(ref target);
        QuantizeSse2(r1, ref divisors, 8).StoreUnsafe(ref target, 8);
        QuantizeSse2(r2, ref divisors, 16).StoreUnsafe(ref target, 16);
        QuantizeSse2(r3, ref divisors, 24).StoreUnsafe(ref target, 24);
        QuantizeSse2(r4, ref divisors, 32).StoreUnsafe(ref target, 32);
        QuantizeSse2(r5, ref divisors, 40).StoreUnsafe(ref target, 40);
        QuantizeSse2(r6, ref divisors, 48).StoreUnsafe(ref target, 48);
        QuantizeSse2(r7, ref divisors, 56).StoreUnsafe(ref target, 56);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransposeSse2(
        ref Vector128<short> r0,
        ref Vector128<short> r1,
        ref Vector128<short> r2,
        ref Vector128<short> r3,
        ref Vector128<short> r4,
        ref Vector128<short> r5,
        ref Vector128<short> r6,
        ref Vector128<short> r7)
    {
        var a0 = Sse2.UnpackLow(r0, r1).AsInt32();
        var a1 = Sse2.UnpackHigh(r0, r1).AsInt32();
        var a2 = Sse2.UnpackLow(r2, r3).AsInt32();
        var a3 = Sse2.UnpackHigh(r2, r3).AsInt32();
        var a4 = Sse2.UnpackLow(r4, r5).AsInt32();
        var a5 = Sse2.UnpackHigh(r4, r5).AsInt32();
        var a6 = Sse2.UnpackLow(r6, r7).AsInt32();
        var a7 = Sse2.UnpackHigh(r6, r7).AsInt32();
        var b0 = Sse2.UnpackLow(a0, a2).AsInt64();
        var b1 = Sse2.UnpackHigh(a0, a2).AsInt64();
        var b2 = Sse2.UnpackLow(a1, a3).AsInt64();
        var b3 = Sse2.UnpackHigh(a1, a3).AsInt64();
        var b4 = Sse2.UnpackLow(a4, a6).AsInt64();
        var b5 = Sse2.UnpackHigh(a4, a6).AsInt64();
        var b6 = Sse2.UnpackLow(a5, a7).AsInt64();
        var b7 = Sse2.UnpackHigh(a5, a7).AsInt64();
        r0 = Sse2.UnpackLow(b0, b4).AsInt16();
        r1 = Sse2.UnpackHigh(b0, b4).AsInt16();
        r2 = Sse2.UnpackLow(b1, b5).AsInt16();
        r3 = Sse2.UnpackHigh(b1, b5).AsInt16();
        r4 = Sse2.UnpackLow(b2, b6).AsInt16();
        r5 = Sse2.UnpackHigh(b2, b6).AsInt16();
        r6 = Sse2.UnpackLow(b3, b7).AsInt16();
        r7 = Sse2.UnpackHigh(b3, b7).AsInt16();
    }

    // A pass of the integer DCT with pairs of products summed by multiply-adds: the same integers as
    // jfdctint.c's, its sums of products regrouped by input
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IntegerPassSse2(
        ref Vector128<short> d0,
        ref Vector128<short> d1,
        ref Vector128<short> d2,
        ref Vector128<short> d3,
        ref Vector128<short> d4,
        ref Vector128<short> d5,
        ref Vector128<short> d6,
        ref Vector128<short> d7,
        bool second)
    {
        var tmp0 = Sse2.Add(d0, d7);
        var tmp7 = Sse2.Subtract(d0, d7);
        var tmp1 = Sse2.Add(d1, d6);
        var tmp6 = Sse2.Subtract(d1, d6);
        var tmp2 = Sse2.Add(d2, d5);
        var tmp5 = Sse2.Subtract(d2, d5);
        var tmp3 = Sse2.Add(d3, d4);
        var tmp4 = Sse2.Subtract(d3, d4);

        var tmp10 = Sse2.Add(tmp0, tmp3);
        var tmp13 = Sse2.Subtract(tmp0, tmp3);
        var tmp11 = Sse2.Add(tmp1, tmp2);
        var tmp12 = Sse2.Subtract(tmp1, tmp2);
        if (second)
        {
            var two = Vector128.Create((short)2);
            d0 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.Add(tmp10, tmp11), two), 2);
            d4 = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.Subtract(tmp10, tmp11), two), 2);
        }
        else
        {
            d0 = Sse2.ShiftLeftLogical(Sse2.Add(tmp10, tmp11), 2);
            d4 = Sse2.ShiftLeftLogical(Sse2.Subtract(tmp10, tmp11), 2);
        }

        // (tmp12, tmp13) pairs: z1 = (tmp12 + tmp13) * c541 folded into each output
        var e0 = Sse2.UnpackLow(tmp12, tmp13);
        var e1 = Sse2.UnpackHigh(tmp12, tmp13);
        d2 = Pair(e0, e1, Fix0541196100, (short)(Fix0541196100 + Fix0765366865), second);
        d6 = Pair(e0, e1, (short)(Fix0541196100 - Fix1847759065), Fix0541196100, second);

        // (tmp4, tmp5) and (tmp6, tmp7) pairs: each odd output's factors of tmp4..tmp7, with z1..z5's
        // folded in
        var p0 = Sse2.UnpackLow(tmp4, tmp5);
        var p1 = Sse2.UnpackHigh(tmp4, tmp5);
        var q0 = Sse2.UnpackLow(tmp6, tmp7);
        var q1 = Sse2.UnpackHigh(tmp6, tmp7);
        d7 = Quad(p0, p1, q0, q1, Fix0298631336 - Fix0899976223 - Fix1961570560 + Fix1175875602, Fix1175875602, Fix1175875602 - Fix1961570560, Fix1175875602 - Fix0899976223, second);
        d5 = Quad(p0, p1, q0, q1, Fix1175875602, Fix2053119869 - Fix2562915447 - Fix0390180644 + Fix1175875602, Fix1175875602 - Fix2562915447, Fix1175875602 - Fix0390180644, second);
        d3 = Quad(p0, p1, q0, q1, Fix1175875602 - Fix1961570560, Fix1175875602 - Fix2562915447, Fix3072711026 - Fix2562915447 - Fix1961570560 + Fix1175875602, Fix1175875602, second);
        d1 = Quad(p0, p1, q0, q1, Fix1175875602 - Fix0899976223, Fix1175875602 - Fix0390180644, Fix1175875602, Fix1501321110 - Fix0899976223 - Fix0390180644 + Fix1175875602, second);
    }

    // a * ca + b * cb of interleaved (a, b) pairs, rounded down by bits, as 16-bit lanes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Pair(
        Vector128<short> low,
        Vector128<short> high,
        short ca,
        short cb,
        bool second)
    {
        var factors = Vector128.Create(((int)(ushort)cb << 16) | (ushort)ca).AsInt16();
        return DescaleSse2(Sse2.MultiplyAddAdjacent(low, factors), Sse2.MultiplyAddAdjacent(high, factors), second);
    }

    // Both halves rounded down 11 bits (the first pass) or 15 (the second), and packed to 16 bits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> DescaleSse2(
        Vector128<int> low,
        Vector128<int> high,
        bool second)
    {
        if (second)
        {
            var round = Vector128.Create(1 << 14);
            return Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(low, round), 15), Sse2.ShiftRightArithmetic(Sse2.Add(high, round), 15));
        }
        else
        {
            var round = Vector128.Create(1 << 10);
            return Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(low, round), 11), Sse2.ShiftRightArithmetic(Sse2.Add(high, round), 11));
        }
    }

    // a * ca + b * cb + c * cc + d * cd of (a, b) and (c, d) pairs, rounded down by bits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Quad(
        Vector128<short> ab0,
        Vector128<short> ab1,
        Vector128<short> cd0,
        Vector128<short> cd1,
        int ca,
        int cb,
        int cc,
        int cd,
        bool second)
    {
        var f0 = Vector128.Create((cb << 16) | (ushort)(short)ca).AsInt16();
        var f1 = Vector128.Create((cd << 16) | (ushort)(short)cc).AsInt16();
        var l = Sse2.Add(Sse2.MultiplyAddAdjacent(ab0, f0), Sse2.MultiplyAddAdjacent(cd0, f1));
        var h = Sse2.Add(Sse2.MultiplyAddAdjacent(ab1, f0), Sse2.MultiplyAddAdjacent(cd1, f1));
        return DescaleSse2(l, h, second);
    }

    // jsimd_quantize_sse2 for a row: (|x| + correction) times the reciprocal, high 16 bits, times the
    // scale, high 16 bits, the sign restored
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> QuantizeSse2(
        Vector128<short> row,
        ref ushort divisors,
        int i)
    {
        var recip = Vector128.LoadUnsafe(ref divisors, (nuint)i);
        var corr = Vector128.LoadUnsafe(ref divisors, (nuint)(Corrections + i));
        var scale = Vector128.LoadUnsafe(ref divisors, (nuint)(Scales + i));
        var sign = Sse2.ShiftRightArithmetic(row, 15);
        var abs = Sse2.Subtract(Sse2.Xor(row, sign), sign).AsUInt16();
        var product = Sse2.MultiplyHigh(Sse2.MultiplyHigh(Sse2.Add(abs, corr), recip), scale).AsInt16();
        return Sse2.Subtract(Sse2.Xor(product, sign), sign);
    }
}
