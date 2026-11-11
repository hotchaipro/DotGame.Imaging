// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dsp/lossless.c and lossless_common.h, the inverse transforms and
// the output's conversion), modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LDecoder
{
    private const uint ArgbBlack = 0xff000000;

    private sealed partial class Decoding
    {
        // libwebp's VP8LInverseTransform, on rows startRow to endRow: 'input' and 'output' are offsets
        // in the pixels (the same, after the first transform)
        private void InverseTransform(
            ref Transform transform,
            int startRow,
            int endRow,
            int input,
            int output)
        {
            var pixels = this.pixels;
            int w = transform.XSize;
            switch (transform.Type)
            {
                case SubtractGreenTransform:
                    AddGreenToBlueAndRed(pixels.AsSpan(input, (endRow - startRow) * w), pixels.AsSpan(output, (endRow - startRow) * w));
                    break;

                case PredictorTransform:
                    PredictorInverse(ref transform, startRow, endRow, pixels, input, output);
                    if (endRow != transform.YSize)
                    {
                        // The last row, above the next rows' first
                        Array.Copy(pixels, output + ((endRow - startRow - 1) * w), pixels, output - w, w);
                    }

                    break;

                case CrossColorTransform:
                    ColorSpaceInverse(ref transform, startRow, endRow, pixels, input, output);
                    break;

                case ColorIndexingTransform:
                    if ((input == output) && (transform.Bits > 0))
                    {
                        // Packed indices in place: moved to the end of the rows first, so they aren't
                        // overwritten before they're read
                        int outStride = (endRow - startRow) * w;
                        int inStride = (endRow - startRow) * SubSampleSize(transform.XSize, transform.Bits);
                        int source = output + outStride - inStride;
                        Array.Copy(pixels, output, pixels, source, inStride);
                        ColorIndexInverse(ref transform, startRow, endRow, pixels, source, output);
                    }
                    else
                    {
                        ColorIndexInverse(ref transform, startRow, endRow, pixels, input, output);
                    }

                    break;
            }
        }
    }

    // Green added back to red and blue; with 'toRgba', red and blue then swapped for the output
    private static void AddGreenToBlueAndRed(
        ReadOnlySpan<uint> source,
        Span<uint> target,
        bool toRgba = false)
    {
        ref uint s = ref MemoryMarshal.GetReference(source);
        ref uint d = ref MemoryMarshal.GetReference(target);
        int i = 0;
        var keep = Vector128.Create(0xff00ff00);
        var redBlueMask = Vector128.Create(0x00ff00ffu);
        var low = Vector128.Create(0xffu);
        for (; i + 4 <= source.Length; i += 4)
        {
            var argb = Vector128.LoadUnsafe(ref s, (nuint)i);
            var green = (argb >> 8) & low;
            var redBlue = ((argb & redBlueMask) + ((green << 16) | green)) & redBlueMask;
            var result = (argb & keep) | redBlue;
            if (toRgba)
            {
                result = (result & keep) | ((result >> 16) & low) | ((result & low) << 16);
            }

            result.StoreUnsafe(ref d, (nuint)i);
        }

        for (; i < source.Length; i++)
        {
            uint argb = Unsafe.Add(ref s, i);
            uint green = (argb >> 8) & 0xff;
            uint redBlue = (argb & 0x00ff00ff) + ((green << 16) | green);
            uint result = (argb & 0xff00ff00) | (redBlue & 0x00ff00ff);
            if (toRgba)
            {
                result = (result & 0xff00ff00) | ((result >> 16) & 0xff) | ((result & 0xff) << 16);
            }

            Unsafe.Add(ref d, i) = result;
        }
    }

    private static void PredictorInverse(
        ref Transform transform,
        int yStart,
        int yEnd,
        uint[] pixels,
        int input,
        int output)
    {
        int w = transform.XSize;
        if (yStart == 0)
        {
            // The first row: black, then the left pixel
            pixels[output] = AddPixels(pixels[input], ArgbBlack);
            uint left = pixels[output];
            for (int x = 1; x < w; x++)
            {
                left = AddPixels(pixels[input + x], left);
                pixels[output + x] = left;
            }

            input += w;
            output += w;
            yStart++;
        }

        int tileWidth = 1 << transform.Bits;
        int mask = tileWidth - 1;
        int tilesPerRow = SubSampleSize(w, transform.Bits);
        int modeBase = (yStart >> transform.Bits) * tilesPerRow;
        var modes = transform.Data;
        for (int y = yStart; y < yEnd; y++)
        {
            int mode = modeBase;

            // The first pixel: the one above
            pixels[output] = AddPixels(pixels[input], pixels[output - w]);
            int x = 1;
            while (x < w)
            {
                int predictor = (int)((modes[mode++] >> 8) & 0xf);
                int xEnd = (x & ~mask) + tileWidth;
                if (xEnd > w)
                {
                    xEnd = w;
                }

                PredictorAdd(predictor, pixels, input + x, output + x - w, xEnd - x, output + x);
                x = xEnd;
            }

            input += w;
            output += w;
            if (((y + 1) & mask) == 0)
            {
                modeBase += tilesPerRow;
            }
        }
    }

    // One predictor over a run of a row's pixels: each the residual plus its prediction from the
    // pixels decoded already (left, and the row above). Those that don't read the pixel to the left
    // take four pixels at a time: their bytes add and average independently, as vectors' do.
    internal static void PredictorAdd(
        int predictor,
        uint[] pixels,
        int input,
        int upper,
        int count,
        int output)
    {
        ref uint p = ref MemoryMarshal.GetArrayDataReference(pixels);
        ref uint inp = ref Unsafe.Add(ref p, input);
        ref uint up = ref Unsafe.Add(ref p, upper);
        ref uint outp = ref Unsafe.Add(ref p, output);
        int i = 0;
        switch (predictor)
        {
            case 0:
            case 14:
            case 15:
                for (; i + 4 <= count; i += 4)
                {
                    Store(ref outp, i, Load(ref inp, i) + Vector128.Create(ArgbBlack).AsByte());
                }

                for (; i < count; i++)
                {
                    Unsafe.Add(ref outp, i) = AddPixels(Unsafe.Add(ref inp, i), ArgbBlack);
                }

                return;

            case 2:
            case 3:
            case 4:
                {
                    // The pixel above, above right or above left
                    ref uint top = ref Unsafe.Add(ref up, (predictor == 2) ? 0 : (predictor == 3) ? 1 : -1);
                    for (; i + 4 <= count; i += 4)
                    {
                        Store(ref outp, i, Load(ref inp, i) + Load(ref top, i));
                    }

                    for (; i < count; i++)
                    {
                        Unsafe.Add(ref outp, i) = AddPixels(Unsafe.Add(ref inp, i), Unsafe.Add(ref top, i));
                    }

                    return;
                }

            case 8:
            case 9:
                {
                    // The average of above left and above, or of above and above right
                    ref uint first = ref Unsafe.Add(ref up, (predictor == 8) ? -1 : 0);
                    for (; i + 4 <= count; i += 4)
                    {
                        Store(ref outp, i, Load(ref inp, i) + Average2(Load(ref first, i), Load(ref first, i + 1)));
                    }

                    for (; i < count; i++)
                    {
                        Unsafe.Add(ref outp, i) = AddPixels(Unsafe.Add(ref inp, i), Average2(Unsafe.Add(ref first, i), Unsafe.Add(ref first, i + 1)));
                    }

                    return;
                }
        }

        // The rest read the pixel to the left, just made: on Arm64 four at a time with vectors (the
        // last few, and other machines, a pixel at a time)
        uint left = Unsafe.Add(ref outp, -1);
        if (AdvSimd.IsSupported && (predictor != 1))
        {
            i = PredictorAddNeon(predictor, ref inp, ref up, ref outp, count, left);
            if (i > 0)
            {
                left = Unsafe.Add(ref outp, i - 1);
            }
        }

        switch (predictor)
        {
            case 1:
                if (AdvSimd.IsSupported || Sse2.IsSupported)
                {
                    // Each pixel the sum of the residuals to its left: the four summed in two steps,
                    // shifted a pixel and then two, and the pixel before them added to all
                    var previous = Vector128.Create(left).AsByte();
                    for (; i + 4 <= count; i += 4)
                    {
                        var sums = Load(ref inp, i);
                        sums += ShiftPixelsUp(sums, 1);
                        sums += ShiftPixelsUp(sums, 2);
                        sums += previous;
                        Store(ref outp, i, sums);
                        previous = Vector128.Create(sums.AsUInt32().GetElement(3)).AsByte();
                    }

                    left = previous.AsUInt32().GetElement(0);
                }

                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), left);
                    Unsafe.Add(ref outp, i) = left;
                }

                break;

            case 5:
                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), Average3(left, Unsafe.Add(ref up, i), Unsafe.Add(ref up, i + 1)));
                    Unsafe.Add(ref outp, i) = left;
                }

                break;

            case 6:
                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), Average2(left, Unsafe.Add(ref up, i - 1)));
                    Unsafe.Add(ref outp, i) = left;
                }

                break;

            case 7:
                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), Average2(left, Unsafe.Add(ref up, i)));
                    Unsafe.Add(ref outp, i) = left;
                }

                break;

            case 10:
                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), Average4(left, Unsafe.Add(ref up, i - 1), Unsafe.Add(ref up, i), Unsafe.Add(ref up, i + 1)));
                    Unsafe.Add(ref outp, i) = left;
                }

                break;

            case 11:
                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), Select(Unsafe.Add(ref up, i), left, Unsafe.Add(ref up, i - 1)));
                    Unsafe.Add(ref outp, i) = left;
                }

                break;

            case 12:
                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), ClampedAddSubtractFull(left, Unsafe.Add(ref up, i), Unsafe.Add(ref up, i - 1)));
                    Unsafe.Add(ref outp, i) = left;
                }

                break;

            default:
                for (; i < count; i++)
                {
                    left = AddPixels(Unsafe.Add(ref inp, i), ClampedAddSubtractHalf(left, Unsafe.Add(ref up, i), Unsafe.Add(ref up, i - 1)));
                    Unsafe.Add(ref outp, i) = left;
                }

                break;
        }
    }

    // The four pixels moved up by one or two places, zeros below
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ShiftPixelsUp(
        Vector128<byte> pixels,
        [ConstantExpected(Min = 1, Max = 2)] byte count)
    {
        if (AdvSimd.IsSupported)
        {
            return (count == 1) ? AdvSimd.ExtractVector128(Vector128<byte>.Zero, pixels, 12) : AdvSimd.ExtractVector128(Vector128<byte>.Zero, pixels, 8);
        }

        return (count == 1) ? Sse2.ShiftLeftLogical128BitLane(pixels, 4) : Sse2.ShiftLeftLogical128BitLane(pixels, 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load(
        ref uint pixels,
        int index)
    {
        return Vector128.LoadUnsafe(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref pixels, index)));
    }

    // Two pixels, as the low half of Load's four. A widening add or subtract of x.GetLower()
    // compiles incorrectly on .NET 11's Arm64 JIT (it takes the wide form, which reads all of x:
    // dotnet/runtime#137322), so those load their low halves with this instead
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector64<byte> LoadLower(
        ref uint pixels,
        int index)
    {
        return Vector64.LoadUnsafe(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref pixels, index)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(
        ref uint pixels,
        int index,
        Vector128<byte> value)
    {
        value.StoreUnsafe(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref pixels, index)));
    }

    // The bytes' averages, rounded down (libwebp's Average2, a byte at a time)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Average2(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        if (AdvSimd.IsSupported)
        {
            return AdvSimd.FusedAddHalving(a, b);
        }

        return (((a ^ b) & Vector128.Create((byte)0xfe)).AsUInt32() >> 1).AsByte() + (a & b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddPixels(
        uint a,
        uint b)
    {
        uint alphaAndGreen = (a & 0xff00ff00) + (b & 0xff00ff00);
        uint redAndBlue = (a & 0x00ff00ff) + (b & 0x00ff00ff);
        return (alphaAndGreen & 0xff00ff00) | (redAndBlue & 0x00ff00ff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average2(
        uint a0,
        uint a1)
    {
        return (((a0 ^ a1) & 0xfefefefe) >> 1) + (a0 & a1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average3(
        uint a0,
        uint a1,
        uint a2)
    {
        return Average2(Average2(a0, a2), a1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average4(
        uint a0,
        uint a1,
        uint a2,
        uint a3)
    {
        return Average2(Average2(a0, a1), Average2(a2, a3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Clip255(
        uint a)
    {
        return (a < 256) ? a : (~a >> 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ClampedAddSubtractFull(
        uint c0,
        uint c1,
        uint c2)
    {
        uint a = Clip255((c0 >> 24) + (c1 >> 24) - (c2 >> 24));
        uint r = Clip255(((c0 >> 16) & 0xff) + ((c1 >> 16) & 0xff) - ((c2 >> 16) & 0xff));
        uint g = Clip255(((c0 >> 8) & 0xff) + ((c1 >> 8) & 0xff) - ((c2 >> 8) & 0xff));
        uint b = Clip255((c0 & 0xff) + (c1 & 0xff) - (c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddSubtractComponentHalf(
        int a,
        int b)
    {
        return Clip255((uint)(a + ((a - b) / 2)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ClampedAddSubtractHalf(
        uint c0,
        uint c1,
        uint c2)
    {
        uint ave = Average2(c0, c1);
        uint a = AddSubtractComponentHalf((int)(ave >> 24), (int)(c2 >> 24));
        uint r = AddSubtractComponentHalf((int)((ave >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        uint g = AddSubtractComponentHalf((int)((ave >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        uint b = AddSubtractComponentHalf((int)(ave & 0xff), (int)(c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sub3(
        int a,
        int b,
        int c)
    {
        int pb = b - c;
        int pa = a - c;
        return Math.Abs(pb) - Math.Abs(pa);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Select(
        uint a,
        uint b,
        uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
            Sub3((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff), (int)((c >> 16) & 0xff)) +
            Sub3((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff), (int)((c >> 8) & 0xff)) +
            Sub3((int)(a & 0xff), (int)(b & 0xff), (int)(c & 0xff));
        return (paMinusPb <= 0) ? a : b;
    }

    // The cross-color transform: red from green, blue from green and red, by each tile's multipliers
    private static void ColorSpaceInverse(
        ref Transform transform,
        int yStart,
        int yEnd,
        uint[] pixels,
        int src,
        int dst)
    {
        int w = transform.XSize;
        int tileWidth = 1 << transform.Bits;
        int mask = tileWidth - 1;
        int tilesPerRow = SubSampleSize(w, transform.Bits);
        int predRow = (yStart >> transform.Bits) * tilesPerRow;
        var data = transform.Data;
        for (int y = yStart; y < yEnd; y++)
        {
            int pred = predRow;
            for (int x = 0; x < w; x += tileWidth)
            {
                uint code = data[pred++];
                int count = Math.Min(tileWidth, w - x);
                TransformColorInverse((sbyte)code, (sbyte)(code >> 8), (sbyte)(code >> 16), pixels.AsSpan(src + x, count), pixels.AsSpan(dst + x, count));
            }

            src += w;
            dst += w;
            if (((y + 1) & mask) == 0)
            {
                predRow += tilesPerRow;
            }
        }
    }

    private static void TransformColorInverse(
        int greenToRed,
        int greenToBlue,
        int redToBlue,
        ReadOnlySpan<uint> source,
        Span<uint> target)
    {
        int i = 0;
        ref uint s = ref MemoryMarshal.GetReference(source);
        ref uint d = ref MemoryMarshal.GetReference(target);
        if (Vector128.IsHardwareAccelerated)
        {
            // The channels as signed 32-bit lanes: green and red sign-extended from their bytes
            var g2r = Vector128.Create(greenToRed);
            var g2b = Vector128.Create(greenToBlue);
            var r2b = Vector128.Create(redToBlue);
            var low = Vector128.Create(0xff);
            var keep = Vector128.Create(unchecked((int)0xff00ff00));
            for (; i + 4 <= source.Length; i += 4)
            {
                var argb = Vector128.LoadUnsafe(ref s, (nuint)i).AsInt32();
                var green = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(argb, 16), 24);
                var red = (Vector128.ShiftRightLogical(argb, 16) + Vector128.ShiftRightArithmetic(g2r * green, 5)) & low;
                var signedRed = Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(red, 24), 24);
                var blue = (argb + Vector128.ShiftRightArithmetic(g2b * green, 5) + Vector128.ShiftRightArithmetic(r2b * signedRed, 5)) & low;
                var result = (argb & keep) | Vector128.ShiftLeft(red, 16) | blue;
                result.AsUInt32().StoreUnsafe(ref d, (nuint)i);
            }
        }

        for (; i < source.Length; i++)
        {
            uint argb = source[i];
            int green = (sbyte)(argb >> 8);
            int red = (int)((argb >> 16) & 0xff);
            int blue = (int)(argb & 0xff);
            red = (red + ((greenToRed * green) >> 5)) & 0xff;
            blue += (greenToBlue * green) >> 5;
            blue += (redToBlue * (sbyte)red) >> 5;
            blue &= 0xff;
            target[i] = (argb & 0xff00ff00) | ((uint)red << 16) | (uint)blue;
        }
    }

    // The palette: each pixel's green its index, or several indices packed in a pixel's green
    private static void ColorIndexInverse(
        ref Transform transform,
        int yStart,
        int yEnd,
        uint[] pixels,
        int src,
        int dst)
    {
        ref uint p = ref MemoryMarshal.GetArrayDataReference(pixels);
        MapColors(ref transform, yStart, yEnd, ref Unsafe.Add(ref p, src), ref Unsafe.Add(ref p, dst), transform.Data, ref transform.Expanded);
    }

    // A packed palette's table: for each packed byte, the pixels it holds (2, 4 or 8 of them)
    private static uint[] ExpandPalette(
        uint[] map,
        int bits)
    {
        int bitsPerPixel = 8 >> bits;
        int pixelsPerByte = 1 << bits;
        uint mask = (1u << bitsPerPixel) - 1;
        var table = new uint[256 * pixelsPerByte];
        for (int value = 0; value < 256; value++)
        {
            for (int k = 0; k < pixelsPerByte; k++)
            {
                table[(value * pixelsPerByte) + k] = map[(value >> (k * bitsPerPixel)) & mask];
            }
        }

        return table;
    }

    // The palette's indices mapped to the colors of 'map' (ARGB words, or the same colors as RGBA).
    // Packed indices go a byte at a time through 'expanded' (built from 'map' at the first rows big
    // enough to be worth it), each byte's pixels stored at once.
    private static void MapColors(
        ref Transform transform,
        int yStart,
        int yEnd,
        ref uint input,
        ref uint output,
        uint[] map,
        ref uint[]? expanded)
    {
        int bitsPerPixel = 8 >> transform.Bits;
        int w = transform.XSize;
        ref uint colors = ref MemoryMarshal.GetArrayDataReference(map);
        if ((bitsPerPixel < 8) && ((expanded is not null) || ((long)transform.XSize * transform.YSize >= 16384)))
        {
            expanded ??= ExpandPalette(map, transform.Bits);
            ref uint table = ref MemoryMarshal.GetArrayDataReference(expanded);
            int pixelsPerByte = 1 << transform.Bits;
            uint bitMask = (1u << bitsPerPixel) - 1;
            int src = 0;
            int dst = 0;
            for (int y = yStart; y < yEnd; y++)
            {
                int x = 0;
                for (; x + pixelsPerByte <= w; x += pixelsPerByte)
                {
                    ref uint pixelsOf = ref Unsafe.Add(ref table, (int)((Unsafe.Add(ref input, src++) >> 8) & 0xff) * pixelsPerByte);
                    ref uint target = ref Unsafe.Add(ref output, dst);
                    if (pixelsPerByte == 8)
                    {
                        Vector128.LoadUnsafe(ref pixelsOf).StoreUnsafe(ref target);
                        Vector128.LoadUnsafe(ref pixelsOf, 4).StoreUnsafe(ref target, 4);
                    }
                    else if (pixelsPerByte == 4)
                    {
                        Vector128.LoadUnsafe(ref pixelsOf).StoreUnsafe(ref target);
                    }
                    else
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref target), Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<uint, byte>(ref pixelsOf)));
                    }

                    dst += pixelsPerByte;
                }

                if (x < w)
                {
                    // The row's last byte, holding fewer pixels than it could
                    uint packedPixels = (Unsafe.Add(ref input, src++) >> 8) & 0xff;
                    for (; x < w; x++)
                    {
                        Unsafe.Add(ref output, dst++) = Unsafe.Add(ref colors, packedPixels & bitMask);
                        packedPixels >>= bitsPerPixel;
                    }
                }
            }
        }
        else if (bitsPerPixel < 8)
        {
            int countMask = (1 << transform.Bits) - 1;
            uint bitMask = (1u << bitsPerPixel) - 1;
            int src = 0;
            int dst = 0;
            for (int y = yStart; y < yEnd; y++)
            {
                uint packedPixels = 0;
                for (int x = 0; x < w; x++)
                {
                    if ((x & countMask) == 0)
                    {
                        packedPixels = (Unsafe.Add(ref input, src++) >> 8) & 0xff;
                    }

                    Unsafe.Add(ref output, dst++) = Unsafe.Add(ref colors, packedPixels & bitMask);
                    packedPixels >>= bitsPerPixel;
                }
            }
        }
        else
        {
            int count = (yEnd - yStart) * w;
            int i = 0;
            for (; i + 4 <= count; i += 4)
            {
                uint a = Unsafe.Add(ref input, i);
                uint b = Unsafe.Add(ref input, i + 1);
                uint c = Unsafe.Add(ref input, i + 2);
                uint e = Unsafe.Add(ref input, i + 3);
                Unsafe.Add(ref output, i) = Unsafe.Add(ref colors, (a >> 8) & 0xff);
                Unsafe.Add(ref output, i + 1) = Unsafe.Add(ref colors, (b >> 8) & 0xff);
                Unsafe.Add(ref output, i + 2) = Unsafe.Add(ref colors, (c >> 8) & 0xff);
                Unsafe.Add(ref output, i + 3) = Unsafe.Add(ref colors, (e >> 8) & 0xff);
            }

            for (; i < count; i++)
            {
                Unsafe.Add(ref output, i) = Unsafe.Add(ref colors, (Unsafe.Add(ref input, i) >> 8) & 0xff);
            }
        }
    }

    // The alpha of paletted alpha rows: each index's color's green
    private static void MapAlpha(
        ref Transform transform,
        int yStart,
        int yEnd,
        byte[] indices,
        int src,
        byte[] plane,
        int dst)
    {
        int bitsPerPixel = 8 >> transform.Bits;
        int w = transform.XSize;
        var map = transform.Data;
        if (bitsPerPixel < 8)
        {
            int countMask = (1 << transform.Bits) - 1;
            uint bitMask = (1u << bitsPerPixel) - 1;
            for (int y = yStart; y < yEnd; y++)
            {
                uint packedPixels = 0;
                for (int x = 0; x < w; x++)
                {
                    if ((x & countMask) == 0)
                    {
                        packedPixels = indices[src++];
                    }

                    plane[dst++] = (byte)(map[packedPixels & bitMask] >> 8);
                    packedPixels >>= bitsPerPixel;
                }
            }
        }
        else
        {
            int count = (yEnd - yStart) * w;
            for (int i = 0; i < count; i++)
            {
                plane[dst + i] = (byte)(map[indices[src + i]] >> 8);
            }
        }
    }

    // ARGB words (blue in the low byte) to RGBA bytes: red and blue swapped
    private static void ConvertBgraToRgba(
        ReadOnlySpan<uint> source,
        Span<uint> target)
    {
        ref uint s = ref MemoryMarshal.GetReference(source);
        ref uint d = ref MemoryMarshal.GetReference(target);
        int i = 0;
        var keep = Vector128.Create(0xff00ff00);
        var low = Vector128.Create(0xffu);
        for (; i + 4 <= source.Length; i += 4)
        {
            var argb = Vector128.LoadUnsafe(ref s, (nuint)i);
            var rgba = (argb & keep) | ((argb >> 16) & low) | ((argb & low) << 16);
            rgba.StoreUnsafe(ref d, (nuint)i);
        }

        for (; i < source.Length; i++)
        {
            uint argb = Unsafe.Add(ref s, i);
            Unsafe.Add(ref d, i) = (argb & 0xff00ff00) | ((argb >> 16) & 0xff) | ((argb & 0xff) << 16);
        }
    }
}
