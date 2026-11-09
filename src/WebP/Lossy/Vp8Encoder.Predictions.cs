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
    // A size x size block (4, 8 or 16) of one value
    private static void FillBlock(
        Span<byte> dst,
        int d,
        int value,
        int size)
    {
        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), d);
        if (size == 16)
        {
            var v = Vector128.Create((byte)value);
            for (int j = 0; j < 16; j++)
            {
                v.StoreUnsafe(ref p, (nuint)(j * Bps));
            }
        }
        else if (size == 8)
        {
            var v = Vector64.Create((byte)value);
            for (int j = 0; j < 8; j++)
            {
                v.StoreUnsafe(ref p, (nuint)(j * Bps));
            }
        }
        else
        {
            uint v = 0x01010101u * (byte)value;
            for (int j = 0; j < 4; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, j * Bps), v);
            }
        }
    }

    // VerticalPred, HorizontalPred, TrueMotion and DCMode: top or left absent (null in the C) is
    // topAt or leftAt < 0
    private static void VerticalPred(
        Span<byte> dst,
        int d,
        ReadOnlySpan<byte> top,
        int topAt,
        int size)
    {
        if (topAt < 0)
        {
            FillBlock(dst, d, 127, size);
            return;
        }

        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), d);
        ref byte t = ref Unsafe.Add(ref MemoryMarshal.GetReference(top), topAt);
        if (size == 16)
        {
            var v = Vector128.LoadUnsafe(ref t);
            for (int j = 0; j < 16; j++)
            {
                v.StoreUnsafe(ref p, (nuint)(j * Bps));
            }
        }
        else
        {
            var v = Vector64.LoadUnsafe(ref t);
            for (int j = 0; j < 8; j++)
            {
                v.StoreUnsafe(ref p, (nuint)(j * Bps));
            }
        }
    }

    private static void HorizontalPred(
        Span<byte> dst,
        int d,
        ReadOnlySpan<byte> left,
        int leftAt,
        int size)
    {
        if (leftAt < 0)
        {
            FillBlock(dst, d, 129, size);
            return;
        }

        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), d);
        ref byte l = ref Unsafe.Add(ref MemoryMarshal.GetReference(left), leftAt);
        if (size == 16)
        {
            for (int j = 0; j < 16; j++)
            {
                Vector128.Create(Unsafe.Add(ref l, j)).StoreUnsafe(ref p, (nuint)(j * Bps));
            }
        }
        else
        {
            for (int j = 0; j < 8; j++)
            {
                Vector64.Create(Unsafe.Add(ref l, j)).StoreUnsafe(ref p, (nuint)(j * Bps));
            }
        }
    }

    private static void TrueMotion(
        Span<byte> dst,
        int d,
        ReadOnlySpan<byte> left,
        int leftAt,
        ReadOnlySpan<byte> top,
        int topAt,
        int size)
    {
        if (leftAt < 0)
        {
            if (topAt >= 0)
            {
                VerticalPred(dst, d, top, topAt, size);
            }
            else
            {
                FillBlock(dst, d, 129, size);
            }

            return;
        }

        if (topAt < 0)
        {
            HorizontalPred(dst, d, left, leftAt, size);
            return;
        }

        // top[x] + left[y] - top-left, clipped: in shorts, clamped, narrowed
        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), d);
        ref byte l = ref Unsafe.Add(ref MemoryMarshal.GetReference(left), leftAt);
        ref byte t = ref Unsafe.Add(ref MemoryMarshal.GetReference(top), topAt);
        int topLeft = Unsafe.Subtract(ref l, 1);
        var max = Vector128.Create((short)255);
        if (size == 16)
        {
            var (t0, t1) = Vector128.Widen(Vector128.LoadUnsafe(ref t));
            for (int y = 0; y < 16; y++)
            {
                var delta = Vector128.Create((short)(Unsafe.Add(ref l, y) - topLeft));
                var lo = Vector128.Min(Vector128.Max(t0.AsInt16() + delta, Vector128<short>.Zero), max);
                var hi = Vector128.Min(Vector128.Max(t1.AsInt16() + delta, Vector128<short>.Zero), max);
                Vector128.Narrow(lo.AsUInt16(), hi.AsUInt16()).StoreUnsafe(ref p, (nuint)(y * Bps));
            }
        }
        else
        {
            var t0 = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref t)).AsByte()).AsInt16();
            for (int y = 0; y < 8; y++)
            {
                var delta = Vector128.Create((short)(Unsafe.Add(ref l, y) - topLeft));
                var row = Vector128.Min(Vector128.Max(t0 + delta, Vector128<short>.Zero), max);
                Vector128.Narrow(row.AsUInt16(), row.AsUInt16()).GetLower().StoreUnsafe(ref p, (nuint)(y * Bps));
            }
        }
    }

    private static void DcMode(
        Span<byte> dst,
        int d,
        ReadOnlySpan<byte> left,
        int leftAt,
        ReadOnlySpan<byte> top,
        int topAt,
        int size,
        int round,
        int shift)
    {
        int dc = 0;
        if (topAt >= 0)
        {
            for (int j = 0; j < size; j++)
            {
                dc += top[topAt + j];
            }

            if (leftAt >= 0)
            {
                for (int j = 0; j < size; j++)
                {
                    dc += left[leftAt + j];
                }
            }
            else
            {
                dc += dc;
            }

            dc = (dc + round) >> shift;
        }
        else if (leftAt >= 0)
        {
            for (int j = 0; j < size; j++)
            {
                dc += left[leftAt + j];
            }

            dc += dc;
            dc = (dc + round) >> shift;
        }
        else
        {
            dc = 0x80;
        }

        FillBlock(dst, d, dc, size);
    }

    // IntraChromaPreds_C: U at dst, V at dst + 8 (left: U's at leftAt, V's at leftAt + 16)
    private static void IntraChromaPreds(
        Span<byte> dst,
        ReadOnlySpan<byte> left,
        int leftAt,
        ReadOnlySpan<byte> top,
        int topAt)
    {
        DcMode(dst, C8DC8, left, leftAt, top, topAt, 8, 8, 4);
        VerticalPred(dst, C8VE8, top, topAt, 8);
        HorizontalPred(dst, C8HE8, left, leftAt, 8);
        TrueMotion(dst, C8TM8, left, leftAt, top, topAt, 8);
        int topV = (topAt >= 0) ? topAt + 8 : -1;
        int leftV = (leftAt >= 0) ? leftAt + 16 : -1;
        DcMode(dst, C8DC8 + 8, left, leftV, top, topV, 8, 8, 4);
        VerticalPred(dst, C8VE8 + 8, top, topV, 8);
        HorizontalPred(dst, C8HE8 + 8, left, leftV, 8);
        TrueMotion(dst, C8TM8 + 8, left, leftV, top, topV, 8);
    }

    private static void Intra16Preds(
        Span<byte> dst,
        ReadOnlySpan<byte> left,
        int leftAt,
        ReadOnlySpan<byte> top,
        int topAt)
    {
        DcMode(dst, I16DC16, left, leftAt, top, topAt, 16, 16, 5);
        VerticalPred(dst, I16VE16, top, topAt, 16);
        HorizontalPred(dst, I16HE16, left, leftAt, 16);
        TrueMotion(dst, I16TM16, left, leftAt, top, topAt, 16);
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

    // Intra4Preds_C: all ten 4x4 predictions, from the boundary at t (top[-1] the top-left, top[-2]
    // to top[-5] the left column, top[0..7] the top row and its right)
    private static void Intra4Preds(
        Span<byte> dst,
        ReadOnlySpan<byte> b,
        int t)
    {
        int x0 = b[t - 1], i0 = b[t - 2], j0 = b[t - 3], k0 = b[t - 4], l0 = b[t - 5];
        int a0 = b[t], b0 = b[t + 1], c0 = b[t + 2], d0 = b[t + 3], e0 = b[t + 4], f0 = b[t + 5], g0 = b[t + 6], h0 = b[t + 7];

        // DC4
        {
            int dc = 4;
            for (int i = 0; i < 4; i++)
            {
                dc += b[t + i] + b[t - 5 + i];
            }

            FillBlock(dst, I4DC4, dc >> 3, 4);
        }

        // TM4 (clipped by clamping shorts, as the 8 and 16 forms)
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte tm = ref Unsafe.Add(ref MemoryMarshal.GetReference(dst), I4TM4);
            var top = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in b[t]))).AsByte()).AsInt16();
            var max = Vector128.Create((short)255);
            for (int y = 0; y < 4; y++)
            {
                var row = Vector128.Min(Vector128.Max(top + Vector128.Create((short)(b[t - 2 - y] - x0)), Vector128<short>.Zero), max);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref tm, y * Bps), Vector128.Narrow(row.AsUInt16(), row.AsUInt16()).AsUInt32().ToScalar());
            }
        }
        else
        {
            for (int y = 0; y < 4; y++)
            {
                int l = b[t - 2 - y] - x0;
                for (int x = 0; x < 4; x++)
                {
                    dst[I4TM4 + (y * Bps) + x] = Clip8(b[t + x] + l);
                }
            }
        }

        // VE4 to HU4: the distinct averages once (AVG3 is symmetric), each row written as a word
        ref byte d = ref MemoryMarshal.GetReference(dst);
        int kll = Avg3(k0, l0, l0), jkl = Avg3(j0, k0, l0), ijk = Avg3(i0, j0, k0), xij = Avg3(x0, i0, j0);
        int axi = Avg3(a0, x0, i0), bax = Avg3(b0, a0, x0), abc = Avg3(a0, b0, c0), bcd = Avg3(b0, c0, d0);
        int cde = Avg3(c0, d0, e0), def = Avg3(d0, e0, f0), efg = Avg3(e0, f0, g0), fgh = Avg3(f0, g0, h0);
        int ghh = Avg3(g0, h0, h0);
        int xa = Avg2(x0, a0), ab = Avg2(a0, b0), bc = Avg2(b0, c0), cd = Avg2(c0, d0), de = Avg2(d0, e0);
        int ix = Avg2(i0, x0), ij = Avg2(i0, j0), jk = Avg2(j0, k0), kl = Avg2(k0, l0);

        // VE4
        {
            uint row = Word(bax, abc, bcd, cde);
            ref byte p = ref Unsafe.Add(ref d, I4VE4);
            Unsafe.WriteUnaligned(ref p, row);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Bps), row);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 2 * Bps), row);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 3 * Bps), row);
        }

        // HE4
        {
            ref byte p = ref Unsafe.Add(ref d, I4HE4);
            Unsafe.WriteUnaligned(ref p, 0x01010101u * (uint)xij);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Bps), 0x01010101u * (uint)ijk);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 2 * Bps), 0x01010101u * (uint)jkl);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 3 * Bps), 0x01010101u * (uint)kll);
        }

        Rows(ref Unsafe.Add(ref d, I4RD4), Word(axi, bax, abc, bcd), Word(xij, axi, bax, abc), Word(ijk, xij, axi, bax), Word(jkl, ijk, xij, axi));
        Rows(ref Unsafe.Add(ref d, I4VR4), Word(xa, ab, bc, cd), Word(axi, bax, abc, bcd), Word(xij, xa, ab, bc), Word(ijk, axi, bax, abc));
        Rows(ref Unsafe.Add(ref d, I4LD4), Word(abc, bcd, cde, def), Word(bcd, cde, def, efg), Word(cde, def, efg, fgh), Word(def, efg, fgh, ghh));
        Rows(ref Unsafe.Add(ref d, I4VL4), Word(ab, bc, cd, de), Word(abc, bcd, cde, def), Word(bc, cd, de, efg), Word(bcd, cde, def, fgh));
        int ji = Avg2(j0, i0);
        int kj = Avg2(k0, j0), lk = Avg2(l0, k0);
        Rows(ref Unsafe.Add(ref d, I4HD4), Word(ix, axi, bax, abc), Word(ji, xij, ix, axi), Word(kj, ijk, ji, xij), Word(lk, jkl, kj, ijk));
        Rows(ref Unsafe.Add(ref d, I4HU4), Word(ij, ijk, jk, jkl), Word(jk, jkl, kl, kll), Word(kl, kll, l0, l0), Word(l0, l0, l0, l0));
    }
}
