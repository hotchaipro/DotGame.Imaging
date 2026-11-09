// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Part of the JPEG decoder (JpegDecoder.cs). The decoder's port notes and notices:
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, the whole image decoded
// at once and output a row at a time, and the Huffman decoding, vector forms and buffer handling new.
//
// The inverse DCT's arithmetic, the upsampling, the color conversion, the range limiting, the AC
// refinement, the color space guess, the block smoothing rule and the context rows follow jidctint.c,
// jdsample.c, jdcolor.c, jdcolext.c, jdmaster.c, jdphuff.c, jdhuff.c, jdmainct.c, jdcoefct.c and
// jdapimin.c, which carry these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1998, Thomas G. Lane.
//   Modified 2002-2018 by Guido Vollbeding.
//   Lossless JPEG Modifications: Copyright (C) 1999, Ken Murchison.
//   libjpeg-turbo Modifications:
//   Copyright 2009 Pierre Ossman <ossman@cendio.se> for Cendio AB
//   Copyright (C) 2009-2026, D. R. Commander.
//   Copyright (C) 2013, Linaro Limited.
//   Copyright (C) 2015, 2020, Google, Inc.
//   Copyright (C) 2018, Matthias Räncker.
//   Copyright (C) 2019-2020, Arm Limited.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.
//
// The Arm64 inverse DCT is ported from simd/arm/jidctint-neon.c, which carries this notice:
//   Copyright (C) 2020, Arm Limited.
//   Copyright (C) 2020, 2024-2025, D. R. Commander.
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

internal static partial class JpegDecoder
{
    // The accurate integer IDCT: jidctint.c's jpeg_idct_islow as libjpeg-turbo's NEON version
    // (simd/arm/jidctint-neon.c) computes it, which libjpeg-turbo builds on Arm64: the same
    // arithmetic, with its products regrouped (exactly, in integers) and its intermediate values in
    // 16 bits, rounded on the way down and saturated at the end, where the C wraps out-of-range
    // values (they differ only for coefficients no encoder makes). On Arm64 it is that code; other
    // machines do the same arithmetic in 32-bit lanes with the same 16-bit truncations, so every
    // build machine makes the same samples.
    private static class Idct
    {
        private const int ConstBits = 13;
        private const int Pass1Bits = 2;
        private const int DescaleP1 = ConstBits - Pass1Bits;
        private const int DescaleP2 = ConstBits + Pass1Bits + 3;

        private const short F0298 = 2446;
        private const short F0390 = 3196;
        private const short F0541 = 4433;
        private const short F0765 = 6270;
        private const short F0899 = 7373;
        private const short F1175 = 9633;
        private const short F1501 = 12299;
        private const short F1847 = 15137;
        private const short F1961 = 16069;
        private const short F2053 = 16819;
        private const short F2562 = 20995;
        private const short F3072 = 25172;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Islow(
            ReadOnlySpan<short> coefficients,
            short[] quant,
            Span<byte> output,
            int stride)
        {
            ref short c = ref MemoryMarshal.GetReference(coefficients);
            ref short q = ref MemoryMarshal.GetArrayDataReference(quant);

            // A block with only its DC term: every sample is the same, as the NEON code's sparse
            // passes give: dc = (coefficient * quant) << 2 in 16 bits, then saturated
            // ((dc >> 3) + 2) >> 2, plus 128
            var ac = Vector128.LoadUnsafe(ref c, 8) | Vector128.LoadUnsafe(ref c, 16) | Vector128.LoadUnsafe(ref c, 24) |
                Vector128.LoadUnsafe(ref c, 32) | Vector128.LoadUnsafe(ref c, 40) | Vector128.LoadUnsafe(ref c, 48) | Vector128.LoadUnsafe(ref c, 56) |
                (Vector128.LoadUnsafe(ref c) & Vector128.Create(0, -1, -1, -1, -1, -1, -1, -1).AsInt16());
            if (ac == Vector128<short>.Zero)
            {
                short dc = (short)((short)(c * q) << Pass1Bits);
                int value = Math.Clamp(((dc >> 3) + 2) >> 2, -128, 127) + 128;
                ulong fill = (ulong)value * 0x0101010101010101UL;
                ref byte o = ref MemoryMarshal.GetReference(output);
                for (int row = 0; row < 8; row++)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, row * stride), fill);
                }

