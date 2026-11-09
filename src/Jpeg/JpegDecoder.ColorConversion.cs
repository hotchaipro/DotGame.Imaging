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
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.Jpeg;

internal static partial class JpegDecoder
{
    // jdcolor.c's tables and jdcolext.c's conversions to RGBA. The tables are the formulas they
    // were filled from, worked out sixteen pixels at a time: the products in 32-bit lanes, then
    // the sums in 16-bit ones, narrowed with unsigned saturation (exactly the range limit), and
    // zipped into RGBA.
    private static class ColorConversion
    {
        private const int ScaleBits = 16;
        private const int OneHalf = 1 << (ScaleBits - 1);

        // FIX(x) = (x * 65536 + 0.5) truncated, as jdcolor.c computes it
        private const int Fix1402 = 91881;
        private const int Fix1772 = 116130;
        private const int Fix071414 = 46802;
        private const int Fix034414 = 22554;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void YccRgb(
            ReadOnlySpan<byte> y,
            ReadOnlySpan<byte> cb,
            ReadOnlySpan<byte> cr,
            Span<byte> rgba)
        {
            int n = y.Length;
            int i = 0;
            if (AdvSimd.Arm64.IsSupported)
            {
                i = YccRgbNeon(y, cb, cr, rgba);
            }
            else if (Vector128.IsHardwareAccelerated && Sse2.IsSupported)
            {
                ref byte ys = ref MemoryMarshal.GetReference(y);
                ref byte cbs = ref MemoryMarshal.GetReference(cb);
                ref byte crs = ref MemoryMarshal.GetReference(cr);
                ref byte output = ref MemoryMarshal.GetReference(rgba);
                for (; i + 16 <= n; i += 16)
                {
                    var yv = Vector128.LoadUnsafe(ref ys, (nuint)i);
                    var (yLow, yHigh) = Vector128.Widen(yv);
                    var (bLow, bHigh) = Centered(Vector128.LoadUnsafe(ref cbs, (nuint)i));
                    var (rLow, rHigh) = Centered(Vector128.LoadUnsafe(ref crs, (nuint)i));

                    var red = Saturate(yLow.AsInt16() + Narrow(Scaled(rLow.Lower, Fix1402), Scaled(rLow.Upper, Fix1402)), yHigh.AsInt16() + Narrow(Scaled(rHigh.Lower, Fix1402), Scaled(rHigh.Upper, Fix1402)));
                    var blue = Saturate(yLow.AsInt16() + Narrow(Scaled(bLow.Lower, Fix1772), Scaled(bLow.Upper, Fix1772)), yHigh.AsInt16() + Narrow(Scaled(bHigh.Lower, Fix1772), Scaled(bHigh.Upper, Fix1772)));
                    var green = Saturate(
                        yLow.AsInt16() + Narrow(Green(bLow.Lower, rLow.Lower), Green(bLow.Upper, rLow.Upper)),
                        yHigh.AsInt16() + Narrow(Green(bHigh.Lower, rHigh.Lower), Green(bHigh.Upper, rHigh.Upper)));
                    StoreRgba(red, green, blue, ref Unsafe.Add(ref output, 4 * i));
                }
            }

            for (; i < n; i++)
            {
                int luma = y[i];
                int b = cb[i] - 128;
                int r = cr[i] - 128;
                int crR = ((Fix1402 * r) + OneHalf) >> ScaleBits;
                int cbB = ((Fix1772 * b) + OneHalf) >> ScaleBits;
                int g = ((-Fix034414 * b) + OneHalf + (-Fix071414 * r)) >> ScaleBits;
                rgba[4 * i] = Clamp(luma + crR);
                rgba[(4 * i) + 1] = Clamp(luma + g);
                rgba[(4 * i) + 2] = Clamp(luma + cbB);
                rgba[(4 * i) + 3] = 255;
            }
        }

