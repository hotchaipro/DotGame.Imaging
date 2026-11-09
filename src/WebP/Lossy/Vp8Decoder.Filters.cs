// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dsp/dec_neon.c and dec_sse2.c), modified by Hot Chai Games LLC: written
// in C#, with the runtime's intrinsics. libwebp carries this notice:
//   Copyright 2014 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.WebP;

// The loop filters 16 pixels at a time: a luma edge's 16, or a chroma edge's 8 of U and 8 of V
// together, each lane one line across the edge. libwebp's vector filters, whose arithmetic (signed
// bytes with saturation) gives its C filters' pixels exactly.
internal static partial class Vp8Decoder
{
    private static bool VectorFilters => AdvSimd.Arm64.IsSupported || Sse2.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> AddSat(
        Vector128<sbyte> a,
        Vector128<sbyte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.AddSaturate(a, b) : Sse2.AddSaturate(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> SubSat(
        Vector128<sbyte> a,
        Vector128<sbyte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.SubtractSaturate(a, b) : Sse2.SubtractSaturate(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AddSatU(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.AddSaturate(a, b) : Sse2.AddSaturate(a, b);
    }

    // An arithmetic shift of signed bytes right by 3 (SSE2 has none for bytes: done in 16 bits)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> ShiftRight3(
        Vector128<sbyte> v)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.ShiftRightArithmetic(v, 3);
        }

        var low = Sse2.ShiftRightArithmetic(Sse2.UnpackLow(v, v).AsInt16(), 11);
        var high = Sse2.ShiftRightArithmetic(Sse2.UnpackHigh(v, v).AsInt16(), 11);
        return Sse2.PackSignedSaturate(low, high);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AbsDiff(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return Vector128.Max(a, b) - Vector128.Min(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> FlipSign(
        Vector128<byte> v)
    {
        return (v ^ Vector128.Create((byte)0x80)).AsSByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> FlipSignBack(
        Vector128<sbyte> v)
    {
        return v.AsByte() ^ Vector128.Create((byte)0x80);
    }

    // NeedsFilter_NEON: 2 |p0 - q0| + |p1 - q1| / 2 <= t, which is 4 |p0 - q0| + |p1 - q1| <= 2 t + 1
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NeedsFilterMask(
        Vector128<byte> p1,
        Vector128<byte> p0,
        Vector128<byte> q0,
        Vector128<byte> q1,
        int threshold)
    {
        var ap0q0 = AbsDiff(p0, q0);
        var ap1q1 = AbsDiff(p1, q1);
        var sum = AddSatU(AddSatU(ap0q0, ap0q0), ap1q1 >>> 1);
        return Vector128.GreaterThanOrEqual(Vector128.Create((byte)threshold), sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NeedsFilter2Mask(
        Vector128<byte> p3,
        Vector128<byte> p2,
        Vector128<byte> p1,
        Vector128<byte> p0,
        Vector128<byte> q0,
        Vector128<byte> q1,
        Vector128<byte> q2,
        Vector128<byte> q3,
        int innerThreshold,
        int threshold)
    {
        var max = Vector128.Max(Vector128.Max(AbsDiff(p3, p2), AbsDiff(p2, p1)), Vector128.Max(AbsDiff(p1, p0), AbsDiff(q3, q2)));
        max = Vector128.Max(max, Vector128.Max(AbsDiff(q2, q1), AbsDiff(q1, q0)));
        var inner = Vector128.GreaterThanOrEqual(Vector128.Create((byte)innerThreshold), max);
        return inner & NeedsFilterMask(p1, p0, q0, q1, threshold);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> HevMask(
        Vector128<byte> p1,
        Vector128<byte> p0,
        Vector128<byte> q0,
        Vector128<byte> q1,
        int hevThreshold)
    {
        var max = Vector128.Max(AbsDiff(p1, p0), AbsDiff(q1, q0));
        return Vector128.GreaterThan(max, Vector128.Create((byte)hevThreshold));
    }

    // (p1 - q1) + 3 (q0 - p0), saturated at each step
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> BaseDelta(
        Vector128<sbyte> p1,
        Vector128<sbyte> p0,
        Vector128<sbyte> q0,
        Vector128<sbyte> q1)
    {
        var q0p0 = SubSat(q0, p0);
        var p1q1 = SubSat(p1, q1);
        var s1 = AddSat(p1q1, q0p0);
        var s2 = AddSat(q0p0, s1);
        return AddSat(q0p0, s2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> BaseDelta0(
        Vector128<sbyte> p0,
        Vector128<sbyte> q0)
    {
        var q0p0 = SubSat(q0, p0);
        var s1 = AddSat(q0p0, q0p0);
        return AddSat(q0p0, s1);
    }

    // ApplyFilter2NoFlip_NEON
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyFilter2(
        ref Vector128<sbyte> p0,
        ref Vector128<sbyte> q0,
        Vector128<sbyte> delta)
    {
        var delta3 = ShiftRight3(AddSat(delta, Vector128.Create((sbyte)3)));
        var delta4 = ShiftRight3(AddSat(delta, Vector128.Create((sbyte)4)));
        p0 = AddSat(p0, delta3);
        q0 = SubSat(q0, delta4);
    }

    // DoFilter2_NEON: the simple filter, where the mask says
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter2(
        Vector128<byte> p1,
        ref Vector128<byte> p0,
        ref Vector128<byte> q0,
        Vector128<byte> q1,
        Vector128<byte> mask)
    {
        var p0s = FlipSign(p0);
        var q0s = FlipSign(q0);
        var delta = BaseDelta(FlipSign(p1), p0s, q0s, FlipSign(q1)) & mask.AsSByte();
        ApplyFilter2(ref p0s, ref q0s, delta);
        p0 = FlipSignBack(p0s);
        q0 = FlipSignBack(q0s);
    }

    // DoFilter4_NEON: an inner edge
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter4(
        ref Vector128<byte> p1,
        ref Vector128<byte> p0,
        ref Vector128<byte> q0,
        ref Vector128<byte> q1,
        Vector128<byte> mask,
        Vector128<byte> hevMask)
    {
        var p1s = FlipSign(p1);
        var p0s = FlipSign(p0);
        var q0s = FlipSign(q0);
        var q1s = FlipSign(q1);
        var simpleMask = mask & hevMask;
        ApplyFilter2(ref p0s, ref q0s, BaseDelta(p1s, p0s, q0s, q1s) & simpleMask.AsSByte());
        var delta0 = BaseDelta0(p0s, q0s) & (simpleMask ^ mask).AsSByte();

        // ApplyFilter4_NEON
        var a1 = ShiftRight3(AddSat(delta0, Vector128.Create((sbyte)4)));
        var a2 = ShiftRight3(AddSat(delta0, Vector128.Create((sbyte)3)));
        var a3 = Rounding1(a1);
        p0 = FlipSignBack(AddSat(p0s, a2));
        q0 = FlipSignBack(SubSat(q0s, a1));
        p1 = FlipSignBack(AddSat(p1s, a3));
        q1 = FlipSignBack(SubSat(q1s, a3));
    }

    // (a + 1) >> 1, for a in [-16, 15]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> Rounding1(
        Vector128<sbyte> a)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.ShiftRightArithmeticRounded(a, 1);
        }

        // In 16 bits, as SSE2 has no byte shifts
        var one = Vector128.Create((short)1);
        var low = Sse2.ShiftRightArithmetic(Sse2.ShiftRightArithmetic(Sse2.UnpackLow(a, a).AsInt16(), 8) + one, 1);
        var high = Sse2.ShiftRightArithmetic(Sse2.ShiftRightArithmetic(Sse2.UnpackHigh(a, a).AsInt16(), 8) + one, 1);
        return Sse2.PackSignedSaturate(low, high);
    }

    // DoFilter6_NEON: a macroblock edge
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter6(
        ref Vector128<byte> p2,
        ref Vector128<byte> p1,
        ref Vector128<byte> p0,
        ref Vector128<byte> q0,
        ref Vector128<byte> q1,
        ref Vector128<byte> q2,
        Vector128<byte> mask,
        Vector128<byte> hevMask)
    {
        var p2s = FlipSign(p2);
        var p1s = FlipSign(p1);
        var p0s = FlipSign(p0);
        var q0s = FlipSign(q0);
        var q1s = FlipSign(q1);
        var q2s = FlipSign(q2);
        var simpleMask = mask & hevMask;
        var delta0 = BaseDelta(p1s, p0s, q0s, q1s);
        ApplyFilter2(ref p0s, ref q0s, delta0 & simpleMask.AsSByte());
        var delta = delta0 & (simpleMask ^ mask).AsSByte();

        // ApplyFilter6_NEON: 9a - 1 and 27a - 1 in 16 bits, rounded down by 2^7 (and 18a - 1 by 2^6)
        var lowDelta = Vector128.WidenLower(delta);
        var highDelta = Vector128.WidenUpper(delta);
        var minusOne = Vector128.Create((short)-1);
        var sLow = (lowDelta * Vector128.Create((short)9)) + minusOne;
        var sHigh = (highDelta * Vector128.Create((short)9)) + minusOne;
        var zLow = sLow + (lowDelta * Vector128.Create((short)18));
        var zHigh = sHigh + (highDelta * Vector128.Create((short)18));
        var a3 = Vector128.Narrow((sLow + Vector128.Create((short)64)) >> 7, (sHigh + Vector128.Create((short)64)) >> 7);
        var a2 = Vector128.Narrow((sLow + Vector128.Create((short)32)) >> 6, (sHigh + Vector128.Create((short)32)) >> 6);
        var a1 = Vector128.Narrow((zLow + Vector128.Create((short)64)) >> 7, (zHigh + Vector128.Create((short)64)) >> 7);
        p0 = FlipSignBack(AddSat(p0s, a1));
        q0 = FlipSignBack(SubSat(q0s, a1));
        q1 = FlipSignBack(SubSat(q1s, a2));
        p1 = FlipSignBack(AddSat(p1s, a2));
        q2 = FlipSignBack(SubSat(q2s, a3));
        p2 = FlipSignBack(AddSat(p2s, a3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load16(
        ref byte p)
    {
        return Unsafe.ReadUnaligned<Vector128<byte>>(ref p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store16(
        ref byte p,
        Vector128<byte> v)
    {
        Unsafe.WriteUnaligned(ref p, v);
    }

    // Two rows of 8 (one of U and one of V, or two of luma) as one vector: on Arm64 with 64-bit vector
    // loads, as a load through a general register into a vector stalls the M6
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load8x2(
        ref byte u,
        ref byte v)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return Vector128.Create(Vector64.LoadUnsafe(ref u), Vector64.LoadUnsafe(ref v));
        }

        return Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref u), Unsafe.ReadUnaligned<ulong>(ref v)).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8x2(
        ref byte u,
        ref byte v,
        Vector128<byte> value)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            value.GetLower().StoreUnsafe(ref u);
            value.GetUpper().StoreUnsafe(ref v);
            return;
        }

        Unsafe.WriteUnaligned(ref u, value.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref v, value.AsUInt64().GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip8Low(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip8High(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip16Low(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a.AsUInt16(), b.AsUInt16()).AsByte() : Sse2.UnpackLow(a.AsUInt16(), b.AsUInt16()).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip16High(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a.AsUInt16(), b.AsUInt16()).AsByte() : Sse2.UnpackHigh(a.AsUInt16(), b.AsUInt16()).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip32Low(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a.AsUInt32(), b.AsUInt32()).AsByte() : Sse2.UnpackLow(a.AsUInt32(), b.AsUInt32()).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip32High(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a.AsUInt32(), b.AsUInt32()).AsByte() : Sse2.UnpackHigh(a.AsUInt32(), b.AsUInt32()).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip64Low(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a.AsUInt64(), b.AsUInt64()).AsByte() : Sse2.UnpackLow(a.AsUInt64(), b.AsUInt64()).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Zip64High(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a.AsUInt64(), b.AsUInt64()).AsByte() : Sse2.UnpackHigh(a.AsUInt64(), b.AsUInt64()).AsByte();
    }

    // Sixteen rows of 8 bytes (rows 0 to 7 from top, 8 to 15 from bottom, both at stride) as 8 columns
    // of 16: c[k] holds byte k of every row
    private struct Columns
    {
        public Vector128<byte> C0, C1, C2, C3, C4, C5, C6, C7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Columns LoadColumns(
        ref byte top,
        ref byte bottom,
        int stride)
    {
        var a0 = Load8x2(ref top, ref bottom);
        var a1 = Load8x2(ref Unsafe.Add(ref top, stride), ref Unsafe.Add(ref bottom, stride));
        var a2 = Load8x2(ref Unsafe.Add(ref top, 2 * stride), ref Unsafe.Add(ref bottom, 2 * stride));
        var a3 = Load8x2(ref Unsafe.Add(ref top, 3 * stride), ref Unsafe.Add(ref bottom, 3 * stride));
        var a4 = Load8x2(ref Unsafe.Add(ref top, 4 * stride), ref Unsafe.Add(ref bottom, 4 * stride));
        var a5 = Load8x2(ref Unsafe.Add(ref top, 5 * stride), ref Unsafe.Add(ref bottom, 5 * stride));
        var a6 = Load8x2(ref Unsafe.Add(ref top, 6 * stride), ref Unsafe.Add(ref bottom, 6 * stride));
        var a7 = Load8x2(ref Unsafe.Add(ref top, 7 * stride), ref Unsafe.Add(ref bottom, 7 * stride));

        // Bytes, then pairs, then fours, then eights interleaved: rows 0 to 7 (low halves) and 8 to 15
        var t01L = Zip8Low(a0, a1);
        var t01H = Zip8High(a0, a1);
        var t23L = Zip8Low(a2, a3);
        var t23H = Zip8High(a2, a3);
        var t45L = Zip8Low(a4, a5);
        var t45H = Zip8High(a4, a5);
        var t67L = Zip8Low(a6, a7);
        var t67H = Zip8High(a6, a7);
        var u0 = Zip16Low(t01L, t23L);
        var u1 = Zip16High(t01L, t23L);
        var u2 = Zip16Low(t45L, t67L);
        var u3 = Zip16High(t45L, t67L);
        var v0 = Zip16Low(t01H, t23H);
        var v1 = Zip16High(t01H, t23H);
        var v2 = Zip16Low(t45H, t67H);
        var v3 = Zip16High(t45H, t67H);
        var w0 = Zip32Low(u0, u2);
        var w1 = Zip32High(u0, u2);
        var w2 = Zip32Low(u1, u3);
        var w3 = Zip32High(u1, u3);
        var x0 = Zip32Low(v0, v2);
        var x1 = Zip32High(v0, v2);
        var x2 = Zip32Low(v1, v3);
        var x3 = Zip32High(v1, v3);
        Columns c;
        c.C0 = Zip64Low(w0, x0);
        c.C1 = Zip64High(w0, x0);
        c.C2 = Zip64Low(w1, x1);
        c.C3 = Zip64High(w1, x1);
        c.C4 = Zip64Low(w2, x2);
        c.C5 = Zip64High(w2, x2);
        c.C6 = Zip64Low(w3, x3);
        c.C7 = Zip64High(w3, x3);
        return c;
    }

    // The columns back to their 16 rows of 8
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreColumns(
        ref byte top,
        ref byte bottom,
        int stride,
        in Columns c)
    {
        var l01 = Zip8Low(c.C0, c.C1);
        var h01 = Zip8High(c.C0, c.C1);
        var l23 = Zip8Low(c.C2, c.C3);
        var h23 = Zip8High(c.C2, c.C3);
        var l45 = Zip8Low(c.C4, c.C5);
        var h45 = Zip8High(c.C4, c.C5);
        var l67 = Zip8Low(c.C6, c.C7);
        var h67 = Zip8High(c.C6, c.C7);
        StoreRows(ref top, stride, l01, l23, l45, l67);
        StoreRows(ref bottom, stride, h01, h23, h45, h67);
    }

    // Eight rows from four vectors of two columns each, row by row
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRows(
        ref byte p,
        int stride,
        Vector128<byte> c01,
        Vector128<byte> c23,
        Vector128<byte> c45,
        Vector128<byte> c67)
    {
        var s0 = Zip16Low(c01, c23);
        var s1 = Zip16High(c01, c23);
        var s2 = Zip16Low(c45, c67);
        var s3 = Zip16High(c45, c67);
        var r01 = Zip32Low(s0, s2).AsUInt64();
        var r23 = Zip32High(s0, s2).AsUInt64();
        var r45 = Zip32Low(s1, s3).AsUInt64();
        var r67 = Zip32High(s1, s3).AsUInt64();
        Store8x2(ref p, ref Unsafe.Add(ref p, stride), r01.AsByte());
        Store8x2(ref Unsafe.Add(ref p, 2 * stride), ref Unsafe.Add(ref p, 3 * stride), r23.AsByte());
        Store8x2(ref Unsafe.Add(ref p, 4 * stride), ref Unsafe.Add(ref p, 5 * stride), r45.AsByte());
        Store8x2(ref Unsafe.Add(ref p, 6 * stride), ref Unsafe.Add(ref p, 7 * stride), r67.AsByte());
    }

    // The simple filter of the edge above row p (16 columns)
    private static void SimpleVFilter16Vector(
        ref byte p,
        int stride,
        int threshold)
    {
        var p1 = Load16(ref Unsafe.Subtract(ref p, 2 * stride));
        var p0 = Load16(ref Unsafe.Subtract(ref p, stride));
        var q0 = Load16(ref p);
        var q1 = Load16(ref Unsafe.Add(ref p, stride));
        DoFilter2(p1, ref p0, ref q0, q1, NeedsFilterMask(p1, p0, q0, q1, threshold));
        Store16(ref Unsafe.Subtract(ref p, stride), p0);
        Store16(ref p, q0);
    }

    // The simple filter of the edge left of column p (16 rows)
    private static void SimpleHFilter16Vector(
        ref byte p,
        int stride,
        int threshold)
    {
        ref byte top = ref Unsafe.Subtract(ref p, 4);
        ref byte bottom = ref Unsafe.Add(ref top, 8 * stride);
        var c = LoadColumns(ref top, ref bottom, stride);
        DoFilter2(c.C2, ref c.C3, ref c.C4, c.C5, NeedsFilterMask(c.C2, c.C3, c.C4, c.C5, threshold));
        StoreColumns(ref top, ref bottom, stride, c);
    }

    // A complex macroblock edge above row p: 16 columns of luma
    private static void VFilter16Vector(
        ref byte p,
        int stride,
        int threshold,
        int innerThreshold,
        int hevThreshold)
    {
        var p3 = Load16(ref Unsafe.Subtract(ref p, 4 * stride));
        var p2 = Load16(ref Unsafe.Subtract(ref p, 3 * stride));
        var p1 = Load16(ref Unsafe.Subtract(ref p, 2 * stride));
        var p0 = Load16(ref Unsafe.Subtract(ref p, stride));
        var q0 = Load16(ref p);
        var q1 = Load16(ref Unsafe.Add(ref p, stride));
        var q2 = Load16(ref Unsafe.Add(ref p, 2 * stride));
        var q3 = Load16(ref Unsafe.Add(ref p, 3 * stride));
        var mask = NeedsFilter2Mask(p3, p2, p1, p0, q0, q1, q2, q3, innerThreshold, threshold);
        DoFilter6(ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, mask, HevMask(p1, p0, q0, q1, hevThreshold));
        Store16(ref Unsafe.Subtract(ref p, 3 * stride), p2);
        Store16(ref Unsafe.Subtract(ref p, 2 * stride), p1);
        Store16(ref Unsafe.Subtract(ref p, stride), p0);
        Store16(ref p, q0);
        Store16(ref Unsafe.Add(ref p, stride), q1);
        Store16(ref Unsafe.Add(ref p, 2 * stride), q2);
    }

    // A complex inner edge above row p
    private static void VFilter16InnerVector(
        ref byte p,
        int stride,
        int threshold,
        int innerThreshold,
        int hevThreshold)
    {
        var p3 = Load16(ref Unsafe.Subtract(ref p, 4 * stride));
        var p2 = Load16(ref Unsafe.Subtract(ref p, 3 * stride));
        var p1 = Load16(ref Unsafe.Subtract(ref p, 2 * stride));
        var p0 = Load16(ref Unsafe.Subtract(ref p, stride));
        var q0 = Load16(ref p);
        var q1 = Load16(ref Unsafe.Add(ref p, stride));
        var q2 = Load16(ref Unsafe.Add(ref p, 2 * stride));
        var q3 = Load16(ref Unsafe.Add(ref p, 3 * stride));
        var mask = NeedsFilter2Mask(p3, p2, p1, p0, q0, q1, q2, q3, innerThreshold, threshold);
        DoFilter4(ref p1, ref p0, ref q0, ref q1, mask, HevMask(p1, p0, q0, q1, hevThreshold));
        Store16(ref Unsafe.Subtract(ref p, 2 * stride), p1);
        Store16(ref Unsafe.Subtract(ref p, stride), p0);
        Store16(ref p, q0);
        Store16(ref Unsafe.Add(ref p, stride), q1);
    }

    // A complex edge left of column p, for 16 rows given as two runs of 8 (luma, or U and V): a
    // macroblock edge (inner false) or an inner one
    private static void HFilterVector(
        ref byte top,
        ref byte bottom,
        int stride,
        int threshold,
        int innerThreshold,
        int hevThreshold,
        bool inner)
    {
        ref byte t = ref Unsafe.Subtract(ref top, 4);
        ref byte b = ref Unsafe.Subtract(ref bottom, 4);
        var c = LoadColumns(ref t, ref b, stride);
        var mask = NeedsFilter2Mask(c.C0, c.C1, c.C2, c.C3, c.C4, c.C5, c.C6, c.C7, innerThreshold, threshold);
        var hev = HevMask(c.C2, c.C3, c.C4, c.C5, hevThreshold);
        if (inner)
        {
            DoFilter4(ref c.C2, ref c.C3, ref c.C4, ref c.C5, mask, hev);
        }
        else
        {
            DoFilter6(ref c.C1, ref c.C2, ref c.C3, ref c.C4, ref c.C5, ref c.C6, mask, hev);
        }

        StoreColumns(ref t, ref b, stride, c);
    }

    // A complex edge above row u (and v): 8 columns of each
    private static void VFilter8Vector(
        ref byte u,
        ref byte v,
        int stride,
        int threshold,
        int innerThreshold,
        int hevThreshold,
        bool inner)
    {
        var p3 = Load8x2(ref Unsafe.Subtract(ref u, 4 * stride), ref Unsafe.Subtract(ref v, 4 * stride));
        var p2 = Load8x2(ref Unsafe.Subtract(ref u, 3 * stride), ref Unsafe.Subtract(ref v, 3 * stride));
        var p1 = Load8x2(ref Unsafe.Subtract(ref u, 2 * stride), ref Unsafe.Subtract(ref v, 2 * stride));
        var p0 = Load8x2(ref Unsafe.Subtract(ref u, stride), ref Unsafe.Subtract(ref v, stride));
        var q0 = Load8x2(ref u, ref v);
        var q1 = Load8x2(ref Unsafe.Add(ref u, stride), ref Unsafe.Add(ref v, stride));
        var q2 = Load8x2(ref Unsafe.Add(ref u, 2 * stride), ref Unsafe.Add(ref v, 2 * stride));
        var q3 = Load8x2(ref Unsafe.Add(ref u, 3 * stride), ref Unsafe.Add(ref v, 3 * stride));
        var mask = NeedsFilter2Mask(p3, p2, p1, p0, q0, q1, q2, q3, innerThreshold, threshold);
        var hev = HevMask(p1, p0, q0, q1, hevThreshold);
        if (inner)
        {
            DoFilter4(ref p1, ref p0, ref q0, ref q1, mask, hev);
        }
        else
        {
            DoFilter6(ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, mask, hev);
            Store8x2(ref Unsafe.Subtract(ref u, 3 * stride), ref Unsafe.Subtract(ref v, 3 * stride), p2);
            Store8x2(ref Unsafe.Add(ref u, 2 * stride), ref Unsafe.Add(ref v, 2 * stride), q2);
        }

        Store8x2(ref Unsafe.Subtract(ref u, 2 * stride), ref Unsafe.Subtract(ref v, 2 * stride), p1);
        Store8x2(ref Unsafe.Subtract(ref u, stride), ref Unsafe.Subtract(ref v, stride), p0);
        Store8x2(ref u, ref v, q0);
        Store8x2(ref Unsafe.Add(ref u, stride), ref Unsafe.Add(ref v, stride), q1);
    }

    // DoFilter, with the vector filters
    private static void FilterMacroblockVector(
        byte[] cacheY,
        byte[] cacheU,
        byte[] cacheV,
        int y,
        int yStride,
        int uv,
        int uvStride,
        int filterType,
        int mbX,
        int mbY,
        int limit,
        int ilevel,
        int hevThreshold,
        bool inner)
    {
        ref byte py = ref MemoryMarshal.GetArrayDataReference(cacheY);
        ref byte yp = ref Unsafe.Add(ref py, y);
        if (filterType == 1)
        {
            if (mbX > 0)
            {
                SimpleHFilter16Vector(ref yp, yStride, limit + 4);
            }

            if (inner)
            {
                SimpleHFilter16Vector(ref Unsafe.Add(ref yp, 4), yStride, limit);
                SimpleHFilter16Vector(ref Unsafe.Add(ref yp, 8), yStride, limit);
                SimpleHFilter16Vector(ref Unsafe.Add(ref yp, 12), yStride, limit);
            }

            if (mbY > 0)
            {
                SimpleVFilter16Vector(ref yp, yStride, limit + 4);
            }

            if (inner)
            {
                SimpleVFilter16Vector(ref Unsafe.Add(ref yp, 4 * yStride), yStride, limit);
                SimpleVFilter16Vector(ref Unsafe.Add(ref yp, 8 * yStride), yStride, limit);
                SimpleVFilter16Vector(ref Unsafe.Add(ref yp, 12 * yStride), yStride, limit);
            }

            return;
        }

        ref byte up = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cacheU), uv);
        ref byte vp = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(cacheV), uv);
        ref byte yBottom = ref Unsafe.Add(ref yp, 8 * yStride);
        if (mbX > 0)
        {
            HFilterVector(ref yp, ref yBottom, yStride, limit + 4, ilevel, hevThreshold, inner: false);
            HFilterVector(ref up, ref vp, uvStride, limit + 4, ilevel, hevThreshold, inner: false);
        }

        if (inner)
        {
            HFilterVector(ref Unsafe.Add(ref yp, 4), ref Unsafe.Add(ref yBottom, 4), yStride, limit, ilevel, hevThreshold, inner: true);
            HFilterVector(ref Unsafe.Add(ref yp, 8), ref Unsafe.Add(ref yBottom, 8), yStride, limit, ilevel, hevThreshold, inner: true);
            HFilterVector(ref Unsafe.Add(ref yp, 12), ref Unsafe.Add(ref yBottom, 12), yStride, limit, ilevel, hevThreshold, inner: true);
            HFilterVector(ref Unsafe.Add(ref up, 4), ref Unsafe.Add(ref vp, 4), uvStride, limit, ilevel, hevThreshold, inner: true);
        }

        if (mbY > 0)
        {
            VFilter16Vector(ref yp, yStride, limit + 4, ilevel, hevThreshold);
            VFilter8Vector(ref up, ref vp, uvStride, limit + 4, ilevel, hevThreshold, inner: false);
        }

        if (inner)
        {
            VFilter16InnerVector(ref Unsafe.Add(ref yp, 4 * yStride), yStride, limit, ilevel, hevThreshold);
            VFilter16InnerVector(ref Unsafe.Add(ref yp, 8 * yStride), yStride, limit, ilevel, hevThreshold);
            VFilter16InnerVector(ref Unsafe.Add(ref yp, 12 * yStride), yStride, limit, ilevel, hevThreshold);
            VFilter8Vector(ref Unsafe.Add(ref up, 4 * uvStride), ref Unsafe.Add(ref vp, 4 * uvStride), uvStride, limit, ilevel, hevThreshold, inner: true);
        }
    }
}
