// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, with the color
// conversion's arithmetic in place of its tables, and the portable vector forms new.
//
// The color conversion, the downsampling and the edge padding follow jccolor.c, jccolext.c,
// jcsample.c and jcprepct.c, which carry these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1996, Thomas G. Lane.
//   libjpeg-turbo Modifications:
//   Copyright 2009 Pierre Ossman <ossman@cendio.se> for Cendio AB
//   Copyright (C) 2009-2012, 2014-2015, 2022-2025, D. R. Commander.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.
//
// The Arm64 color conversion and downsampling are ported from simd/arm/aarch64/jccolext-neon.c,
// simd/arm/jcgryext-neon.c and simd/arm/jcsample-neon.c, which carry these notices:
//   Copyright (C) 2020, Arm Limited.
//   Copyright (C) 2020, 2025, D. R. Commander.
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
    // jccolor.c's fixed-point factors, 16 fraction bits (FIX(0.29900) and so on), which its tables hold
    // multiplied by each sample value
    private const ushort FixRY = 19595;
    private const ushort FixGY = 38470;
    private const ushort FixBY = 7471;
    private const ushort FixRCb = 11059;
    private const ushort FixGCb = 21709;
    private const ushort FixHalf = 32768;
    private const ushort FixGCr = 27439;
    private const ushort FixBCr = 5329;

    // Y's rounding (ONE_HALF), and Cb's and Cr's offset with their rounding (CBCR_OFFSET + ONE_HALF - 1)
    private const int RoundY = 1 << 15;
    private const int OffsetCbCr = (128 << 16) + (1 << 15) - 1;

    /// <summary>
    /// The samples of an iMCU row: the pixels converted to the components' planes (jccolor.c), the
    /// chroma downsampled (jcsample.c), and the right and bottom edges padded as jcprepct.c and
    /// jcsample.c pad them.
    /// </summary>
    private void PrepareRow(
        ReadOnlySpan<byte> rgba,
        int row)
    {
        int rowHeight = this.maxV * 8;
        int top = row * rowHeight;
        int rows = Math.Min(rowHeight, this.height - top);
        int stride = this.sampleStride;
        var y = this.components[0].Plane;
        if (this.components.Length == 1)
        {
            for (int i = 0; i < rows; i++)
            {
                var source = rgba.Slice((top + i) * this.width * 4, this.width * 4);
                ConvertGrayRow(source, y.AsSpan(i * stride, stride), this.width);
            }

            ReplicateRows(y, stride, rows, rowHeight);
            return;
        }

        var cb = this.fullCb ?? this.components[1].Plane;
        var cr = this.fullCr ?? this.components[2].Plane;
        for (int i = 0; i < rows; i++)
        {
            var source = rgba.Slice((top + i) * this.width * 4, this.width * 4);
            ConvertYccRow(source, y.AsSpan(i * stride, stride), cb.AsSpan(i * stride, stride), cr.AsSpan(i * stride, stride), this.width);
        }

        // The luma, at full size, is padded to the iMCU row's height with copies of its last row
        // (a copy of a copy, as jcprepct.c pads first the row group, then the downsampled rows)
        ReplicateRows(y, stride, rows, rowHeight);
        if (this.fullCb == null)
        {
            ReplicateRows(cb, stride, rows, rowHeight);
            ReplicateRows(cr, stride, rows, rowHeight);
            return;
        }

        // The last row group of full-size chroma is padded with copies of its last row, and the rows
        // the groups make are padded to the downsampled iMCU row with copies of the last
        int groups = DivideRoundUp(rows, this.maxV);
        ReplicateRows(cb, stride, rows, groups * this.maxV);
        ReplicateRows(cr, stride, rows, groups * this.maxV);
        for (int ci = 1; ci < 3; ci++)
        {
            var c = this.components[ci];
            var full = (ci == 1) ? cb : cr;
            int columns = c.WidthInBlocks * 8;
            for (int g = 0; g < groups; g++)
            {
                var target = c.Plane.AsSpan(g * c.PlaneStride, columns);
                if (this.maxV == 2)
                {
                    DownsampleH2V2(full.AsSpan(g * 2 * stride, stride), full.AsSpan(((g * 2) + 1) * stride, stride), target);
                }
                else
                {
                    DownsampleH2V1(full.AsSpan(g * stride, stride), target);
                }
            }

            ReplicateRows(c.Plane, c.PlaneStride, groups, c.V * 8);
        }
    }

    // expand_bottom_edge: rows from..to - 1 copies of row from - 1
    private static void ReplicateRows(
        byte[] plane,
        int stride,
        int from,
        int to)
    {
        var last = plane.AsSpan((from - 1) * stride, stride);
        for (int i = from; i < to; i++)
        {
            last.CopyTo(plane.AsSpan(i * stride, stride));
        }
    }

    // rgb_ycc_convert (jsimd_rgb_ycc_convert_neon on Arm64), then expand_right_edge to the row's end
    private static void ConvertYccRow(
        ReadOnlySpan<byte> rgba,
        Span<byte> y,
        Span<byte> cb,
        Span<byte> cr,
        int width)
    {
        ref byte source = ref MemoryMarshal.GetReference(rgba);
        ref byte yRef = ref MemoryMarshal.GetReference(y);
        ref byte cbRef = ref MemoryMarshal.GetReference(cb);
        ref byte crRef = ref MemoryMarshal.GetReference(cr);
        int x = 0;
        if (AdvSimd.Arm64.IsSupported)
        {
            var offset = Vector128.Create((uint)OffsetCbCr);
            for (; x + 16 <= width; x += 16)
            {
                Deinterleave(ref Unsafe.Add(ref source, x * 4), out var r, out var g, out var b);
                var rl = AdvSimd.ZeroExtendWideningLower(r.GetLower());
                var rh = AdvSimd.ZeroExtendWideningUpper(r);
                var gl = AdvSimd.ZeroExtendWideningLower(g.GetLower());
                var gh = AdvSimd.ZeroExtendWideningUpper(g);
                var bl = AdvSimd.ZeroExtendWideningLower(b.GetLower());
                var bh = AdvSimd.ZeroExtendWideningUpper(b);
                LumaArm64(rl, gl, bl, rh, gh, bh).StoreUnsafe(ref yRef, (nuint)x);

                var cbll = AdvSimd.MultiplyWideningLowerAndSubtract(offset, rl.GetLower(), Vector64.Create(FixRCb));
                cbll = AdvSimd.MultiplyWideningLowerAndSubtract(cbll, gl.GetLower(), Vector64.Create(FixGCb));
                cbll = AdvSimd.MultiplyWideningLowerAndAdd(cbll, bl.GetLower(), Vector64.Create(FixHalf));
                var cblh = AdvSimd.MultiplyWideningUpperAndSubtract(offset, rl, Vector128.Create(FixRCb));
                cblh = AdvSimd.MultiplyWideningUpperAndSubtract(cblh, gl, Vector128.Create(FixGCb));
                cblh = AdvSimd.MultiplyWideningUpperAndAdd(cblh, bl, Vector128.Create(FixHalf));
                var cbhl = AdvSimd.MultiplyWideningLowerAndSubtract(offset, rh.GetLower(), Vector64.Create(FixRCb));
                cbhl = AdvSimd.MultiplyWideningLowerAndSubtract(cbhl, gh.GetLower(), Vector64.Create(FixGCb));
                cbhl = AdvSimd.MultiplyWideningLowerAndAdd(cbhl, bh.GetLower(), Vector64.Create(FixHalf));
                var cbhh = AdvSimd.MultiplyWideningUpperAndSubtract(offset, rh, Vector128.Create(FixRCb));
                cbhh = AdvSimd.MultiplyWideningUpperAndSubtract(cbhh, gh, Vector128.Create(FixGCb));
                cbhh = AdvSimd.MultiplyWideningUpperAndAdd(cbhh, bh, Vector128.Create(FixHalf));
                NarrowArm64(cbll, cblh, cbhl, cbhh).StoreUnsafe(ref cbRef, (nuint)x);

                var crll = AdvSimd.MultiplyWideningLowerAndAdd(offset, rl.GetLower(), Vector64.Create(FixHalf));
                crll = AdvSimd.MultiplyWideningLowerAndSubtract(crll, gl.GetLower(), Vector64.Create(FixGCr));
                crll = AdvSimd.MultiplyWideningLowerAndSubtract(crll, bl.GetLower(), Vector64.Create(FixBCr));
                var crlh = AdvSimd.MultiplyWideningUpperAndAdd(offset, rl, Vector128.Create(FixHalf));
                crlh = AdvSimd.MultiplyWideningUpperAndSubtract(crlh, gl, Vector128.Create(FixGCr));
                crlh = AdvSimd.MultiplyWideningUpperAndSubtract(crlh, bl, Vector128.Create(FixBCr));
                var crhl = AdvSimd.MultiplyWideningLowerAndAdd(offset, rh.GetLower(), Vector64.Create(FixHalf));
                crhl = AdvSimd.MultiplyWideningLowerAndSubtract(crhl, gh.GetLower(), Vector64.Create(FixGCr));
                crhl = AdvSimd.MultiplyWideningLowerAndSubtract(crhl, bh.GetLower(), Vector64.Create(FixBCr));
                var crhh = AdvSimd.MultiplyWideningUpperAndAdd(offset, rh, Vector128.Create(FixHalf));
                crhh = AdvSimd.MultiplyWideningUpperAndSubtract(crhh, gh, Vector128.Create(FixGCr));
                crhh = AdvSimd.MultiplyWideningUpperAndSubtract(crhh, bh, Vector128.Create(FixBCr));
                NarrowArm64(crll, crlh, crhl, crhh).StoreUnsafe(ref crRef, (nuint)x);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            // 4 pixels a vector, their channels in 32-bit lanes
            for (; x + 16 <= width; x += 16)
            {
                Unpack(ref Unsafe.Add(ref source, x * 4), out var r0, out var g0, out var b0);
                Unpack(ref Unsafe.Add(ref source, (x * 4) + 16), out var r1, out var g1, out var b1);
                Unpack(ref Unsafe.Add(ref source, (x * 4) + 32), out var r2, out var g2, out var b2);
                Unpack(ref Unsafe.Add(ref source, (x * 4) + 48), out var r3, out var g3, out var b3);
                Pack(Luma(r0, g0, b0), Luma(r1, g1, b1), Luma(r2, g2, b2), Luma(r3, g3, b3)).StoreUnsafe(ref yRef, (nuint)x);
                Pack(Cb(r0, g0, b0), Cb(r1, g1, b1), Cb(r2, g2, b2), Cb(r3, g3, b3)).StoreUnsafe(ref cbRef, (nuint)x);
                Pack(Cr(r0, g0, b0), Cr(r1, g1, b1), Cr(r2, g2, b2), Cr(r3, g3, b3)).StoreUnsafe(ref crRef, (nuint)x);
            }
        }

        for (; x < width; x++)
        {
            int r = rgba[x * 4];
            int g = rgba[(x * 4) + 1];
            int b = rgba[(x * 4) + 2];
            y[x] = (byte)(((FixRY * r) + (FixGY * g) + (FixBY * b) + RoundY) >> 16);
            cb[x] = (byte)((OffsetCbCr - (FixRCb * r) - (FixGCb * g) + (FixHalf * b)) >> 16);
            cr[x] = (byte)((OffsetCbCr + (FixHalf * r) - (FixGCr * g) - (FixBCr * b)) >> 16);
        }

        y[(width - 1)..].Fill(y[width - 1]);
        cb[(width - 1)..].Fill(cb[width - 1]);
        cr[(width - 1)..].Fill(cr[width - 1]);
    }

    // rgb_gray_convert (jsimd_rgb_gray_convert_neon on Arm64), then expand_right_edge
    private static void ConvertGrayRow(
        ReadOnlySpan<byte> rgba,
        Span<byte> y,
        int width)
    {
        ref byte source = ref MemoryMarshal.GetReference(rgba);
        ref byte yRef = ref MemoryMarshal.GetReference(y);
        int x = 0;
        if (AdvSimd.Arm64.IsSupported)
        {
            for (; x + 16 <= width; x += 16)
            {
                Deinterleave(ref Unsafe.Add(ref source, x * 4), out var r, out var g, out var b);
                var luma = LumaArm64(
                    AdvSimd.ZeroExtendWideningLower(r.GetLower()),
                    AdvSimd.ZeroExtendWideningLower(g.GetLower()),
                    AdvSimd.ZeroExtendWideningLower(b.GetLower()),
                    AdvSimd.ZeroExtendWideningUpper(r),
                    AdvSimd.ZeroExtendWideningUpper(g),
                    AdvSimd.ZeroExtendWideningUpper(b));
                luma.StoreUnsafe(ref yRef, (nuint)x);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; x + 16 <= width; x += 16)
            {
                Unpack(ref Unsafe.Add(ref source, x * 4), out var r0, out var g0, out var b0);
                Unpack(ref Unsafe.Add(ref source, (x * 4) + 16), out var r1, out var g1, out var b1);
                Unpack(ref Unsafe.Add(ref source, (x * 4) + 32), out var r2, out var g2, out var b2);
                Unpack(ref Unsafe.Add(ref source, (x * 4) + 48), out var r3, out var g3, out var b3);
                Pack(Luma(r0, g0, b0), Luma(r1, g1, b1), Luma(r2, g2, b2), Luma(r3, g3, b3)).StoreUnsafe(ref yRef, (nuint)x);
            }
        }

        for (; x < width; x++)
        {
            int r = rgba[x * 4];
            int g = rgba[(x * 4) + 1];
            int b = rgba[(x * 4) + 2];
            y[x] = (byte)(((FixRY * r) + (FixGY * g) + (FixBY * b) + RoundY) >> 16);
        }

        y[(width - 1)..].Fill(y[width - 1]);
    }

    // 16 RGBA pixels as 16 reds, greens and blues (vld4q_u8's deinterleaving, by unzips)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Deinterleave(
        ref byte pixels,
        out Vector128<byte> r,
        out Vector128<byte> g,
        out Vector128<byte> b)
    {
        var p0 = Vector128.LoadUnsafe(ref pixels);
        var p1 = Vector128.LoadUnsafe(ref pixels, 16);
        var p2 = Vector128.LoadUnsafe(ref pixels, 32);
        var p3 = Vector128.LoadUnsafe(ref pixels, 48);
        var rb01 = AdvSimd.Arm64.UnzipEven(p0, p1);
        var ga01 = AdvSimd.Arm64.UnzipOdd(p0, p1);
        var rb23 = AdvSimd.Arm64.UnzipEven(p2, p3);
        var ga23 = AdvSimd.Arm64.UnzipOdd(p2, p3);
        r = AdvSimd.Arm64.UnzipEven(rb01, rb23);
        b = AdvSimd.Arm64.UnzipOdd(rb01, rb23);
        g = AdvSimd.Arm64.UnzipEven(ga01, ga23);
    }

    // Y of 16 pixels: the products summed in 32 bits, rounded down 16 bits (vrshrn_n_u32)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LumaArm64(
        Vector128<ushort> rl,
        Vector128<ushort> gl,
        Vector128<ushort> bl,
        Vector128<ushort> rh,
        Vector128<ushort> gh,
        Vector128<ushort> bh)
    {
        var yll = AdvSimd.MultiplyWideningLower(rl.GetLower(), Vector64.Create(FixRY));
        yll = AdvSimd.MultiplyWideningLowerAndAdd(yll, gl.GetLower(), Vector64.Create(FixGY));
        yll = AdvSimd.MultiplyWideningLowerAndAdd(yll, bl.GetLower(), Vector64.Create(FixBY));
        var ylh = AdvSimd.MultiplyWideningUpper(rl, Vector128.Create(FixRY));
        ylh = AdvSimd.MultiplyWideningUpperAndAdd(ylh, gl, Vector128.Create(FixGY));
        ylh = AdvSimd.MultiplyWideningUpperAndAdd(ylh, bl, Vector128.Create(FixBY));
        var yhl = AdvSimd.MultiplyWideningLower(rh.GetLower(), Vector64.Create(FixRY));
        yhl = AdvSimd.MultiplyWideningLowerAndAdd(yhl, gh.GetLower(), Vector64.Create(FixGY));
        yhl = AdvSimd.MultiplyWideningLowerAndAdd(yhl, bh.GetLower(), Vector64.Create(FixBY));
        var yhh = AdvSimd.MultiplyWideningUpper(rh, Vector128.Create(FixRY));
        yhh = AdvSimd.MultiplyWideningUpperAndAdd(yhh, gh, Vector128.Create(FixGY));
        yhh = AdvSimd.MultiplyWideningUpperAndAdd(yhh, bh, Vector128.Create(FixBY));
        var low = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(yll, 16), ylh, 16);
        var high = AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(AdvSimd.ShiftRightLogicalRoundedNarrowingLower(yhl, 16), yhh, 16);
        return AdvSimd.ExtractNarrowingUpper(AdvSimd.ExtractNarrowingLower(low), high);
    }

    // Cb or Cr of 16 pixels from their sums: down 16 bits (vshrn_n_u32), then to bytes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NarrowArm64(
        Vector128<uint> ll,
        Vector128<uint> lh,
        Vector128<uint> hl,
        Vector128<uint> hh)
    {
        var low = AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(ll, 16), lh, 16);
        var high = AdvSimd.ShiftRightLogicalNarrowingUpper(AdvSimd.ShiftRightLogicalNarrowingLower(hl, 16), hh, 16);
        return AdvSimd.ExtractNarrowingUpper(AdvSimd.ExtractNarrowingLower(low), high);
    }

    // 4 RGBA pixels' channels in 32-bit lanes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Unpack(
        ref byte pixels,
        out Vector128<int> r,
        out Vector128<int> g,
        out Vector128<int> b)
    {
        var v = Vector128.LoadUnsafe(ref pixels).AsInt32();
        var mask = Vector128.Create(0xFF);
        r = v & mask;
        g = Vector128.ShiftRightLogical(v, 8) & mask;
        b = Vector128.ShiftRightLogical(v, 16) & mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Luma(
        Vector128<int> r,
        Vector128<int> g,
        Vector128<int> b) =>
        Vector128.ShiftRightArithmetic((r * FixRY) + (g * FixGY) + (b * FixBY) + Vector128.Create(RoundY), 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Cb(
        Vector128<int> r,
        Vector128<int> g,
        Vector128<int> b) =>
        Vector128.ShiftRightArithmetic(Vector128.Create(OffsetCbCr) - (r * FixRCb) - (g * FixGCb) + (b * FixHalf), 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Cr(
        Vector128<int> r,
        Vector128<int> g,
        Vector128<int> b) =>
        Vector128.ShiftRightArithmetic(Vector128.Create(OffsetCbCr) + (r * FixHalf) - (g * FixGCr) - (b * FixBCr), 16);

    // 16 samples in 32-bit lanes as bytes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Pack(
        Vector128<int> a,
        Vector128<int> b,
        Vector128<int> c,
        Vector128<int> d) =>
        Vector128.Narrow(Vector128.Narrow(a.AsUInt32(), b.AsUInt32()), Vector128.Narrow(c.AsUInt32(), d.AsUInt32()));

    // h2v1_downsample (jsimd_h2v1_downsample_neon): each pair of samples averaged, the rounding
    // alternating 0, 1; the target a multiple of 8 samples
    private static void DownsampleH2V1(
        ReadOnlySpan<byte> source,
        Span<byte> target)
    {
        ref byte s = ref MemoryMarshal.GetReference(source);
        ref byte t = ref MemoryMarshal.GetReference(target);
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var bias = Vector128.Create(0x00010000).AsUInt16();
            for (; x + 8 <= target.Length; x += 8)
            {
                var sum = PairSums(Vector128.LoadUnsafe(ref s, (nuint)(2 * x)));
                var result = Vector128.ShiftRightLogical(sum + bias, 1);
                Vector128.Narrow(result, result).GetLower().StoreUnsafe(ref t, (nuint)x);
            }
        }

        for (; x < target.Length; x++)
        {
            target[x] = (byte)((source[2 * x] + source[(2 * x) + 1] + (x & 1)) >> 1);
        }
    }

    // h2v2_downsample (jsimd_h2v2_downsample_neon): each 2 x 2 averaged, the rounding alternating 1, 2
    private static void DownsampleH2V2(
        ReadOnlySpan<byte> source0,
        ReadOnlySpan<byte> source1,
        Span<byte> target)
    {
        ref byte s0 = ref MemoryMarshal.GetReference(source0);
        ref byte s1 = ref MemoryMarshal.GetReference(source1);
        ref byte t = ref MemoryMarshal.GetReference(target);
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var bias = Vector128.Create(0x00020001).AsUInt16();
            for (; x + 16 <= target.Length; x += 16)
            {
                var low = PairSums(Vector128.LoadUnsafe(ref s0, (nuint)(2 * x))) + PairSums(Vector128.LoadUnsafe(ref s1, (nuint)(2 * x)));
                var high = PairSums(Vector128.LoadUnsafe(ref s0, (nuint)((2 * x) + 16))) + PairSums(Vector128.LoadUnsafe(ref s1, (nuint)((2 * x) + 16)));
                Vector128.Narrow(Vector128.ShiftRightLogical(low + bias, 2), Vector128.ShiftRightLogical(high + bias, 2)).StoreUnsafe(ref t, (nuint)x);
            }

            for (; x + 8 <= target.Length; x += 8)
            {
                var sum = PairSums(Vector128.LoadUnsafe(ref s0, (nuint)(2 * x))) + PairSums(Vector128.LoadUnsafe(ref s1, (nuint)(2 * x)));
                var result = Vector128.ShiftRightLogical(sum + bias, 2);
                Vector128.Narrow(result, result).GetLower().StoreUnsafe(ref t, (nuint)x);
            }
        }

        for (; x < target.Length; x++)
        {
            int sum = source0[2 * x] + source0[(2 * x) + 1] + source1[2 * x] + source1[(2 * x) + 1];
            target[x] = (byte)((sum + 1 + (x & 1)) >> 2);
        }
    }

    // The sums of 16 bytes' adjacent pairs as 8 16-bit lanes (vpaddlq_u8 on Arm64)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> PairSums(
        Vector128<byte> bytes)
    {
        if (AdvSimd.IsSupported)
        {
            return AdvSimd.AddPairwiseWidening(bytes);
        }

        var words = bytes.AsUInt16();
        return (words & Vector128.Create((ushort)0xFF)) + Vector128.ShiftRightLogical(words, 8);
    }
}
