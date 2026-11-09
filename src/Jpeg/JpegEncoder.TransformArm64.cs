// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1's Arm Neon code, modified by Hot Chai Games LLC: written in C#, the
// coefficients put in zigzag order as they're quantized.
//
// The forward DCTs and the quantization are ported from simd/arm/jfdctint-neon.c,
// jfdctfst-neon.c and jquanti-neon.c, and the zigzag lookup follows simd/arm/aarch64/jchuff-neon.c,
// which carry these notices:
//   Copyright (C) 2020, Arm Limited.
//   Copyright (C) 2020, 2022, 2024-2025, D. R. Commander.
//
//   This software is provided 'as-is', without any express or implied
//   warranty.  In no event will the authors be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//   3. This notice may not be removed or altered from any source distribution.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // The byte indexes that put 8 rows of coefficients in zigzag order, a zigzag row per 16: a first
    // table lookup from natural rows 0-3 (rows 4-7 for zigzag rows 4 and 7), then, for the lanes it
    // doesn't reach (255 here), a second from rows 1-4 (zigzag row 1), 3-6 (rows 2 and 5) or 4-7 (row 6)
    private static ReadOnlySpan<byte> ZigzagFirst =>
    [
        0, 1, 2, 3, 16, 17, 32, 33, 18, 19, 4, 5, 6, 7, 20, 21,
        34, 35, 48, 49, 255, 255, 50, 51, 36, 37, 22, 23, 8, 9, 10, 11,
        24, 25, 38, 39, 52, 53, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        54, 55, 40, 41, 26, 27, 12, 13, 14, 15, 28, 29, 42, 43, 56, 57,
        6, 7, 20, 21, 34, 35, 48, 49, 50, 51, 36, 37, 22, 23, 8, 9,
        58, 59, 44, 45, 30, 31, 46, 47, 60, 61, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 62, 63, 255, 255, 255, 255,
        42, 43, 56, 57, 58, 59, 44, 45, 30, 31, 46, 47, 60, 61, 62, 63,
    ];

    private static ReadOnlySpan<byte> ZigzagSecond =>
    [
        255, 255, 255, 255, 48, 49, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 18, 19, 32, 33, 48, 49, 34, 35, 20, 21,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 26, 27, 40, 41, 54, 55,
        52, 53, 54, 55, 40, 41, 26, 27, 12, 13, 255, 255, 14, 15, 28, 29,
    ];

    // jsimd_convsamp_neon, jsimd_fdct_islow_neon (or the fast DCT), jsimd_quantize_neon, and the zigzag
    // order by table lookups
    private static void TransformBlockArm64(
        ref byte samples,
        int stride,
        ushort[] table,
        bool fast,
        ref short output)
    {
        if (fast)
        {
            TransformBlockFastArm64(ref samples, stride, table, ref output);
            return;
        }

        var center = Vector64.Create((byte)128);
        var r0 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples), center).AsInt16();
        var r1 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)stride), center).AsInt16();
        var r2 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(2 * stride)), center).AsInt16();
        var r3 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(3 * stride)), center).AsInt16();
        var r4 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(4 * stride)), center).AsInt16();
        var r5 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(5 * stride)), center).AsInt16();
        var r6 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(6 * stride)), center).AsInt16();
        var r7 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(7 * stride)), center).AsInt16();

        // Each pass after a transpose: the first has the columns as vectors, so it works on all the
        // rows at once, and the second the rows. A pass of jsimd_fdct_islow_neon: adds in 16 bits,
        // products in 32, rounded down 11 bits (13 - 2) in the first pass and 15 (13 + 2) in the
        // second, which also rounds the even part's first outputs down 2 bits instead of shifting
        // them up
        for (int pass = 0; pass < 2; pass++)
        {
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            var tmp0 = AdvSimd.Add(r0, r7);
            var tmp7 = AdvSimd.Subtract(r0, r7);
            var tmp1 = AdvSimd.Add(r1, r6);
            var tmp6 = AdvSimd.Subtract(r1, r6);
            var tmp2 = AdvSimd.Add(r2, r5);
            var tmp5 = AdvSimd.Subtract(r2, r5);
            var tmp3 = AdvSimd.Add(r3, r4);
            var tmp4 = AdvSimd.Subtract(r3, r4);

            var tmp10 = AdvSimd.Add(tmp0, tmp3);
            var tmp13 = AdvSimd.Subtract(tmp0, tmp3);
            var tmp11 = AdvSimd.Add(tmp1, tmp2);
            var tmp12 = AdvSimd.Subtract(tmp1, tmp2);
            if (pass != 0)
            {
                r0 = AdvSimd.ShiftRightArithmeticRounded(AdvSimd.Add(tmp10, tmp11), 2);
                r4 = AdvSimd.ShiftRightArithmeticRounded(AdvSimd.Subtract(tmp10, tmp11), 2);
            }
            else
            {
                r0 = AdvSimd.ShiftLeftLogical(AdvSimd.Add(tmp10, tmp11), 2);
                r4 = AdvSimd.ShiftLeftLogical(AdvSimd.Subtract(tmp10, tmp11), 2);
            }

            var sum1213 = AdvSimd.Add(tmp12, tmp13);
            var z1l = MultiplyLow(sum1213, Fix0541196100);
            var z1h = MultiplyHigh(sum1213, Fix0541196100);
            r2 = Descale(MultiplyAddLow(z1l, tmp13, Fix0765366865), MultiplyAddHigh(z1h, tmp13, Fix0765366865), pass != 0);
            r6 = Descale(MultiplyAddLow(z1l, tmp12, -Fix1847759065), MultiplyAddHigh(z1h, tmp12, -Fix1847759065), pass != 0);

            var z1 = AdvSimd.Add(tmp4, tmp7);
            var z2 = AdvSimd.Add(tmp5, tmp6);
            var z3 = AdvSimd.Add(tmp4, tmp6);
            var z4 = AdvSimd.Add(tmp5, tmp7);
            var z5l = MultiplyAddLow(MultiplyLow(z3, Fix1175875602), z4, Fix1175875602);
            var z5h = MultiplyAddHigh(MultiplyHigh(z3, Fix1175875602), z4, Fix1175875602);

            var tmp4l = MultiplyLow(tmp4, Fix0298631336);
            var tmp4h = MultiplyHigh(tmp4, Fix0298631336);
            var tmp5l = MultiplyLow(tmp5, Fix2053119869);
            var tmp5h = MultiplyHigh(tmp5, Fix2053119869);
            var tmp6l = MultiplyLow(tmp6, Fix3072711026);
            var tmp6h = MultiplyHigh(tmp6, Fix3072711026);
            var tmp7l = MultiplyLow(tmp7, Fix1501321110);
            var tmp7h = MultiplyHigh(tmp7, Fix1501321110);

            var z1l2 = MultiplyLow(z1, -Fix0899976223);
            var z1h2 = MultiplyHigh(z1, -Fix0899976223);
            var z2l = MultiplyLow(z2, -Fix2562915447);
            var z2h = MultiplyHigh(z2, -Fix2562915447);
            var z3l = AdvSimd.Add(MultiplyLow(z3, -Fix1961570560), z5l);
            var z3h = AdvSimd.Add(MultiplyHigh(z3, -Fix1961570560), z5h);
            var z4l = AdvSimd.Add(MultiplyLow(z4, -Fix0390180644), z5l);
            var z4h = AdvSimd.Add(MultiplyHigh(z4, -Fix0390180644), z5h);

            r7 = Descale(AdvSimd.Add(AdvSimd.Add(tmp4l, z1l2), z3l), AdvSimd.Add(AdvSimd.Add(tmp4h, z1h2), z3h), pass != 0);
            r5 = Descale(AdvSimd.Add(AdvSimd.Add(tmp5l, z2l), z4l), AdvSimd.Add(AdvSimd.Add(tmp5h, z2h), z4h), pass != 0);
            r3 = Descale(AdvSimd.Add(AdvSimd.Add(tmp6l, z2l), z3l), AdvSimd.Add(AdvSimd.Add(tmp6h, z2h), z3h), pass != 0);
            r1 = Descale(AdvSimd.Add(AdvSimd.Add(tmp7l, z1l2), z4l), AdvSimd.Add(AdvSimd.Add(tmp7h, z1h2), z4h), pass != 0);
        }

        QuantizeArm64(r0, r1, r2, r3, r4, r5, r6, r7, table, ref output);
    }

    // jsimd_convsamp_neon, jsimd_fdct_ifast_neon and jsimd_quantize_neon
    private static void TransformBlockFastArm64(
        ref byte samples,
        int stride,
        ushort[] table,
        ref short output)
    {
        var center = Vector64.Create((byte)128);
        var r0 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples), center).AsInt16();
        var r1 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)stride), center).AsInt16();
        var r2 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(2 * stride)), center).AsInt16();
        var r3 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(3 * stride)), center).AsInt16();
        var r4 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(4 * stride)), center).AsInt16();
        var r5 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(5 * stride)), center).AsInt16();
        var r6 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(6 * stride)), center).AsInt16();
        var r7 = AdvSimd.SubtractWideningLower(Vector64.LoadUnsafe(ref samples, (nuint)(7 * stride)), center).AsInt16();
        for (int pass = 0; pass < 2; pass++)
        {
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            FastPassArm64(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        }

        QuantizeArm64(r0, r1, r2, r3, r4, r5, r6, r7, table, ref output);
    }

    // jsimd_quantize_neon of a block's rows, then the coefficients in zigzag order by table lookups
    private static void QuantizeArm64(
        Vector128<short> r0,
        Vector128<short> r1,
        Vector128<short> r2,
        Vector128<short> r3,
        Vector128<short> r4,
        Vector128<short> r5,
        Vector128<short> r6,
        Vector128<short> r7,
        ushort[] table,
        ref short output)
    {
        ref ushort divisors = ref MemoryMarshal.GetArrayDataReference(table);
        r0 = QuantizeArm64(r0, ref divisors, 0);
        r1 = QuantizeArm64(r1, ref divisors, 8);
        r2 = QuantizeArm64(r2, ref divisors, 16);
        r3 = QuantizeArm64(r3, ref divisors, 24);
        r4 = QuantizeArm64(r4, ref divisors, 32);
        r5 = QuantizeArm64(r5, ref divisors, 40);
        r6 = QuantizeArm64(r6, ref divisors, 48);
        r7 = QuantizeArm64(r7, ref divisors, 56);

        // Zigzag order: each zigzag row looked up in four natural rows, and the lanes they don't reach
        // in four others
        var b0 = r0.AsByte();
        var b1 = r1.AsByte();
        var b2 = r2.AsByte();
        var b3 = r3.AsByte();
        var b4 = r4.AsByte();
        var b5 = r5.AsByte();
        var b6 = r6.AsByte();
        var b7 = r7.AsByte();
        ref byte first = ref MemoryMarshal.GetReference(ZigzagFirst);
        ref byte second = ref MemoryMarshal.GetReference(ZigzagSecond);
        var low = (b0, b1, b2, b3);
        var high = (b4, b5, b6, b7);
        var z0 = AdvSimd.Arm64.VectorTableLookup(low, Vector128.LoadUnsafe(ref first));
        var z1 = AdvSimd.Arm64.VectorTableLookup(low, Vector128.LoadUnsafe(ref first, 16));
        z1 = AdvSimd.Arm64.VectorTableLookupExtension(z1, (b1, b2, b3, b4), Vector128.LoadUnsafe(ref second));
        var z2 = AdvSimd.Arm64.VectorTableLookup(low, Vector128.LoadUnsafe(ref first, 32));
        z2 = AdvSimd.Arm64.VectorTableLookupExtension(z2, (b3, b4, b5, b6), Vector128.LoadUnsafe(ref second, 16));
        var z3 = AdvSimd.Arm64.VectorTableLookup(low, Vector128.LoadUnsafe(ref first, 48));
        var z4 = AdvSimd.Arm64.VectorTableLookup(high, Vector128.LoadUnsafe(ref first, 64));
        var z5 = AdvSimd.Arm64.VectorTableLookup(low, Vector128.LoadUnsafe(ref first, 80));
        z5 = AdvSimd.Arm64.VectorTableLookupExtension(z5, (b3, b4, b5, b6), Vector128.LoadUnsafe(ref second, 32));
        var z6 = AdvSimd.Arm64.VectorTableLookup(low, Vector128.LoadUnsafe(ref first, 96));
        z6 = AdvSimd.Arm64.VectorTableLookupExtension(z6, high, Vector128.LoadUnsafe(ref second, 48));
        var z7 = AdvSimd.Arm64.VectorTableLookup(high, Vector128.LoadUnsafe(ref first, 112));
        z0.AsInt16().StoreUnsafe(ref output);
        z1.AsInt16().StoreUnsafe(ref output, 8);
        z2.AsInt16().StoreUnsafe(ref output, 16);
        z3.AsInt16().StoreUnsafe(ref output, 24);
        z4.AsInt16().StoreUnsafe(ref output, 32);
        z5.AsInt16().StoreUnsafe(ref output, 40);
        z6.AsInt16().StoreUnsafe(ref output, 48);
        z7.AsInt16().StoreUnsafe(ref output, 56);
    }

    // An 8 x 8 transpose of 16-bit lanes: 16-, 32- and 64-bit transposes of pairs
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector128<short> r0,
        ref Vector128<short> r1,
        ref Vector128<short> r2,
        ref Vector128<short> r3,
        ref Vector128<short> r4,
        ref Vector128<short> r5,
        ref Vector128<short> r6,
        ref Vector128<short> r7)
    {
        var t0 = AdvSimd.Arm64.TransposeEven(r0, r1).AsInt32();
        var t1 = AdvSimd.Arm64.TransposeOdd(r0, r1).AsInt32();
        var t2 = AdvSimd.Arm64.TransposeEven(r2, r3).AsInt32();
        var t3 = AdvSimd.Arm64.TransposeOdd(r2, r3).AsInt32();
        var t4 = AdvSimd.Arm64.TransposeEven(r4, r5).AsInt32();
        var t5 = AdvSimd.Arm64.TransposeOdd(r4, r5).AsInt32();
        var t6 = AdvSimd.Arm64.TransposeEven(r6, r7).AsInt32();
        var t7 = AdvSimd.Arm64.TransposeOdd(r6, r7).AsInt32();
        var u0 = AdvSimd.Arm64.TransposeEven(t0, t2).AsInt64();
        var u2 = AdvSimd.Arm64.TransposeOdd(t0, t2).AsInt64();
        var u1 = AdvSimd.Arm64.TransposeEven(t1, t3).AsInt64();
        var u3 = AdvSimd.Arm64.TransposeOdd(t1, t3).AsInt64();
        var u4 = AdvSimd.Arm64.TransposeEven(t4, t6).AsInt64();
        var u6 = AdvSimd.Arm64.TransposeOdd(t4, t6).AsInt64();
        var u5 = AdvSimd.Arm64.TransposeEven(t5, t7).AsInt64();
        var u7 = AdvSimd.Arm64.TransposeOdd(t5, t7).AsInt64();
        r0 = AdvSimd.Arm64.TransposeEven(u0, u4).AsInt16();
        r4 = AdvSimd.Arm64.TransposeOdd(u0, u4).AsInt16();
        r1 = AdvSimd.Arm64.TransposeEven(u1, u5).AsInt16();
        r5 = AdvSimd.Arm64.TransposeOdd(u1, u5).AsInt16();
        r2 = AdvSimd.Arm64.TransposeEven(u2, u6).AsInt16();
        r6 = AdvSimd.Arm64.TransposeOdd(u2, u6).AsInt16();
        r3 = AdvSimd.Arm64.TransposeEven(u3, u7).AsInt16();
        r7 = AdvSimd.Arm64.TransposeOdd(u3, u7).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MultiplyLow(
        Vector128<short> value,
        short constant) =>
        AdvSimd.MultiplyWideningLower(value.GetLower(), Vector64.Create(constant));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MultiplyHigh(
        Vector128<short> value,
        short constant) =>
        AdvSimd.MultiplyWideningUpper(value, Vector128.Create(constant));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MultiplyAddLow(
        Vector128<int> sum,
        Vector128<short> value,
        short constant) =>
        AdvSimd.MultiplyWideningLowerAndAdd(sum, value.GetLower(), Vector64.Create(constant));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MultiplyAddHigh(
        Vector128<int> sum,
        Vector128<short> value,
        short constant) =>
        AdvSimd.MultiplyWideningUpperAndAdd(sum, value, Vector128.Create(constant));

    // vrshrn_n_s32 of both halves, by 11 bits (the first pass) or 15 (the second), each shift an
    // immediate
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Descale(
        Vector128<int> low,
        Vector128<int> high,
        bool second) =>
        second
            ? AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(low, 15), high, 15)
            : AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(low, 11), high, 11);

    // A pass of jsimd_fdct_ifast_neon: 16-bit arithmetic, the products by doubling multiply-highs of
    // the constants in 15 fraction bits (1.306562965 as 0.306562965 plus the value)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FastPassArm64(
        ref Vector128<short> d0,
        ref Vector128<short> d1,
        ref Vector128<short> d2,
        ref Vector128<short> d3,
        ref Vector128<short> d4,
        ref Vector128<short> d5,
        ref Vector128<short> d6,
        ref Vector128<short> d7)
    {
        var c0382 = Vector128.Create((short)(FastFix0382683433 * 128));
        var c0541 = Vector128.Create((short)(FastFix0541196100 * 128));
        var c0707 = Vector128.Create((short)(FastFix0707106781 * 128));
        var c0306 = Vector128.Create((short)((FastFix1306562965 - 256) * 128));
        var tmp0 = AdvSimd.Add(d0, d7);
        var tmp7 = AdvSimd.Subtract(d0, d7);
        var tmp1 = AdvSimd.Add(d1, d6);
        var tmp6 = AdvSimd.Subtract(d1, d6);
        var tmp2 = AdvSimd.Add(d2, d5);
        var tmp5 = AdvSimd.Subtract(d2, d5);
        var tmp3 = AdvSimd.Add(d3, d4);
        var tmp4 = AdvSimd.Subtract(d3, d4);

        var tmp10 = AdvSimd.Add(tmp0, tmp3);
        var tmp13 = AdvSimd.Subtract(tmp0, tmp3);
        var tmp11 = AdvSimd.Add(tmp1, tmp2);
        var tmp12 = AdvSimd.Subtract(tmp1, tmp2);
        d0 = AdvSimd.Add(tmp10, tmp11);
        d4 = AdvSimd.Subtract(tmp10, tmp11);
        var z1 = AdvSimd.MultiplyDoublingSaturateHigh(AdvSimd.Add(tmp12, tmp13), c0707);
        d2 = AdvSimd.Add(tmp13, z1);
        d6 = AdvSimd.Subtract(tmp13, z1);

        tmp10 = AdvSimd.Add(tmp4, tmp5);
        tmp11 = AdvSimd.Add(tmp5, tmp6);
        tmp12 = AdvSimd.Add(tmp6, tmp7);
        var z5 = AdvSimd.MultiplyDoublingSaturateHigh(AdvSimd.Subtract(tmp10, tmp12), c0382);
        var z2 = AdvSimd.Add(AdvSimd.MultiplyDoublingSaturateHigh(tmp10, c0541), z5);
        var z4 = AdvSimd.MultiplyDoublingSaturateHigh(tmp12, c0306);
        z4 = AdvSimd.Add(z4, AdvSimd.Add(tmp12, z5));
        var z3 = AdvSimd.MultiplyDoublingSaturateHigh(tmp11, c0707);
        var z11 = AdvSimd.Add(tmp7, z3);
        var z13 = AdvSimd.Subtract(tmp7, z3);
        d5 = AdvSimd.Add(z13, z2);
        d3 = AdvSimd.Subtract(z13, z2);
        d1 = AdvSimd.Add(z11, z4);
        d7 = AdvSimd.Subtract(z11, z4);
    }

    // jsimd_quantize_neon for a row: the absolute value plus the correction, times the reciprocal, the
    // high 16 bits shifted right, the sign restored
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> QuantizeArm64(
        Vector128<short> row,
        ref ushort divisors,
        int i)
    {
        var recip = Vector128.LoadUnsafe(ref divisors, (nuint)i);
        var corr = Vector128.LoadUnsafe(ref divisors, (nuint)(Corrections + i));
        var shift = Vector128.LoadUnsafe(ref divisors, (nuint)(NegatedShifts + i)).AsInt16();
        var sign = AdvSimd.ShiftRightArithmetic(row, 15);
        var abs = AdvSimd.Add(AdvSimd.Abs(row).AsUInt16(), corr);
        var low = AdvSimd.MultiplyWideningLower(abs.GetLower(), recip.GetLower());
        var high = AdvSimd.MultiplyWideningUpper(abs, recip);
        var product = AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(low, 16), high, 16);
        var quotient = AdvSimd.ShiftLogical(product, shift).AsInt16();
        return AdvSimd.Subtract(AdvSimd.Xor(quotient, sign), sign);
    }
}
