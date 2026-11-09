// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/alpha_dec.c and src/dsp/filters.c), modified by Hot Chai Games LLC:
// written in C#, the alpha decoded whole. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.WebP;

/// <summary>
/// A lossy WebP image's alpha (its ALPH chunk): stored raw or as a lossless image's green channel,
/// unfiltered (none, horizontal, vertical or gradient).
/// </summary>
internal static class AlphaDecoder
{
    private const int NoCompression = 0;
    private const int LosslessCompression = 1;
    private const int NoFilter = 0;
    private const int HorizontalFilter = 1;
    private const int VerticalFilter = 2;

    /// <summary>
    /// Decodes the ALPH chunk's payload into the alpha byte of each of <paramref name="rgba"/>'s
    /// width x height pixels.
    /// </summary>
    /// <exception cref="InvalidDataException">The alpha is damaged.</exception>
    public static void Decode(
        ReadOnlySpan<byte> alph,
        int width,
        int height,
        Span<byte> rgba)
    {
        // The header: the compression, the filter, the preprocessing (a hint to dither, which libwebp
        // doesn't by default), and reserved bits
        if (alph.Length <= 1)
        {
            throw Damaged();
        }

        int method = alph[0] & 0x03;
        int filter = (alph[0] >> 2) & 0x03;
        int preprocessing = (alph[0] >> 4) & 0x03;
        int reserved = (alph[0] >> 6) & 0x03;
        if ((method > LosslessCompression) || (preprocessing > 1) || (reserved != 0))
        {
            throw Damaged();
        }

        var stream = alph.Slice(1);
        int size = width * height;
        byte[] plane = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            ref byte output = ref MemoryMarshal.GetArrayDataReference(plane);
            if (method == NoCompression)
            {
                if (stream.Length < size)
                {
                    throw Damaged();
                }

                // The rows unfiltered from the chunk's bytes, each from the one above
                ref byte deltas = ref MemoryMarshal.GetReference(stream);
                for (int y = 0; y < height; y++)
                {
                    Unfilter(filter, y > 0, ref Unsafe.Add(ref output, Math.Max(y - 1, 0) * width), ref Unsafe.Add(ref deltas, y * width), ref Unsafe.Add(ref output, y * width), width);
                }
            }
            else
            {
                Vp8LDecoder.DecodeAlpha(stream, width, height, filter, plane);
            }

            // Each pixel's alpha, its color kept: 16 at a time, the bytes widened to the pixels' top
            var pixels = MemoryMarshal.Cast<byte, uint>(rgba.Slice(0, size * 4));
            ref uint target = ref MemoryMarshal.GetReference(pixels);
            int i = 0;
            for (; i + 16 <= size; i += 16)
            {
                var alpha = Vector128.LoadUnsafe(ref output, (nuint)i);
                var low = Vector128.WidenLower(alpha);
                var high = Vector128.WidenUpper(alpha);
                Insert(ref target, i, Vector128.WidenLower(low));
                Insert(ref target, i + 4, Vector128.WidenUpper(low));
                Insert(ref target, i + 8, Vector128.WidenLower(high));
                Insert(ref target, i + 12, Vector128.WidenUpper(high));
            }

            for (; i < size; i++)
            {
                ref uint pixel = ref Unsafe.Add(ref target, i);
                pixel = (pixel & 0x00ffffff) | ((uint)Unsafe.Add(ref output, i) << 24);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plane);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Insert(
        ref uint pixels,
        int index,
        Vector128<uint> alpha)
    {
        var pixel = Vector128.LoadUnsafe(ref pixels, (nuint)index);
        ((pixel & Vector128.Create(0x00ffffffu)) | (alpha << 24)).StoreUnsafe(ref pixels, (nuint)index);
    }

    /// <summary>
    /// A row unfiltered (libwebp's WebPUnfilters): <paramref name="hasPrevious"/> says whether
    /// <paramref name="previous"/> is the row above (the first row has none). The output may be the
    /// input.
    /// </summary>
    public static void Unfilter(
        int filter,
        bool hasPrevious,
        ref byte previous,
        ref byte input,
        ref byte output,
        int width)
    {
        if (filter == NoFilter)
        {
            if (!Unsafe.AreSame(ref input, ref output))
            {
                Unsafe.CopyBlockUnaligned(ref output, ref input, (uint)width);
            }

            return;
        }

        if ((filter == HorizontalFilter) || !hasPrevious)
        {
            // Each byte the sum of those to its left, and the first pixel above (or 0): 16 at a time
            // as a prefix sum in four steps (the bytes shifted up by 1, 2, 4 and 8), the last sum
            // carried to the next 16
            byte pred = hasPrevious ? previous : (byte)0;
            int i = 0;
            if (AdvSimd.IsSupported || Sse2.IsSupported)
            {
                var carry = Vector128.Create(pred);
                for (; i + 16 <= width; i += 16)
                {
                    var sums = Vector128.LoadUnsafe(ref input, (nuint)i);
                    sums += ShiftBytesUp(sums, 1);
                    sums += ShiftBytesUp(sums, 2);
                    sums += ShiftBytesUp(sums, 4);
                    sums += ShiftBytesUp(sums, 8);
                    sums += carry;
                    sums.StoreUnsafe(ref output, (nuint)i);
                    carry = Vector128.Create(sums.GetElement(15));
                }

                pred = carry.GetElement(0);
            }

            for (; i < width; i++)
            {
                pred = (byte)(pred + Unsafe.Add(ref input, i));
                Unsafe.Add(ref output, i) = pred;
            }

            return;
        }

        if (filter == VerticalFilter)
        {
            int i = 0;
            for (; i + 16 <= width; i += 16)
            {
                (Vector128.LoadUnsafe(ref previous, (nuint)i) + Vector128.LoadUnsafe(ref input, (nuint)i)).StoreUnsafe(ref output, (nuint)i);
            }

            for (; i < width; i++)
            {
                Unsafe.Add(ref output, i) = (byte)(Unsafe.Add(ref previous, i) + Unsafe.Add(ref input, i));
            }

            return;
        }

        // Gradient: left + top - top-left, clipped to a byte
        int top = previous;
        int topLeft = top;
        int left = top;
        for (int i = 0; i < width; i++)
        {
            top = Unsafe.Add(ref previous, i);
            int g = left + top - topLeft;
            int predicted = ((g & ~0xff) == 0) ? g : (g < 0) ? 0 : 255;
            left = (byte)(Unsafe.Add(ref input, i) + predicted);
            topLeft = top;
            Unsafe.Add(ref output, i) = (byte)left;
        }
    }

    // The bytes moved up by 1, 2, 4 or 8 places, zeros below
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ShiftBytesUp(
        Vector128<byte> bytes,
        [ConstantExpected(Min = 1, Max = 8)] byte count)
    {
        if (AdvSimd.IsSupported)
        {
            return count switch
            {
                1 => AdvSimd.ExtractVector128(Vector128<byte>.Zero, bytes, 15),
                2 => AdvSimd.ExtractVector128(Vector128<byte>.Zero, bytes, 14),
                4 => AdvSimd.ExtractVector128(Vector128<byte>.Zero, bytes, 12),
                _ => AdvSimd.ExtractVector128(Vector128<byte>.Zero, bytes, 8),
            };
        }

        return count switch
        {
            1 => Sse2.ShiftLeftLogical128BitLane(bytes, 1),
            2 => Sse2.ShiftLeftLogical128BitLane(bytes, 2),
            4 => Sse2.ShiftLeftLogical128BitLane(bytes, 4),
            _ => Sse2.ShiftLeftLogical128BitLane(bytes, 8),
        };
    }

    private static InvalidDataException Damaged()
    {
        return new InvalidDataException("The WebP image's alpha is damaged.");
    }
}