        // Arm64, sixteen pixels at a time in 16-bit lanes, each formula's constant split to fit 16
        // bits, exactly: 91881 x = 65536 x + 26345 x, so R = y + x + ((26345 x + 32768) >> 16);
        // 116130 x = 131072 x - 14942 x, so B = y + 2x + ((-14942 x + 32768) >> 16); and
        // -46802 r = -65536 r + 18734 r, so G = y - r + ((-22554 b + 18734 r + 32768) >> 16). Each
        // (v + 32768) >> 16 is a rounding narrow of a widening product. Returns the pixels done.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int YccRgbNeon(
            ReadOnlySpan<byte> y,
            ReadOnlySpan<byte> cb,
            ReadOnlySpan<byte> cr,
            Span<byte> rgba)
        {
            ref byte ys = ref MemoryMarshal.GetReference(y);
            ref byte cbs = ref MemoryMarshal.GetReference(cb);
            ref byte crs = ref MemoryMarshal.GetReference(cr);
            ref byte output = ref MemoryMarshal.GetReference(rgba);
            var center = Vector64.Create((byte)128);
            var constants = Vector64.Create((short)26345, -14942, -22554, 18734);
            int n = y.Length;
            int i = 0;
            for (; i + 16 <= n; i += 16)
            {
                var yv = Vector128.LoadUnsafe(ref ys, (nuint)i);
                var bv = Vector128.LoadUnsafe(ref cbs, (nuint)i);
                var rv = Vector128.LoadUnsafe(ref crs, (nuint)i);

                // b and r less 128 as shorts (the unsigned differences' bits are the signed ones)
                var bLow = AdvSimd.SubtractWideningLower(bv.GetLower(), center).AsInt16();
                var bHigh = AdvSimd.SubtractWideningUpper(bv, Vector128.Create((byte)128)).AsInt16();
                var rLow = AdvSimd.SubtractWideningLower(rv.GetLower(), center).AsInt16();
                var rHigh = AdvSimd.SubtractWideningUpper(rv, Vector128.Create((byte)128)).AsInt16();
                var yLow = AdvSimd.ZeroExtendWideningLower(yv.GetLower()).AsInt16();
                var yHigh = AdvSimd.ZeroExtendWideningUpper(yv).AsInt16();

                var redLow = yLow + rLow + AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(
                    AdvSimd.ShiftRightLogicalRoundedNarrowingLower(AdvSimd.MultiplyBySelectedScalarWideningLower(rLow.GetLower(), constants, 0), 16),
                    AdvSimd.MultiplyBySelectedScalarWideningUpper(rLow, constants, 0), 16);
                var redHigh = yHigh + rHigh + AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(
                    AdvSimd.ShiftRightLogicalRoundedNarrowingLower(AdvSimd.MultiplyBySelectedScalarWideningLower(rHigh.GetLower(), constants, 0), 16),
                    AdvSimd.MultiplyBySelectedScalarWideningUpper(rHigh, constants, 0), 16);
                var blueLow = yLow + (bLow << 1) + AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(
                    AdvSimd.ShiftRightLogicalRoundedNarrowingLower(AdvSimd.MultiplyBySelectedScalarWideningLower(bLow.GetLower(), constants, 1), 16),
                    AdvSimd.MultiplyBySelectedScalarWideningUpper(bLow, constants, 1), 16);
                var blueHigh = yHigh + (bHigh << 1) + AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(
                    AdvSimd.ShiftRightLogicalRoundedNarrowingLower(AdvSimd.MultiplyBySelectedScalarWideningLower(bHigh.GetLower(), constants, 1), 16),
                    AdvSimd.MultiplyBySelectedScalarWideningUpper(bHigh, constants, 1), 16);
                var greenLow = yLow - rLow + AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(
                    AdvSimd.ShiftRightLogicalRoundedNarrowingLower(AdvSimd.MultiplyBySelectedScalarWideningLowerAndAdd(AdvSimd.MultiplyBySelectedScalarWideningLower(bLow.GetLower(), constants, 2), rLow.GetLower(), constants, 3), 16),
                    AdvSimd.MultiplyBySelectedScalarWideningUpperAndAdd(AdvSimd.MultiplyBySelectedScalarWideningUpper(bLow, constants, 2), rLow, constants, 3), 16);
                var greenHigh = yHigh - rHigh + AdvSimd.ShiftRightLogicalRoundedNarrowingUpper(
                    AdvSimd.ShiftRightLogicalRoundedNarrowingLower(AdvSimd.MultiplyBySelectedScalarWideningLowerAndAdd(AdvSimd.MultiplyBySelectedScalarWideningLower(bHigh.GetLower(), constants, 2), rHigh.GetLower(), constants, 3), 16),
                    AdvSimd.MultiplyBySelectedScalarWideningUpperAndAdd(AdvSimd.MultiplyBySelectedScalarWideningUpper(bHigh, constants, 2), rHigh, constants, 3), 16);

                StoreRgba(Saturate(redLow, redHigh), Saturate(greenLow, greenHigh), Saturate(blueLow, blueHigh), ref Unsafe.Add(ref output, 4 * i));
            }

            return i;
        }

        // Sixteen chroma bytes less 128, as two pairs of four ints
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ((Vector128<int> Lower, Vector128<int> Upper) Low, (Vector128<int> Lower, Vector128<int> Upper) High) Centered(
            Vector128<byte> v)
        {
            var (low, high) = Vector128.Widen(v);
            var lowCentered = low.AsInt16() - Vector128.Create((short)128);
            var highCentered = high.AsInt16() - Vector128.Create((short)128);
            return (Vector128.Widen(lowCentered), Vector128.Widen(highCentered));
        }

        // Cr_r_tab or Cb_b_tab's value: (factor * x + ONE_HALF) >> 16
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> Scaled(
            Vector128<int> x,
            int factor) => ((x * factor) + Vector128.Create(OneHalf)) >> ScaleBits;

