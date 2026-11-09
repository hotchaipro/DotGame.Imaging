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
    // jdsample.c's upsampling, a row of the image at a time, with jdmainct.c's context rows (the
    // rows above the first and below the last are copies of them). The fancy upsamplers' inner
    // columns go eight at a time in 16-bit lanes (the sums stay below 4,096), the C's formulas in
    // each lane; the edge columns are the C's special cases.
    private static class Upsampling
    {
        private static bool Vectors => AdvSimd.Arm64.IsSupported || Sse2.IsSupported;

        // Row y of the component at the image's full size: its plane's row, or upsampled into buffer
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ReadOnlySpan<byte> Row(
            Component c,
            int y,
            int width,
            int maxH,
            int maxV,
            byte[] buffer)
        {
            if ((c.H == maxH) && (c.V == maxV))
            {
                return c.Plane.AsSpan(y * c.Stride, width);
            }

            int lastRow = c.DownsampledHeight - 1;
            ReadOnlySpan<byte> Plane(int r) => c.Plane.AsSpan(Math.Clamp(r, 0, lastRow) * c.Stride, c.Stride);

            if ((c.H * 2 == maxH) && (c.V == maxV))
            {
                if (c.DownsampledWidth > 2)
                {
                    H2V1Fancy(Plane(y), buffer, c.DownsampledWidth);
                }
                else
                {
                    Replicate(Plane(y).Slice(0, c.DownsampledWidth), buffer, 2);
                }

                return buffer.AsSpan(0, width);
            }

            if ((c.H == maxH) && (c.V * 2 == maxV))
            {
                int r = y >> 1;
                H1V2Fancy(Plane(r), Plane(((y & 1) == 0) ? r - 1 : r + 1), buffer, width, ((y & 1) == 0) ? 1 : 2);
                return buffer.AsSpan(0, width);
            }

            if ((c.H * 2 == maxH) && (c.V * 2 == maxV))
            {
                int r = y >> 1;
                if (c.DownsampledWidth > 2)
                {
                    H2V2Fancy(Plane(r), Plane(((y & 1) == 0) ? r - 1 : r + 1), buffer, c.DownsampledWidth);
                }
                else
                {
                    Replicate(Plane(r).Slice(0, c.DownsampledWidth), buffer, 2);
                }

                return buffer.AsSpan(0, width);
            }

            if (((maxH % c.H) == 0) && ((maxV % c.V) == 0))
            {
                Replicate(Plane(y / (maxV / c.V)).Slice(0, c.DownsampledWidth), buffer, maxH / c.H);
                return buffer.AsSpan(0, width);
            }

            throw new NotSupportedException("A JPEG component's sampling factors aren't a fraction libjpeg upsamples (the largest must be a multiple of each).");
        }

        private static void Replicate(
            ReadOnlySpan<byte> input,
            Span<byte> output,
            int factor)
        {
            for (int x = 0; x < input.Length; x++)
            {
                output.Slice(x * factor, factor).Fill(input[x]);
            }
        }

        // Sixteen bytes from index on as two sets of eight shorts
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (Vector128<short> Low, Vector128<short> High) Load16(
            ReadOnlySpan<byte> s,
            int index)
        {
            var (low, high) = Vector128.Widen(Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(s), (nuint)index));
            return (low.AsInt16(), high.AsInt16());
        }

        // Two sets of sixteen values 0 to 255 (each in two vectors of shorts), interleaved as 32 bytes
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StoreInterleaved(
            Vector128<short> evenLow,
            Vector128<short> evenHigh,
            Vector128<short> oddLow,
            Vector128<short> oddHigh,
            Span<byte> output,
            int index)
        {
            var e = Vector128.Narrow(evenLow, evenHigh).AsByte();
            var o = Vector128.Narrow(oddLow, oddHigh).AsByte();
            ref byte target = ref MemoryMarshal.GetReference(output);
            if (AdvSimd.Arm64.IsSupported)
            {
                AdvSimd.Arm64.ZipLow(e, o).StoreUnsafe(ref target, (nuint)index);
                AdvSimd.Arm64.ZipHigh(e, o).StoreUnsafe(ref target, (nuint)(index + 16));
            }
            else
            {
                Sse2.UnpackLow(e, o).StoreUnsafe(ref target, (nuint)index);
                Sse2.UnpackHigh(e, o).StoreUnsafe(ref target, (nuint)(index + 16));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void H2V1Fancy(
            ReadOnlySpan<byte> input,
            Span<byte> output,
            int count)
        {
            int value = input[0];
            output[0] = (byte)value;
            output[1] = (byte)(((value * 3) + input[1] + 2) >> 2);
            int i = 1;
            if (Vectors)
            {
                var one = Vector128.Create((short)1);
                var two = Vector128.Create((short)2);
                for (; i + 17 <= count; i += 16)
                {
                    var (tl, th) = Load16(input, i);
                    var (ll, lh) = Load16(input, i - 1);
                    var (nl, nh) = Load16(input, i + 1);
                    StoreInterleaved(((tl * 3) + ll + one) >> 2, ((th * 3) + lh + one) >> 2, ((tl * 3) + nl + two) >> 2, ((th * 3) + nh + two) >> 2, output, 2 * i);
                }
            }

            for (; i < count - 1; i++)
            {
                value = input[i] * 3;
                output[2 * i] = (byte)((value + input[i - 1] + 1) >> 2);
                output[(2 * i) + 1] = (byte)((value + input[i + 1] + 2) >> 2);
            }

            value = input[count - 1];
            output[2 * (count - 1)] = (byte)(((value * 3) + input[count - 2] + 1) >> 2);
            output[(2 * (count - 1)) + 1] = (byte)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void H1V2Fancy(
            ReadOnlySpan<byte> near,
            ReadOnlySpan<byte> far,
            Span<byte> output,
            int count,
            int bias)
        {
            int x = 0;
            if (Vectors)
            {
                var b = Vector128.Create((short)bias);
                ref byte target = ref MemoryMarshal.GetReference(output);
                for (; x + 16 <= count; x += 16)
                {
                    var (nl, nh) = Load16(near, x);
                    var (fl, fh) = Load16(far, x);
                    Vector128.Narrow(((nl * 3) + fl + b) >> 2, ((nh * 3) + fh + b) >> 2).AsByte().StoreUnsafe(ref target, (nuint)x);
                }
            }

            for (; x < count; x++)
            {
                output[x] = (byte)(((near[x] * 3) + far[x] + bias) >> 2);
            }
        }

        // A column's sum: 3 * the nearer row's sample + the further row's
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Sum(
            ReadOnlySpan<byte> near,
            ReadOnlySpan<byte> far,
            int i) => (near[i] * 3) + far[i];

        // Sixteen columns' sums from index on
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (Vector128<short> Low, Vector128<short> High) Sums(
            ReadOnlySpan<byte> near,
            ReadOnlySpan<byte> far,
            int index)
        {
            var (nl, nh) = Load16(near, index);
            var (fl, fh) = Load16(far, index);
            return ((nl * 3) + fl, (nh * 3) + fh);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void H2V2Fancy(
            ReadOnlySpan<byte> near,
            ReadOnlySpan<byte> far,
            Span<byte> output,
            int count)
        {
            int first = Sum(near, far, 0);
            output[0] = (byte)(((first * 4) + 8) >> 4);
            output[1] = (byte)(((first * 3) + Sum(near, far, 1) + 7) >> 4);
            int i = 1;
            if (Vectors)
            {
                var eight = Vector128.Create((short)8);
                var seven = Vector128.Create((short)7);
                for (; i + 17 <= count; i += 16)
                {
                    var (tl, th) = Sums(near, far, i);
                    var (ll, lh) = Sums(near, far, i - 1);
                    var (nl, nh) = Sums(near, far, i + 1);
                    StoreInterleaved(((tl * 3) + ll + eight) >> 4, ((th * 3) + lh + eight) >> 4, ((tl * 3) + nl + seven) >> 4, ((th * 3) + nh + seven) >> 4, output, 2 * i);
                }
            }

            for (; i < count - 1; i++)
            {
                int three = Sum(near, far, i) * 3;
                output[2 * i] = (byte)((three + Sum(near, far, i - 1) + 8) >> 4);
                output[(2 * i) + 1] = (byte)((three + Sum(near, far, i + 1) + 7) >> 4);
            }

            int last = Sum(near, far, count - 1);
            output[2 * (count - 1)] = (byte)(((last * 3) + Sum(near, far, count - 2) + 8) >> 4);
            output[(2 * (count - 1)) + 1] = (byte)(((last * 4) + 7) >> 4);
        }
    }
}
