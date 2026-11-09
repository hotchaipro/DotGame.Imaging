// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dsp/upsampling_neon.c and upsampling_sse2.c), modified by Hot Chai
// Games LLC: written in C#, with the runtime's intrinsics. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
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

// The fancy upsampler and YUV to RGB 16 pixels at a time: libwebp's vector forms, which give its C's
// pixels exactly. The rows read here have 16 bytes after them to spare, so every load is a vector's.
internal static partial class Vp8Decoder
{
    // The row buffers' spare bytes
    private const int RowPadding = 16;

    private static bool VectorUpsampling => AdvSimd.Arm64.IsSupported || Sse2.IsSupported;

    // Eight chroma samples' upsampled pairs: from a (the row above, at i), b (above, at i + 1), c (this
    // row, at i) and d (this row, at i + 1), the 16 samples of the top output row and of the bottom one
    // (UPSAMPLE_16PIXELS): diag1 = (a + 3b + 3c + d) / 8 and diag2 = (3a + b + c + 3d) / 8, and each
    // sample the rounded average of its nearest one and a diagonal
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Upsample16(
        ref byte top,
        ref byte current,
        out Vector128<byte> topRow,
        out Vector128<byte> bottomRow)
    {
        var a = Vector128.WidenLower(Vector128.LoadUnsafe(ref top));
        var b = Vector128.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref top, 1)));
        var c = Vector128.WidenLower(Vector128.LoadUnsafe(ref current));
        var d = Vector128.WidenLower(Vector128.LoadUnsafe(ref Unsafe.Add(ref current, 1)));
        var ad = a + d;
        var bc = b + c;
        var abcd = ad + bc;
        var diag2 = (abcd + (ad << 1)) >> 3;
        var diag1 = (abcd + (bc << 1)) >> 3;
        var one = Vector128.Create((ushort)1);
        var pa = (a + diag1 + one) >> 1;
        var pb = (b + diag2 + one) >> 1;
        var pc = (c + diag2 + one) >> 1;
        var pd = (d + diag1 + one) >> 1;

        // Each pair interleaved, the first in the low byte
        topRow = (pa | (pb << 8)).AsByte();
        bottomRow = (pc | (pd << 8)).AsByte();
    }

    // Upsample16 for 16 chroma samples (32 pixels of each row): the diagonals in 16 bits, the averages
    // in bytes (a rounded halving add: NEON's urhadd, SSE2's pavgb)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Upsample32(
        ref byte top,
        ref byte current,
        out Vector128<byte> topLow,
        out Vector128<byte> topHigh,
        out Vector128<byte> bottomLow,
        out Vector128<byte> bottomHigh)
    {
        var a = Vector128.LoadUnsafe(ref top);
        var b = Vector128.LoadUnsafe(ref Unsafe.Add(ref top, 1));
        var c = Vector128.LoadUnsafe(ref current);
        var d = Vector128.LoadUnsafe(ref Unsafe.Add(ref current, 1));
        var adLow = Vector128.WidenLower(a) + Vector128.WidenLower(d);
        var adHigh = Vector128.WidenUpper(a) + Vector128.WidenUpper(d);
        var bcLow = Vector128.WidenLower(b) + Vector128.WidenLower(c);
        var bcHigh = Vector128.WidenUpper(b) + Vector128.WidenUpper(c);
        var abcdLow = adLow + bcLow;
        var abcdHigh = adHigh + bcHigh;
        var diag2 = Vector128.Narrow((abcdLow + (adLow << 1)) >> 3, (abcdHigh + (adHigh << 1)) >> 3);
        var diag1 = Vector128.Narrow((abcdLow + (bcLow << 1)) >> 3, (abcdHigh + (bcHigh << 1)) >> 3);
        var pa = RoundedAverage(a, diag1);
        var pb = RoundedAverage(b, diag2);
        var pc = RoundedAverage(c, diag2);
        var pd = RoundedAverage(d, diag1);
        topLow = Zip8Low(pa, pb);
        topHigh = Zip8High(pa, pb);
        bottomLow = Zip8Low(pc, pd);
        bottomHigh = Zip8High(pc, pd);
    }

    // (a + b + 1) >> 1 for bytes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> RoundedAverage(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.FusedAddRoundedHalving(a, b) : Sse2.Average(a, b);
    }

    // (x * k) >> 8 for x a byte, in 16 bits (MultHi): NEON's doubling multiply of x << 7, or SSE2's
    // unsigned high multiply of x << 8
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> MultHi(
        Vector128<ushort> x,
        short k)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.MultiplyDoublingSaturateHigh((x << 7).AsInt16(), Vector128.Create(k));
        }

        return Sse2.MultiplyHigh(x << 8, Vector128.Create((ushort)k)).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> AddSat16(
        Vector128<short> a,
        Vector128<short> b)
    {
        return AdvSimd.Arm64.IsSupported ? AdvSimd.AddSaturate(a, b) : Sse2.AddSaturate(a, b);
    }

    // Eight pixels' red, green and blue in 16 bits, before the final shift (VP8YuvToRgb's sums, with
    // blue's 33050 as 282 + 32768 and its sum saturated, as libwebp's vector code does)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void YuvToRgb8(
        Vector128<ushort> y,
        Vector128<ushort> u,
        Vector128<ushort> v,
        out Vector128<short> r,
        out Vector128<short> g,
        out Vector128<short> b)
    {
        var y1 = MultHi(y, 19077);
        r = y1 + MultHi(v, 26149) - Vector128.Create((short)14234);
        g = y1 - MultHi(u, 6419) - MultHi(v, 13320) + Vector128.Create((short)8708);
        b = AddSat16(y1 + MultHi(u, 282) - Vector128.Create((short)17685), (u << 7).AsInt16());
    }

    // >> 6, clamped to a byte, both halves into one vector
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ToBytes(
        Vector128<short> low,
        Vector128<short> high)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ShiftRightArithmeticNarrowingSaturateUnsignedLower(low, 6), high >> 6);
        }

        return Sse2.PackUnsignedSaturate(low >> 6, high >> 6);
    }

    // Sixteen pixels of a row: luma at y, the row's upsampled chroma, to RGBA at dst (CONVERT8)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertRow16(
        ref byte y,
        Vector128<byte> u,
        Vector128<byte> v,
        ref byte dst)
    {
        var luma = Vector128.LoadUnsafe(ref y);
        YuvToRgb8(Vector128.WidenLower(luma), Vector128.WidenLower(u), Vector128.WidenLower(v), out var rLow, out var gLow, out var bLow);
        YuvToRgb8(Vector128.WidenUpper(luma), Vector128.WidenUpper(u), Vector128.WidenUpper(v), out var rHigh, out var gHigh, out var bHigh);
        var r = ToBytes(rLow, rHigh);
        var g = ToBytes(gLow, gHigh);
        var b = ToBytes(bLow, bHigh);
        var a = Vector128.Create((byte)255);
        var rgLow = Zip8Low(r, g);
        var rgHigh = Zip8High(r, g);
        var baLow = Zip8Low(b, a);
        var baHigh = Zip8High(b, a);
        Zip16Low(rgLow, baLow).StoreUnsafe(ref dst);
        Zip16High(rgLow, baLow).StoreUnsafe(ref Unsafe.Add(ref dst, 16));
        Zip16Low(rgHigh, baHigh).StoreUnsafe(ref Unsafe.Add(ref dst, 32));
        Zip16High(rgHigh, baHigh).StoreUnsafe(ref Unsafe.Add(ref dst, 48));
    }

    // The vector part of a pair of rows: the first pixel as the C does it, then blocks of 16 pixels
    // (from 8 chroma samples and the one after them); the pixel the C loop goes on from
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int UpsampleLinePairVector(
        ReadOnlySpan<byte> topY,
        ReadOnlySpan<byte> bottomY,
        ReadOnlySpan<byte> topU,
        ReadOnlySpan<byte> topV,
        ReadOnlySpan<byte> curU,
        ReadOnlySpan<byte> curV,
        Span<byte> topDst,
        Span<byte> bottomDst,
        int length)
    {
        int uvLength = (length + 1) >> 1;
        int blocks = (uvLength - 1) >> 3;
        ref byte ty = ref MemoryMarshal.GetReference(topY);
        ref byte by = ref MemoryMarshal.GetReference(bottomY);
        ref byte tu = ref MemoryMarshal.GetReference(topU);
        ref byte tv = ref MemoryMarshal.GetReference(topV);
        ref byte cu = ref MemoryMarshal.GetReference(curU);
        ref byte cv = ref MemoryMarshal.GetReference(curV);
        ref byte td = ref MemoryMarshal.GetReference(topDst);
        ref byte bd = ref MemoryMarshal.GetReference(bottomDst);
        bool hasBottom = !bottomY.IsEmpty;

        // Blocks of 16 chroma samples (32 pixels), then of 8
        int block = 0;
        for (; block + 2 <= blocks; block += 2)
        {
            int c = 8 * block;
            int x = 1 + (16 * block);
            Upsample32(ref Unsafe.Add(ref tu, c), ref Unsafe.Add(ref cu, c), out var topULow, out var topUHigh, out var bottomULow, out var bottomUHigh);
            Upsample32(ref Unsafe.Add(ref tv, c), ref Unsafe.Add(ref cv, c), out var topVLow, out var topVHigh, out var bottomVLow, out var bottomVHigh);
            ConvertRow16(ref Unsafe.Add(ref ty, x), topULow, topVLow, ref Unsafe.Add(ref td, x * 4));
            ConvertRow16(ref Unsafe.Add(ref ty, x + 16), topUHigh, topVHigh, ref Unsafe.Add(ref td, (x + 16) * 4));
            if (hasBottom)
            {
                ConvertRow16(ref Unsafe.Add(ref by, x), bottomULow, bottomVLow, ref Unsafe.Add(ref bd, x * 4));
                ConvertRow16(ref Unsafe.Add(ref by, x + 16), bottomUHigh, bottomVHigh, ref Unsafe.Add(ref bd, (x + 16) * 4));
            }
        }

        for (; block < blocks; block++)
        {
            int c = 8 * block;
            int x = 1 + (16 * block);
            Upsample16(ref Unsafe.Add(ref tu, c), ref Unsafe.Add(ref cu, c), out var topU16, out var bottomU16);
            Upsample16(ref Unsafe.Add(ref tv, c), ref Unsafe.Add(ref cv, c), out var topV16, out var bottomV16);
            ConvertRow16(ref Unsafe.Add(ref ty, x), topU16, topV16, ref Unsafe.Add(ref td, x * 4));
            if (hasBottom)
            {
                ConvertRow16(ref Unsafe.Add(ref by, x), bottomU16, bottomV16, ref Unsafe.Add(ref bd, x * 4));
            }
        }

        return (8 * blocks) + 1;
    }
}