        // (Cb_g_tab[cb] + Cr_g_tab[cr]) >> 16
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> Green(
            Vector128<int> b,
            Vector128<int> r) =>
            ((b * -Fix034414) + Vector128.Create(OneHalf) + (r * -Fix071414)) >> ScaleBits;

        // Small ints (within -256..255) as shorts
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<short> Narrow(
            Vector128<int> low,
            Vector128<int> high) => Vector128.Narrow(low, high);

        // Sixteen shorts to bytes, clamped to 0..255 (jdmaster.c's range limit table)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Saturate(
            Vector128<short> low,
            Vector128<short> high)
        {
            if (AdvSimd.Arm64.IsSupported)
            {
                return AdvSimd.ExtractNarrowingSaturateUnsignedUpper(AdvSimd.ExtractNarrowingSaturateUnsignedLower(low), high);
            }

            return Sse2.PackUnsignedSaturate(low, high);
        }

        // Sixteen pixels' red, green and blue bytes as RGBA, alpha 255
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StoreRgba(
            Vector128<byte> red,
            Vector128<byte> green,
            Vector128<byte> blue,
            ref byte output)
        {
            var alpha = Vector128<byte>.AllBitsSet;
            Vector128<ushort> rgLow, rgHigh, baLow, baHigh;
            Vector128<uint> p0, p1, p2, p3;
            if (AdvSimd.Arm64.IsSupported)
            {
                rgLow = AdvSimd.Arm64.ZipLow(red, green).AsUInt16();
                rgHigh = AdvSimd.Arm64.ZipHigh(red, green).AsUInt16();
                baLow = AdvSimd.Arm64.ZipLow(blue, alpha).AsUInt16();
                baHigh = AdvSimd.Arm64.ZipHigh(blue, alpha).AsUInt16();
                p0 = AdvSimd.Arm64.ZipLow(rgLow, baLow).AsUInt32();
                p1 = AdvSimd.Arm64.ZipHigh(rgLow, baLow).AsUInt32();
                p2 = AdvSimd.Arm64.ZipLow(rgHigh, baHigh).AsUInt32();
                p3 = AdvSimd.Arm64.ZipHigh(rgHigh, baHigh).AsUInt32();
            }
            else
            {
                rgLow = Sse2.UnpackLow(red, green).AsUInt16();
                rgHigh = Sse2.UnpackHigh(red, green).AsUInt16();
                baLow = Sse2.UnpackLow(blue, alpha).AsUInt16();
                baHigh = Sse2.UnpackHigh(blue, alpha).AsUInt16();
                p0 = Sse2.UnpackLow(rgLow, baLow).AsUInt32();
                p1 = Sse2.UnpackHigh(rgLow, baLow).AsUInt32();
                p2 = Sse2.UnpackLow(rgHigh, baHigh).AsUInt32();
                p3 = Sse2.UnpackHigh(rgHigh, baHigh).AsUInt32();
            }

            p0.AsByte().StoreUnsafe(ref output);
            p1.AsByte().StoreUnsafe(ref output, 16);
            p2.AsByte().StoreUnsafe(ref output, 32);
            p3.AsByte().StoreUnsafe(ref output, 48);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Rgb(
            ReadOnlySpan<byte> r,
            ReadOnlySpan<byte> g,
            ReadOnlySpan<byte> b,
            Span<byte> rgba)
        {
            int i = 0;
            if (AdvSimd.Arm64.IsSupported || Sse2.IsSupported)
            {
                ref byte output = ref MemoryMarshal.GetReference(rgba);
                for (; i + 16 <= r.Length; i += 16)
                {
                    StoreRgba(Vector128.Create(r.Slice(i, 16)), Vector128.Create(g.Slice(i, 16)), Vector128.Create(b.Slice(i, 16)), ref Unsafe.Add(ref output, 4 * i));
                }
            }

            var pixels = MemoryMarshal.Cast<byte, uint>(rgba);
            for (; i < r.Length; i++)
            {
                pixels[i] = r[i] | ((uint)g[i] << 8) | ((uint)b[i] << 16) | 0xFF000000;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Gray(
            ReadOnlySpan<byte> gray,
            Span<byte> rgba)
        {
            int i = 0;
            if (AdvSimd.Arm64.IsSupported || Sse2.IsSupported)
            {
                ref byte output = ref MemoryMarshal.GetReference(rgba);
                for (; i + 16 <= gray.Length; i += 16)
                {
                    var v = Vector128.Create(gray.Slice(i, 16));
                    StoreRgba(v, v, v, ref Unsafe.Add(ref output, 4 * i));
                }
            }

            var pixels = MemoryMarshal.Cast<byte, uint>(rgba);
            for (; i < gray.Length; i++)
            {
                pixels[i] = (gray[i] * 0x010101u) | 0xFF000000;
            }
        }

        private static byte Clamp(
            int x) => (byte)Math.Clamp(x, 0, 255);
    }
}
