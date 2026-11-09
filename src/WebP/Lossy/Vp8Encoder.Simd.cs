// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// SIMD forms of the encoder's transforms and metrics, after libwebp 1.6.0's src/dsp/enc_neon.c and
// enc_sse2.c, modified by Hot Chai Games LLC: written in C#, each bit-exact with the C (Vp8Encoder.Dsp.cs).
// libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    // The SIMD kernels need the two-source zips (or unpacks): NEON or SSE2
    private static bool SimdKernels => AdvSimd.Arm64.IsSupported || Sse2.IsSupported;

    // Eight bytes zero-extended to eight shorts
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadWiden8(
        ref byte p)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.ZeroExtendWideningLower(Vector64.LoadUnsafe(ref p)).AsInt16();
        }

        var v = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref p)).AsByte();
        return Sse2.UnpackLow(v, Vector128<byte>.Zero).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Zip1(
        Vector128<short> a,
        Vector128<short> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Zip2(
        Vector128<short> a,
        Vector128<short> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Zip1x32(
        Vector128<short> a,
        Vector128<short> b)
    {
        return (AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a.AsInt32(), b.AsInt32()) : Sse2.UnpackLow(a.AsInt32(), b.AsInt32())).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Zip2x32(
        Vector128<short> a,
        Vector128<short> b)
    {
        return (AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a.AsInt32(), b.AsInt32()) : Sse2.UnpackHigh(a.AsInt32(), b.AsInt32())).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Zip1x64(
        Vector128<short> a,
        Vector128<short> b)
    {
        return (AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a.AsInt64(), b.AsInt64()) : Sse2.UnpackLow(a.AsInt64(), b.AsInt64())).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Zip2x64(
        Vector128<short> a,
        Vector128<short> b)
    {
        return (AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a.AsInt64(), b.AsInt64()) : Sse2.UnpackHigh(a.AsInt64(), b.AsInt64())).AsInt16();
    }

    // Transposes the two 4x4 blocks in the vectors' halves: row r of each block in v<r> to column r
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4x2(
        ref Vector128<short> v0,
        ref Vector128<short> v1,
        ref Vector128<short> v2,
        ref Vector128<short> v3)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            // TRN1/TRN2 on 16 then 32 bits keep each block in its half
            var t0 = AdvSimd.Arm64.TransposeEven(v0, v1);
            var t1 = AdvSimd.Arm64.TransposeOdd(v0, v1);
            var t2 = AdvSimd.Arm64.TransposeEven(v2, v3);
            var t3 = AdvSimd.Arm64.TransposeOdd(v2, v3);
            v0 = AdvSimd.Arm64.TransposeEven(t0.AsInt32(), t2.AsInt32()).AsInt16();
            v2 = AdvSimd.Arm64.TransposeOdd(t0.AsInt32(), t2.AsInt32()).AsInt16();
            v1 = AdvSimd.Arm64.TransposeEven(t1.AsInt32(), t3.AsInt32()).AsInt16();
            v3 = AdvSimd.Arm64.TransposeOdd(t1.AsInt32(), t3.AsInt32()).AsInt16();
            return;
        }

        var p0 = Zip1(v0, v1);
        var p1 = Zip2(v0, v1);
        var p2 = Zip1(v2, v3);
        var p3 = Zip2(v2, v3);
        var q0 = Zip1x32(p0, p2);
        var q1 = Zip2x32(p0, p2);
        var q2 = Zip1x32(p1, p3);
        var q3 = Zip2x32(p1, p3);
        v0 = Zip1x64(q0, q2);
        v1 = Zip2x64(q0, q2);
        v2 = Zip1x64(q1, q3);
        v3 = Zip2x64(q1, q3);
    }

    // floor(a * k / 65536) per lane
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> MulHigh(
        Vector128<short> a,
        short k)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            var lo = AdvSimd.MultiplyWideningLower(a.GetLower(), Vector64.Create(k));
            var hi = AdvSimd.MultiplyWideningUpper(a, Vector128.Create(k));
            return AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(lo, 16), hi, 16);
        }

        return Sse2.MultiplyHigh(a, Vector128.Create(k));
    }

    // MUL1 and MUL2 of the inverse transform: a * 20091 / 65536 + a and a * 35468 / 65536 (as
    // a * (35468 - 65536) / 65536 + a)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> VMul1(
        Vector128<short> a)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            // a + ((2 a 20091 >> 16) >> 1), as libwebp's NEON does (SQDMULH, SSRA)
            return AdvSimd.ShiftRightArithmeticAdd(a, AdvSimd.MultiplyDoublingSaturateHigh(a, Vector128.Create((short)20091)), 1);
        }

        return MulHigh(a, 20091) + a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> VMul2(
        Vector128<short> a)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            // 2 a (35468 / 2) >> 16 (SQDMULH)
            return AdvSimd.MultiplyDoublingSaturateHigh(a, Vector128.Create((short)(35468 / 2)));
        }

        return MulHigh(a, -30068) + a;
    }

    // (a * ka + b * kb + c) >> shift per lane, in 32 bits; the result fits in 16
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> MulAddShift(
        Vector128<short> a,
        short ka,
        Vector128<short> b,
        short kb,
        int c,
        [ConstantExpected(Min = 1, Max = 16)] byte shift)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            var vc = Vector128.Create(c);
            var lo = AdvSimd.MultiplyWideningLowerAndAdd(vc, a.GetLower(), Vector64.Create(ka));
            lo = AdvSimd.MultiplyWideningLowerAndAdd(lo, b.GetLower(), Vector64.Create(kb));
            var hi = AdvSimd.MultiplyWideningUpperAndAdd(vc, a, Vector128.Create(ka));
            hi = AdvSimd.MultiplyWideningUpperAndAdd(hi, b, Vector128.Create(kb));
            return AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(lo, shift), hi, shift);
        }

        var k = Vector128.Create((int)(ushort)ka | (kb << 16)).AsInt16();
        var m0 = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(a, b), k) + Vector128.Create(c);
        var m1 = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(a, b), k) + Vector128.Create(c);
        return Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(m0, shift), Sse2.ShiftRightArithmetic(m1, shift));
    }

    // FTransform of two blocks side by side (output and output + 16), or of one (the second's
    // lanes computed from the samples to its right, and dropped)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FTransformSimd(
        ref byte src,
        ref byte reference,
        ref short output,
        bool two)
    {
        if (!two && AdvSimd.Arm64.IsSupported)
        {
            FTransformOneNeon(ref src, ref reference, ref output);
            return;
        }

        var v0 = LoadWiden8(ref src) - LoadWiden8(ref reference);
        var v1 = LoadWiden8(ref Unsafe.Add(ref src, Bps)) - LoadWiden8(ref Unsafe.Add(ref reference, Bps));
        var v2 = LoadWiden8(ref Unsafe.Add(ref src, 2 * Bps)) - LoadWiden8(ref Unsafe.Add(ref reference, 2 * Bps));
        var v3 = LoadWiden8(ref Unsafe.Add(ref src, 3 * Bps)) - LoadWiden8(ref Unsafe.Add(ref reference, 3 * Bps));

        // The rows' pass, on the columns (lanes: the rows)
        Transpose4x4x2(ref v0, ref v1, ref v2, ref v3);
        var a0 = v0 + v3;
        var a1 = v1 + v2;
        var a2 = v1 - v2;
        var a3 = v0 - v3;
        var t0 = (a0 + a1) << 3;
        var t1 = MulAddShift(a2, 2217, a3, 5352, 1812, 9);
        var t2 = (a0 - a1) << 3;
        var t3 = MulAddShift(a3, 2217, a2, -5352, 937, 9);

        // The columns' pass, on the rows (lanes: the frequencies across)
        Transpose4x4x2(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t3;
        a1 = t1 + t2;
        a2 = t1 - t2;
        a3 = t0 - t3;
        var seven = Vector128.Create((short)7);
        var o0 = (a0 + a1 + seven) >> 4;
        var o1 = MulAddShift(a2, 2217, a3, 5352, 12000, 16) - ~Vector128.Equals(a3, Vector128<short>.Zero);
        var o2 = (a0 - a1 + seven) >> 4;
        var o3 = MulAddShift(a3, 2217, a2, -5352, 51000, 16);
        if (two)
        {
            Zip1x64(o0, o1).StoreUnsafe(ref output);
            Zip1x64(o2, o3).StoreUnsafe(ref output, 8);
            Zip2x64(o0, o1).StoreUnsafe(ref output, 16);
            Zip2x64(o2, o3).StoreUnsafe(ref output, 24);
        }
        else
        {
            Zip1x64(o0, o1).StoreUnsafe(ref output);
            Zip1x64(o2, o3).StoreUnsafe(ref output, 8);
        }
    }

    // One block's FTransform with two rows a vector ([row 0 | row 1], [row 2 | row 3]), transposed by
    // two zips, each pass on four lanes
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FTransformOneNeon(
        ref byte src,
        ref byte reference,
        ref short output)
    {
        var r01 = LoadPair(ref src, ref Unsafe.Add(ref src, Bps)) - LoadPair(ref reference, ref Unsafe.Add(ref reference, Bps));
        var r23 = LoadPair(ref Unsafe.Add(ref src, 2 * Bps), ref Unsafe.Add(ref src, 3 * Bps)) - LoadPair(ref Unsafe.Add(ref reference, 2 * Bps), ref Unsafe.Add(ref reference, 3 * Bps));
        Transpose8x2(ref r01, ref r23);

        // The rows' pass on the columns (lanes: the rows)
        var c0 = r01.GetLower();
        var c1 = r01.GetUpper();
        var c2 = r23.GetLower();
        var c3 = r23.GetUpper();
        var a0 = c0 + c3;
        var a1 = c1 + c2;
        var a2 = c1 - c2;
        var a3 = c0 - c3;
        var t0 = (a0 + a1) << 3;
        var t2 = (a0 - a1) << 3;
        var t1 = MulAddShift4(a2, 2217, a3, 5352, 1812, 9);
        var t3 = MulAddShift4(a3, 2217, a2, -5352, 937, 9);
        var u01 = Vector128.Create(t0, t1);
        var u23 = Vector128.Create(t2, t3);
        Transpose8x2(ref u01, ref u23);

        // The columns' pass on the rows (lanes: the frequencies across)
        var v0 = u01.GetLower();
        var v1 = u01.GetUpper();
        var v2 = u23.GetLower();
        var v3 = u23.GetUpper();
        a0 = v0 + v3;
        a1 = v1 + v2;
        a2 = v1 - v2;
        a3 = v0 - v3;
        var seven = Vector64.Create((short)7);
        var o0 = (a0 + a1 + seven) >> 4;
        var o2 = (a0 - a1 + seven) >> 4;
        var o1 = MulAddShift4(a2, 2217, a3, 5352, 12000, 16) - ~Vector64.Equals(a3, Vector64<short>.Zero);
        var o3 = MulAddShift4(a3, 2217, a2, -5352, 51000, 16);
        Vector128.Create(o0, o1).StoreUnsafe(ref output);
        Vector128.Create(o2, o3).StoreUnsafe(ref output, 8);
    }

    // Transpose8x2_NEON: [a | b], [c | d] (rows of four) to [a0 b0 c0 d0 | a1 b1 c1 d1], [.. 2 | .. 3]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x2(
        ref Vector128<short> in0,
        ref Vector128<short> in1)
    {
        var z0 = AdvSimd.Arm64.ZipLow(in0, in1);
        var z1 = AdvSimd.Arm64.ZipHigh(in0, in1);
        in0 = AdvSimd.Arm64.ZipLow(z0, z1);
        in1 = AdvSimd.Arm64.ZipHigh(z0, z1);
    }

    // (a * ka + b * kb + c) >> shift on four lanes, in 32 bits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector64<short> MulAddShift4(
        Vector64<short> a,
        short ka,
        Vector64<short> b,
        short kb,
        int c,
        [ConstantExpected(Min = 1, Max = 16)] byte shift)
    {
        var m = AdvSimd.MultiplyWideningLowerAndAdd(Vector128.Create(c), a, Vector64.Create(ka));
        m = AdvSimd.MultiplyWideningLowerAndAdd(m, b, Vector64.Create(kb));
        return AdvSimd.ShiftRightLogicalNarrowingLower(m, shift);
    }

    // ITransform of two blocks side by side (input and input + 16), or of one (the second's lanes
    // zero, and not stored)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ITransformSimd(
        ref byte reference,
        ref short input,
        ref byte dst,
        bool two)
    {
        if (!two && AdvSimd.Arm64.IsSupported)
        {
            ITransformOneNeon(ref reference, ref input, ref dst);
            return;
        }

        Vector128<short> i0, i1, i2, i3;
        if (two)
        {
            var b00 = Vector128.LoadUnsafe(ref input);
            var b01 = Vector128.LoadUnsafe(ref input, 8);
            var b10 = Vector128.LoadUnsafe(ref input, 16);
            var b11 = Vector128.LoadUnsafe(ref input, 24);
            i0 = Zip1x64(b00, b10);
            i1 = Zip2x64(b00, b10);
            i2 = Zip1x64(b01, b11);
            i3 = Zip2x64(b01, b11);
        }
        else
        {
            var b00 = Vector128.LoadUnsafe(ref input);
            var b01 = Vector128.LoadUnsafe(ref input, 8);
            i0 = Zip1x64(b00, Vector128<short>.Zero);
            i1 = Zip2x64(b00, Vector128<short>.Zero);
            i2 = Zip1x64(b01, Vector128<short>.Zero);
            i3 = Zip2x64(b01, Vector128<short>.Zero);
        }

        // The vertical pass (lanes: the columns); T<k> holds tmp[4 * lane + k]
        var a = i0 + i2;
        var b = i0 - i2;
        var c = VMul2(i1) - VMul1(i3);
        var d = VMul1(i1) + VMul2(i3);
        var t0 = a + d;
        var t1 = b + c;
        var t2 = b - c;
        var t3 = a - d;

        // The horizontal pass (lanes: the rows)
        Transpose4x4x2(ref t0, ref t1, ref t2, ref t3);
        var dc = t0 + Vector128.Create((short)4);
        a = dc + t2;
        b = dc - t2;
        c = VMul2(t1) - VMul1(t3);
        d = VMul1(t1) + VMul2(t3);
        var o0 = (a + d) >> 3;
        var o1 = (b + c) >> 3;
        var o2 = (b - c) >> 3;
        var o3 = (a - d) >> 3;
        Transpose4x4x2(ref o0, ref o1, ref o2, ref o3);
        StoreRow(ref dst, ref reference, o0, two);
        StoreRow(ref Unsafe.Add(ref dst, Bps), ref Unsafe.Add(ref reference, Bps), o1, two);
        StoreRow(ref Unsafe.Add(ref dst, 2 * Bps), ref Unsafe.Add(ref reference, 2 * Bps), o2, two);
        StoreRow(ref Unsafe.Add(ref dst, 3 * Bps), ref Unsafe.Add(ref reference, 3 * Bps), o3, two);
    }

    // One block's inverse transform as libwebp's ITransformOne_NEON: two rows a vector ([row 0 | row 1],
    // [row 2 | row 3]), the passes' results transposed by two zips, the (x + 4) >> 3 a rounding shift
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ITransformOneNeon(
        ref byte reference,
        ref short input,
        ref byte dst)
    {
        var r01 = Vector128.LoadUnsafe(ref input);
        var r23 = Vector128.LoadUnsafe(ref input, 8);
        TransformPassNeon(ref r01, ref r23);
        TransformPassNeon(ref r01, ref r23);
        r01 = AdvSimd.ShiftRightArithmeticRounded(r01, 3);
        r23 = AdvSimd.ShiftRightArithmeticRounded(r23, 3);
        var o01 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(LoadPair(ref reference, ref Unsafe.Add(ref reference, Bps)) + r01).AsUInt32();
        var o23 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(LoadPair(ref Unsafe.Add(ref reference, 2 * Bps), ref Unsafe.Add(ref reference, 3 * Bps)) + r23).AsUInt32();
        Unsafe.WriteUnaligned(ref dst, o01.ToScalar());
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, Bps), o01.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 2 * Bps), o23.ToScalar());
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 3 * Bps), o23.GetElement(1));
    }

    // TransformPass_NEON: a = in0 + in8, b = in0 - in8, c = MUL2(in4) - MUL1(in12), d = MUL1(in4) +
    // MUL2(in12); [a + d | b + c], [b - c | a - d] transposed back to rows
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransformPassNeon(
        ref Vector128<short> r01,
        ref Vector128<short> r23)
    {
        var b1 = AdvSimd.Arm64.ZipHigh(r01.AsInt64(), r23.AsInt64()).AsInt16();
        var c0 = VMul1(b1);
        var c1 = VMul2(b1);
        var a = r01.GetLower() + r23.GetLower();
        var b = r01.GetLower() - r23.GetLower();
        var c = c1.GetLower() - c0.GetUpper();
        var d = c0.GetLower() + c1.GetUpper();
        var d0 = Vector128.Create(a, b);
        var d1 = Vector128.Create(d, c);
        var e0 = d0 + d1;
        var t = d0 - d1;
        var e1 = Vector128.Create(t.GetUpper(), t.GetLower());
        Transpose8x2(ref e0, ref e1);
        r01 = e0;
        r23 = e1;
    }

    // A row of reference + residual, clipped to bytes: eight samples, or four
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(
        ref byte dst,
        ref byte reference,
        Vector128<short> residual,
        bool two)
    {
        var sum = LoadWiden8(ref reference) + residual;
        Vector64<byte> bytes;
        if (AdvSimd.Arm64.IsSupported)
        {
            bytes = AdvSimd.ExtractNarrowingSaturateUnsignedLower(sum);
        }
        else
        {
            bytes = Sse2.PackUnsignedSaturate(sum, sum).GetLower();
        }

        if (two)
        {
            bytes.StoreUnsafe(ref dst);
        }
        else
        {
            Unsafe.WriteUnaligned(ref dst, bytes.AsUInt32().ToScalar());
        }
    }

    // Disto4x4: |TTransform(b) - TTransform(a)| >> 5, the two blocks in the halves of the vectors
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Disto4x4Simd(
        ref byte a,
        ref byte b,
        ReadOnlySpan<ushort> w)
    {
        var v0 = LoadPair(ref a, ref b);
        var v1 = LoadPair(ref Unsafe.Add(ref a, Bps), ref Unsafe.Add(ref b, Bps));
        var v2 = LoadPair(ref Unsafe.Add(ref a, 2 * Bps), ref Unsafe.Add(ref b, 2 * Bps));
        var v3 = LoadPair(ref Unsafe.Add(ref a, 3 * Bps), ref Unsafe.Add(ref b, 3 * Bps));

        // The Hadamard transform is exact, so either pass can go first: down the columns, then
        // across (the weights are symmetric, so the transposed result weighs the same)
        var a0 = v0 + v2;
        var a1 = v1 + v3;
        var a2 = v1 - v3;
        var a3 = v0 - v2;
        var t0 = a0 + a1;
        var t1 = a3 + a2;
        var t2 = a3 - a2;
        var t3 = a0 - a1;
        Transpose4x4x2(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t2;
        a1 = t1 + t3;
        a2 = t1 - t3;
        a3 = t0 - t2;
        var r0 = Vector128.Abs(a0 + a1);
        var r1 = Vector128.Abs(a3 + a2);
        var r2 = Vector128.Abs(a3 - a2);
        var r3 = Vector128.Abs(a0 - a1);

        // The weights, negative for a's half: the sum is b's minus a's
        ref ushort wr = ref MemoryMarshal.GetReference(w);
        var w0 = SignedWeights(ref wr);
        var w1 = SignedWeights(ref Unsafe.Add(ref wr, 4));
        var w2 = SignedWeights(ref Unsafe.Add(ref wr, 8));
        var w3 = SignedWeights(ref Unsafe.Add(ref wr, 12));
        int sum;
        if (AdvSimd.Arm64.IsSupported)
        {
            var acc = AdvSimd.MultiplyWideningLower(r0.GetLower(), w0.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, r0, w0);
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, r1.GetLower(), w1.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, r1, w1);
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, r2.GetLower(), w2.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, r2, w2);
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, r3.GetLower(), w3.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, r3, w3);
            sum = AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }
        else
        {
            var acc = Sse2.MultiplyAddAdjacent(r0, w0) + Sse2.MultiplyAddAdjacent(r1, w1)
                + Sse2.MultiplyAddAdjacent(r2, w2) + Sse2.MultiplyAddAdjacent(r3, w3);
            sum = Vector128.Sum(acc);
        }

        return ((sum ^ (sum >> 31)) - (sum >> 31)) >> 5;
    }

    // [a's four samples | b's four] of a row, as shorts
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadPair(
        ref byte a,
        ref byte b)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            var pair = AdvSimd.Arm64.ZipLow(Vector64.LoadUnsafe(ref a).AsUInt32(), Vector64.LoadUnsafe(ref b).AsUInt32());
            return AdvSimd.ZeroExtendWideningLower(pair.AsByte()).AsInt16();
        }

        var v = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref a));
        v = Sse2.UnpackLow(v, Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref b)));
        return Sse2.UnpackLow(v.AsByte(), Vector128<byte>.Zero).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> SignedWeights(
        ref ushort w)
    {
        var four = Vector64.LoadUnsafe(ref w).AsInt16();
        return Vector128.Create(-four, four);
    }

    // GetSSE over 16 samples across (luma, or u and v side by side), h rows
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Sse16Simd(
        ref byte a,
        ref byte b,
        int h)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            var acc = Vector128<uint>.Zero;
            for (int y = 0; y < h; y++)
            {
                var d = AdvSimd.AbsoluteDifference(Vector128.LoadUnsafe(ref a, (nuint)(y * Bps)), Vector128.LoadUnsafe(ref b, (nuint)(y * Bps)));
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.MultiplyWideningLower(d.GetLower(), d.GetLower()));
                acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.MultiplyWideningUpper(d, d));
            }

            return (int)AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }

        var sum = Vector128<int>.Zero;
        for (int y = 0; y < h; y++)
        {
            var va = Vector128.LoadUnsafe(ref a, (nuint)(y * Bps));
            var vb = Vector128.LoadUnsafe(ref b, (nuint)(y * Bps));
            var lo = Sse2.UnpackLow(va, Vector128<byte>.Zero).AsInt16() - Sse2.UnpackLow(vb, Vector128<byte>.Zero).AsInt16();
            var hi = Sse2.UnpackHigh(va, Vector128<byte>.Zero).AsInt16() - Sse2.UnpackHigh(vb, Vector128<byte>.Zero).AsInt16();
            sum += Sse2.MultiplyAddAdjacent(lo, lo) + Sse2.MultiplyAddAdjacent(hi, hi);
        }

        return Vector128.Sum(sum);
    }

    // GetSSE of a 4x4 block
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Sse4x4Simd(
        ref byte a,
        ref byte b)
    {
        var va = LoadPair(ref a, ref Unsafe.Add(ref a, Bps));
        var vb = LoadPair(ref b, ref Unsafe.Add(ref b, Bps));
        var vc = LoadPair(ref Unsafe.Add(ref a, 2 * Bps), ref Unsafe.Add(ref a, 3 * Bps));
        var vd = LoadPair(ref Unsafe.Add(ref b, 2 * Bps), ref Unsafe.Add(ref b, 3 * Bps));
        var d0 = va - vb;
        var d1 = vc - vd;
        if (AdvSimd.Arm64.IsSupported)
        {
            var acc = AdvSimd.MultiplyWideningLower(d0.GetLower(), d0.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, d0, d0);
            acc = AdvSimd.MultiplyWideningLowerAndAdd(acc, d1.GetLower(), d1.GetLower());
            acc = AdvSimd.MultiplyWideningUpperAndAdd(acc, d1, d1);
            return AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }

        return Vector128.Sum(Sse2.MultiplyAddAdjacent(d0, d0) + Sse2.MultiplyAddAdjacent(d1, d1));
    }

    // QuantizeBlock: the levels in natural order to levels (zigzagged after), in dequantized
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool QuantizeBlockSimd(
        ref short input,
        ref short output,
        Matrix mtx)
    {
        ref ushort q = ref MemoryMarshal.GetArrayDataReference(mtx.Q);
        ref ushort iq = ref MemoryMarshal.GetArrayDataReference(mtx.Iq);
        ref uint bias = ref MemoryMarshal.GetArrayDataReference(mtx.Bias);
        ref uint zthresh = ref MemoryMarshal.GetArrayDataReference(mtx.ZThresh);
        ref ushort sharpen = ref MemoryMarshal.GetArrayDataReference(mtx.Sharpen);
        var l0 = QuantizeHalf(ref input, ref q, ref iq, ref bias, ref zthresh, ref sharpen);
        var l1 = QuantizeHalf(ref Unsafe.Add(ref input, 8), ref Unsafe.Add(ref q, 8), ref Unsafe.Add(ref iq, 8), ref Unsafe.Add(ref bias, 8), ref Unsafe.Add(ref zthresh, 8), ref Unsafe.Add(ref sharpen, 8));

        // The zigzag order: 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15
        if (AdvSimd.Arm64.IsSupported)
        {
            var table = (l0.AsByte(), l1.AsByte());
            var z0 = AdvSimd.Arm64.VectorTableLookup(table, Vector128.Create((byte)0, 1, 2, 3, 8, 9, 16, 17, 10, 11, 4, 5, 6, 7, 12, 13));
            var z1 = AdvSimd.Arm64.VectorTableLookup(table, Vector128.Create((byte)18, 19, 24, 25, 26, 27, 20, 21, 14, 15, 22, 23, 28, 29, 30, 31));
            z0.AsInt16().StoreUnsafe(ref output);
            z1.AsInt16().StoreUnsafe(ref output, 8);
        }
        else
        {
            var zigzag = Zigzag;
            Span<short> natural = stackalloc short[16];
            l0.StoreUnsafe(ref MemoryMarshal.GetReference(natural));
            l1.StoreUnsafe(ref MemoryMarshal.GetReference(natural), 8);
            for (int n = 0; n < 16; n++)
            {
                Unsafe.Add(ref output, n) = natural[zigzag[n]];
            }
        }

        return (l0 | l1) != Vector128<short>.Zero;
    }

    // Eight coefficients quantized: the levels returned, the dequantized values stored back
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> QuantizeHalf(
        ref short input,
        ref ushort q,
        ref ushort iq,
        ref uint bias,
        ref uint zthresh,
        ref ushort sharpen)
    {
        var x = Vector128.LoadUnsafe(ref input);
        var sign = Vector128.LessThan(x, Vector128<short>.Zero);
        var coeff = Vector128.Abs(x).AsUInt16() + Vector128.LoadUnsafe(ref sharpen);
        if (AdvSimd.Arm64.IsSupported)
        {
            // As libwebp's NEON: no zero threshold (below it the level is 0 anyway), the product
            // widened, the bias added halving, and >> 16 narrowed
            var iqv = Vector128.LoadUnsafe(ref iq);
            var m0 = AdvSimd.FusedAddHalving(AdvSimd.MultiplyWideningLower(coeff.GetLower(), iqv.GetLower()), Vector128.LoadUnsafe(ref bias));
            var m1 = AdvSimd.FusedAddHalving(AdvSimd.MultiplyWideningUpper(coeff, iqv), Vector128.LoadUnsafe(ref bias, 4));
            var levels = AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(m0, 16), m1, 16);
            var signed = (Vector128.Min(levels, Vector128.Create((ushort)MaxLevel)).AsInt16() ^ sign) - sign;
            (signed * Vector128.LoadUnsafe(ref q).AsInt16()).StoreUnsafe(ref input);
            return signed;
        }

        var (c0, c1) = Vector128.Widen(coeff);
        var (iq0, iq1) = Vector128.Widen(Vector128.LoadUnsafe(ref iq));
        var maxLevel = Vector128.Create((uint)MaxLevel);
        var lv0 = Vector128.Min(((c0 * iq0) + Vector128.LoadUnsafe(ref bias)) >>> QFix, maxLevel);
        var lv1 = Vector128.Min(((c1 * iq1) + Vector128.LoadUnsafe(ref bias, 4)) >>> QFix, maxLevel);
        lv0 &= Vector128.GreaterThan(c0, Vector128.LoadUnsafe(ref zthresh));
        lv1 &= Vector128.GreaterThan(c1, Vector128.LoadUnsafe(ref zthresh, 4));
        var level = Vector128.Narrow(lv0, lv1).AsInt16();
        level = (level ^ sign) - sign;
        (level * Vector128.LoadUnsafe(ref q).AsInt16()).StoreUnsafe(ref input);
        return level;
    }

    // The index of a block's last level that isn't zero, or -1 (VP8SetResidualCoeffs)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LastNonZero(
        ref short coeffs)
    {
        if (SimdKernels)
        {
            var zero = Vector128<short>.Zero;
            var nonZero = Vector128.Narrow(
                ~Vector128.Equals(Vector128.LoadUnsafe(ref coeffs), zero),
                ~Vector128.Equals(Vector128.LoadUnsafe(ref coeffs, 8), zero)).AsByte();
            if (AdvSimd.Arm64.IsSupported)
            {
                // A nibble per level
                ulong mask = AdvSimd.ShiftRightLogicalNarrowingLower(nonZero.AsUInt16(), 4).AsUInt64().ToScalar();
                return (mask == 0) ? -1 : 15 - (BitOperations.LeadingZeroCount(mask) >> 2);
            }

            uint bits = (uint)Sse2.MoveMask(nonZero);
            return 31 - BitOperations.LeadingZeroCount(bits);
        }

        for (int n = 15; n >= 0; n--)
        {
            if (Unsafe.Add(ref coeffs, n) != 0)
            {
                return n;
            }
        }

        return -1;
    }

    // IsFlat: whether at most thresh of the blocks' AC levels aren't zero
    private static bool IsFlatSimd(
        ref short levels,
        int numBlocks,
        int thresh)
    {
        var zero = Vector128<short>.Zero;
        var notDc = Vector128.Create((short)0, -1, -1, -1, -1, -1, -1, -1);
        var count = Vector128<short>.Zero;
        for (int b = 0; b < numBlocks; b++)
        {
            count += ~Vector128.Equals(Vector128.LoadUnsafe(ref levels, (nuint)(b * 16)), zero) & notDc;
            count += ~Vector128.Equals(Vector128.LoadUnsafe(ref levels, (nuint)((b * 16) + 8)), zero);
        }

        return -Vector128.Sum(count) <= thresh;
    }
}