                return;
            }

            if (AdvSimd.Arm64.IsSupported)
            {
                Neon(ref c, ref q, output, stride);
            }
            else
            {
                Portable(ref c, ref q, output, stride);
            }
        }

        // libjpeg-turbo's constants, in its order: k0 = (0.899, 0.541, 2.562, 0.298 - 0.899),
        // k1 = (1.501 - 0.899, 2.053 - 2.562, 0.541 + 0.765, 1.175),
        // k2 = (1.175 - 0.390, 0.541 - 1.847, 3.072 - 2.562, 1.175 - 1.961)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Neon(
            ref short c,
            ref short q,
            Span<byte> output,
            int stride)
        {
            var k0 = Vector64.Create(F0899, F0541, F2562, (short)(F0298 - F0899));
            var k1 = Vector64.Create((short)(F1501 - F0899), (short)(F2053 - F2562), (short)(F0541 + F0765), F1175);
            var k2 = Vector64.Create((short)(F1175 - F0390), (short)(F0541 - F1847), (short)(F3072 - F2562), (short)(F1175 - F1961));

            // Pass 1: the rows of coefficients dequantized in 16 bits, each pass's products in 32
            Vector128<short> Row(ref short c, ref short q, int r) => Vector128.LoadUnsafe(ref c, (nuint)(r * 8)) * Vector128.LoadUnsafe(ref q, (nuint)(r * 8));
            var r0 = Row(ref c, ref q, 0);
            var r1 = Row(ref c, ref q, 1);
            var r2 = Row(ref c, ref q, 2);
            var r3 = Row(ref c, ref q, 3);
            var r4 = Row(ref c, ref q, 4);
            var r5 = Row(ref c, ref q, 5);
            var r6 = Row(ref c, ref q, 6);
            var r7 = Row(ref c, ref q, 7);

            Pass(r0, r1, r2, r3, r4, r5, r6, r7, k0, k1, k2, upper: false,
                out var l10, out var l11, out var l12, out var l13, out var l0, out var l1, out var l2, out var l3);
            Pass(r0, r1, r2, r3, r4, r5, r6, r7, k0, k1, k2, upper: true,
                out var h10, out var h11, out var h12, out var h13, out var h0, out var h1, out var h2, out var h3);
            var w0 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l10 + l3, DescaleP1), h10 + h3, DescaleP1);
            var w1 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l11 + l2, DescaleP1), h11 + h2, DescaleP1);
            var w2 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l12 + l1, DescaleP1), h12 + h1, DescaleP1);
            var w3 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l13 + l0, DescaleP1), h13 + h0, DescaleP1);
            var w4 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l13 - l0, DescaleP1), h13 - h0, DescaleP1);
            var w5 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l12 - l1, DescaleP1), h12 - h1, DescaleP1);
            var w6 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l11 - l2, DescaleP1), h11 - h2, DescaleP1);
            var w7 = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(l10 - l3, DescaleP1), h10 - h3, DescaleP1);

            // Rows to columns: column k of the workspace, rows 0 to 7 in the lanes
            Transpose8(ref w0, ref w1, ref w2, ref w3, ref w4, ref w5, ref w6, ref w7);

            // Pass 2 over the columns: each output sample k for the eight rows
            Pass(w0, w1, w2, w3, w4, w5, w6, w7, k0, k1, k2, upper: false,
                out l10, out l11, out l12, out l13, out l0, out l1, out l2, out l3);
            Pass(w0, w1, w2, w3, w4, w5, w6, w7, k0, k1, k2, upper: true,
                out h10, out h11, out h12, out h13, out h0, out h1, out h2, out h3);
            var p0 = Samples(AdvSimd.AddHighNarrowingUpper(AdvSimd.AddHighNarrowingLower(l10, l3), h10, h3));
            var p1 = Samples(AdvSimd.AddHighNarrowingUpper(AdvSimd.AddHighNarrowingLower(l11, l2), h11, h2));
            var p2 = Samples(AdvSimd.AddHighNarrowingUpper(AdvSimd.AddHighNarrowingLower(l12, l1), h12, h1));
            var p3 = Samples(AdvSimd.AddHighNarrowingUpper(AdvSimd.AddHighNarrowingLower(l13, l0), h13, h0));
            var p4 = Samples(AdvSimd.SubtractHighNarrowingUpper(AdvSimd.SubtractHighNarrowingLower(l13, l0), h13, h0));
            var p5 = Samples(AdvSimd.SubtractHighNarrowingUpper(AdvSimd.SubtractHighNarrowingLower(l12, l1), h12, h1));
            var p6 = Samples(AdvSimd.SubtractHighNarrowingUpper(AdvSimd.SubtractHighNarrowingLower(l11, l2), h11, h2));
            var p7 = Samples(AdvSimd.SubtractHighNarrowingUpper(AdvSimd.SubtractHighNarrowingLower(l10, l3), h10, h3));

            // Samples k of the rows to rows of samples
            var a0 = AdvSimd.Arm64.TransposeEven(p0, p1);
            var b0 = AdvSimd.Arm64.TransposeOdd(p0, p1);
            var a1 = AdvSimd.Arm64.TransposeEven(p2, p3);
            var b1 = AdvSimd.Arm64.TransposeOdd(p2, p3);
            var a2 = AdvSimd.Arm64.TransposeEven(p4, p5);
            var b2 = AdvSimd.Arm64.TransposeOdd(p4, p5);
            var a3 = AdvSimd.Arm64.TransposeEven(p6, p7);
            var b3 = AdvSimd.Arm64.TransposeOdd(p6, p7);
            var c0 = AdvSimd.Arm64.TransposeEven(a0.AsUInt16(), a1.AsUInt16());
            var c2 = AdvSimd.Arm64.TransposeOdd(a0.AsUInt16(), a1.AsUInt16());
            var d0 = AdvSimd.Arm64.TransposeEven(b0.AsUInt16(), b1.AsUInt16());
            var d2 = AdvSimd.Arm64.TransposeOdd(b0.AsUInt16(), b1.AsUInt16());
            var c1 = AdvSimd.Arm64.TransposeEven(a2.AsUInt16(), a3.AsUInt16());
            var c3 = AdvSimd.Arm64.TransposeOdd(a2.AsUInt16(), a3.AsUInt16());
            var d1 = AdvSimd.Arm64.TransposeEven(b2.AsUInt16(), b3.AsUInt16());
            var d3 = AdvSimd.Arm64.TransposeOdd(b2.AsUInt16(), b3.AsUInt16());
            ref byte o = ref MemoryMarshal.GetReference(output);
            Unsafe.WriteUnaligned(ref o, AdvSimd.Arm64.TransposeEven(c0.AsUInt32(), c1.AsUInt32()).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, stride), AdvSimd.Arm64.TransposeEven(d0.AsUInt32(), d1.AsUInt32()).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 2 * stride), AdvSimd.Arm64.TransposeEven(c2.AsUInt32(), c3.AsUInt32()).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 3 * stride), AdvSimd.Arm64.TransposeEven(d2.AsUInt32(), d3.AsUInt32()).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 4 * stride), AdvSimd.Arm64.TransposeOdd(c0.AsUInt32(), c1.AsUInt32()).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 5 * stride), AdvSimd.Arm64.TransposeOdd(d0.AsUInt32(), d1.AsUInt32()).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 6 * stride), AdvSimd.Arm64.TransposeOdd(c2.AsUInt32(), c3.AsUInt32()).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 7 * stride), AdvSimd.Arm64.TransposeOdd(d2.AsUInt32(), d3.AsUInt32()).AsUInt64().ToScalar());
        }

        // The pass's even and odd parts for columns 0 to 3 (or 4 to 7, upper) of 16-bit inputs 0 to 7
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Pass(
            Vector128<short> i0, Vector128<short> i1, Vector128<short> i2, Vector128<short> i3,
            Vector128<short> i4, Vector128<short> i5, Vector128<short> i6, Vector128<short> i7,
            Vector64<short> k0, Vector64<short> k1, Vector64<short> k2, bool upper,
            out Vector128<int> tmp10, out Vector128<int> tmp11, out Vector128<int> tmp12, out Vector128<int> tmp13,
            out Vector128<int> tmp0, out Vector128<int> tmp1, out Vector128<int> tmp2, out Vector128<int> tmp3)
        {
            // Even part: tmp2 = z2 * 0.541 + z3 * (0.541 - 1.847), tmp3 = z2 * (0.541 + 0.765) + z3 * 0.541
            var sum04 = i0 + i4;
            var difference04 = i0 - i4;
            Vector128<int> even2, even3, even0, even1;
            if (!upper)
            {
                even2 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndAdd(AdvSimd.MultiplyBySelectedScalarWideningLower(i2.GetLower(), k0, 1), i6.GetLower(), k2, 1);
                even3 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndAdd(AdvSimd.MultiplyBySelectedScalarWideningLower(i2.GetLower(), k1, 2), i6.GetLower(), k0, 1);
                even0 = AdvSimd.ShiftLeftLogicalWideningLower(sum04.GetLower(), ConstBits);
                even1 = AdvSimd.ShiftLeftLogicalWideningLower(difference04.GetLower(), ConstBits);
            }
            else
            {
                even2 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndAdd(AdvSimd.MultiplyBySelectedScalarWideningUpper(i2, k0, 1), i6, k2, 1);
                even3 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndAdd(AdvSimd.MultiplyBySelectedScalarWideningUpper(i2, k1, 2), i6, k0, 1);
                even0 = AdvSimd.ShiftLeftLogicalWideningUpper(sum04, ConstBits);
                even1 = AdvSimd.ShiftLeftLogicalWideningUpper(difference04, ConstBits);
            }

            tmp10 = even0 + even3;
            tmp13 = even0 - even3;
            tmp11 = even1 + even2;
            tmp12 = even1 - even2;

            // Odd part: inputs 7, 5, 3 and 1
            var z3s = i7 + i3;
            var z4s = i5 + i1;
            Vector128<int> z3, z4;
            if (!upper)
            {
                z3 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndAdd(AdvSimd.MultiplyBySelectedScalarWideningLower(z3s.GetLower(), k2, 3), z4s.GetLower(), k1, 3);
                z4 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndAdd(AdvSimd.MultiplyBySelectedScalarWideningLower(z3s.GetLower(), k1, 3), z4s.GetLower(), k2, 0);
                tmp0 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningLower(i7.GetLower(), k0, 3), i1.GetLower(), k0, 0);
                tmp1 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningLower(i5.GetLower(), k1, 1), i3.GetLower(), k0, 2);
                tmp2 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningLower(i3.GetLower(), k2, 2), i5.GetLower(), k0, 2);
                tmp3 = AdvSimd.MultiplyBySelectedScalarWideningLowerAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningLower(i1.GetLower(), k1, 0), i7.GetLower(), k0, 0);
            }
            else
            {
                z3 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndAdd(AdvSimd.MultiplyBySelectedScalarWideningUpper(z3s, k2, 3), z4s, k1, 3);
                z4 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndAdd(AdvSimd.MultiplyBySelectedScalarWideningUpper(z3s, k1, 3), z4s, k2, 0);
                tmp0 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningUpper(i7, k0, 3), i1, k0, 0);
                tmp1 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningUpper(i5, k1, 1), i3, k0, 2);
                tmp2 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningUpper(i3, k2, 2), i5, k0, 2);
                tmp3 = AdvSimd.MultiplyBySelectedScalarWideningUpperAndSubtract(AdvSimd.MultiplyBySelectedScalarWideningUpper(i1, k1, 0), i7, k0, 0);
            }

            tmp0 += z3;
            tmp1 += z4;
            tmp2 += z3;
            tmp3 += z4;
        }

        // Eight shorts' high halves, rounded right by 2 with saturation to a signed byte, plus 128
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector64<byte> Samples(
            Vector128<short> high) =>
            AdvSimd.ShiftRightArithmeticRoundedNarrowingSaturateLower(high, DescaleP2 - 16).AsByte() + Vector64.Create((byte)128);

        // An 8 x 8 transpose of shorts in eight vectors
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transpose8(
            ref Vector128<short> r0, ref Vector128<short> r1, ref Vector128<short> r2, ref Vector128<short> r3,
            ref Vector128<short> r4, ref Vector128<short> r5, ref Vector128<short> r6, ref Vector128<short> r7)
        {
            var t0 = AdvSimd.Arm64.TransposeEven(r0, r1);
            var t1 = AdvSimd.Arm64.TransposeOdd(r0, r1);
            var t2 = AdvSimd.Arm64.TransposeEven(r2, r3);
            var t3 = AdvSimd.Arm64.TransposeOdd(r2, r3);
            var t4 = AdvSimd.Arm64.TransposeEven(r4, r5);
            var t5 = AdvSimd.Arm64.TransposeOdd(r4, r5);
            var t6 = AdvSimd.Arm64.TransposeEven(r6, r7);
            var t7 = AdvSimd.Arm64.TransposeOdd(r6, r7);
            var u0 = AdvSimd.Arm64.TransposeEven(t0.AsInt32(), t2.AsInt32());
            var u2 = AdvSimd.Arm64.TransposeOdd(t0.AsInt32(), t2.AsInt32());
            var u1 = AdvSimd.Arm64.TransposeEven(t1.AsInt32(), t3.AsInt32());
            var u3 = AdvSimd.Arm64.TransposeOdd(t1.AsInt32(), t3.AsInt32());
            var u4 = AdvSimd.Arm64.TransposeEven(t4.AsInt32(), t6.AsInt32());
            var u6 = AdvSimd.Arm64.TransposeOdd(t4.AsInt32(), t6.AsInt32());
            var u5 = AdvSimd.Arm64.TransposeEven(t5.AsInt32(), t7.AsInt32());
            var u7 = AdvSimd.Arm64.TransposeOdd(t5.AsInt32(), t7.AsInt32());
            r0 = AdvSimd.Arm64.TransposeEven(u0.AsInt64(), u4.AsInt64()).AsInt16();
            r4 = AdvSimd.Arm64.TransposeOdd(u0.AsInt64(), u4.AsInt64()).AsInt16();
            r1 = AdvSimd.Arm64.TransposeEven(u1.AsInt64(), u5.AsInt64()).AsInt16();
            r5 = AdvSimd.Arm64.TransposeOdd(u1.AsInt64(), u5.AsInt64()).AsInt16();
            r2 = AdvSimd.Arm64.TransposeEven(u2.AsInt64(), u6.AsInt64()).AsInt16();
            r6 = AdvSimd.Arm64.TransposeOdd(u2.AsInt64(), u6.AsInt64()).AsInt16();
            r3 = AdvSimd.Arm64.TransposeEven(u3.AsInt64(), u7.AsInt64()).AsInt16();
            r7 = AdvSimd.Arm64.TransposeOdd(u3.AsInt64(), u7.AsInt64()).AsInt16();
        }

        // The same arithmetic one sample at a time in 32 bits, with the NEON code's 16-bit
        // truncations, rounding and saturation (x64, and machines without vectors)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Portable(
            ref short c,
            ref short q,
            Span<byte> output,
            int stride)
        {
            Span<short> workspace = stackalloc short[64];
            for (int column = 0; column < 8; column++)
            {
                short In(ref short c, ref short q, int i) => (short)(Unsafe.Add(ref c, i) * Unsafe.Add(ref q, i));
                Columns(
                    In(ref c, ref q, column), In(ref c, ref q, 8 + column), In(ref c, ref q, 16 + column), In(ref c, ref q, 24 + column),
                    In(ref c, ref q, 32 + column), In(ref c, ref q, 40 + column), In(ref c, ref q, 48 + column), In(ref c, ref q, 56 + column),
                    out int t10, out int t11, out int t12, out int t13, out int t0, out int t1, out int t2, out int t3);
                workspace[column] = RoundNarrow(t10 + t3);
                workspace[8 + column] = RoundNarrow(t11 + t2);
                workspace[16 + column] = RoundNarrow(t12 + t1);
                workspace[24 + column] = RoundNarrow(t13 + t0);
                workspace[32 + column] = RoundNarrow(t13 - t0);
                workspace[40 + column] = RoundNarrow(t12 - t1);
                workspace[48 + column] = RoundNarrow(t11 - t2);
                workspace[56 + column] = RoundNarrow(t10 - t3);
            }

            ref byte o = ref MemoryMarshal.GetReference(output);
            for (int row = 0; row < 8; row++)
            {
                var w = workspace.Slice(row * 8, 8);
                Columns(w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], out int t10, out int t11, out int t12, out int t13, out int t0, out int t1, out int t2, out int t3);
                ref byte r = ref Unsafe.Add(ref o, row * stride);
                r = Sample(t10 + t3);
                Unsafe.Add(ref r, 1) = Sample(t11 + t2);
                Unsafe.Add(ref r, 2) = Sample(t12 + t1);
                Unsafe.Add(ref r, 3) = Sample(t13 + t0);
                Unsafe.Add(ref r, 4) = Sample(t13 - t0);
                Unsafe.Add(ref r, 5) = Sample(t12 - t1);
                Unsafe.Add(ref r, 6) = Sample(t11 - t2);
                Unsafe.Add(ref r, 7) = Sample(t10 - t3);
            }
        }

        private static void Columns(
            short i0, short i1, short i2, short i3, short i4, short i5, short i6, short i7,
            out int tmp10, out int tmp11, out int tmp12, out int tmp13, out int tmp0, out int tmp1, out int tmp2, out int tmp3)
        {
            int even2 = (i2 * F0541) + (i6 * (F0541 - F1847));
            int even3 = (i2 * (F0541 + F0765)) + (i6 * F0541);
            int even0 = (short)(i0 + i4) << ConstBits;
            int even1 = (short)(i0 - i4) << ConstBits;
            tmp10 = even0 + even3;
            tmp13 = even0 - even3;
            tmp11 = even1 + even2;
            tmp12 = even1 - even2;

            short z3s = (short)(i7 + i3);
            short z4s = (short)(i5 + i1);
            int z3 = (z3s * (F1175 - F1961)) + (z4s * F1175);
            int z4 = (z3s * F1175) + (z4s * (F1175 - F0390));
            tmp0 = (i7 * (F0298 - F0899)) - (i1 * F0899) + z3;
            tmp1 = (i5 * (F2053 - F2562)) - (i3 * F2562) + z4;
            tmp2 = (i3 * (F3072 - F2562)) - (i5 * F2562) + z3;
            tmp3 = (i1 * (F1501 - F0899)) - (i7 * F0899) + z4;
        }

        // rshrn: rounded right by pass 1's descale, the low 16 bits kept
        private static short RoundNarrow(
            int x) => (short)((x + (1 << (DescaleP1 - 1))) >> DescaleP1);

        // addhn's high half, then sqrshrn by 2 to a signed byte, plus 128
        private static byte Sample(
            int x) => (byte)(Math.Clamp(((short)(x >> 16) + 2) >> 2, -128, 127) + 128);
    }
}
