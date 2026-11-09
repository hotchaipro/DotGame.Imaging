// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/utils/palette.c, and the palette parts of src/enc/vp8l_enc.c),
// modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2023 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private const int MaxPaletteSize = 256;

    private const int SortedDefault = 0;
    private const int MinimizeDelta = 1;
    private const int ModifiedZeng = 2;
    private const int UnusedPalette = 3;
    private const int PaletteSortingNum = 4;

    // The image's colors, sorted, if it has at most 256 (GetColorPalette): else 257
    private static int GetColorPalette(
        ReadOnlySpan<uint> argb,
        Span<uint> palette)
    {
        const int ColorHashSize = MaxPaletteSize * 4;
        const int ColorHashRightShift = 22;
        Span<bool> inUse = stackalloc bool[ColorHashSize];
        Span<uint> colors = stackalloc uint[ColorHashSize];
        inUse.Clear();
        colors.Clear();
        int numColors = 0;
        uint lastPixel = ~argb[0];
        ref uint pixels = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(argb);
        for (int i = 0; i < argb.Length; i++)
        {
            uint pixel = System.Runtime.CompilerServices.Unsafe.Add(ref pixels, i);
            if (pixel == lastPixel)
            {
                continue;
            }

            lastPixel = pixel;
            int key = HashPix(lastPixel, ColorHashRightShift);
            while (true)
            {
                ref bool used = ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(inUse), key & (ColorHashSize - 1));
                ref uint entry = ref System.Runtime.CompilerServices.Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(colors), key & (ColorHashSize - 1));
                if (!used)
                {
                    entry = lastPixel;
                    used = true;
                    ++numColors;
                    if (numColors > MaxPaletteSize)
                    {
                        return MaxPaletteSize + 1;
                    }

                    break;
                }
                else if (entry == lastPixel)
                {
                    break;
                }
                else
                {
                    ++key;
                    key &= ColorHashSize - 1;
                }
            }
        }

        numColors = 0;
        for (int i = 0; i < ColorHashSize; i++)
        {
            if (inUse[i])
            {
                palette[numColors++] = colors[i];
            }
        }

        palette.Slice(0, numColors).Sort();
        return numColors;
    }

    private static uint PaletteComponentDistance(
        uint v)
    {
        return (v <= 128) ? v : (256 - v);
    }

    private static uint PaletteColorDistance(
        uint color1,
        uint color2)
    {
        uint diff = SubPixels(color1, color2);
        const uint MoreWeightForRgbThanForAlpha = 9;
        uint score = PaletteComponentDistance(diff & 0xff);
        score += PaletteComponentDistance((diff >> 8) & 0xff);
        score += PaletteComponentDistance((diff >> 16) & 0xff);
        score *= MoreWeightForRgbThanForAlpha;
        score += PaletteComponentDistance((diff >> 24) & 0xff);
        return score;
    }

    private static int SearchColorNoIdx(
        ReadOnlySpan<uint> sorted,
        uint color,
        int numColors)
    {
        int low = 0, hi = numColors;
        if (sorted[low] == color)
        {
            return low;
        }

        while (true)
        {
            int mid = (low + hi) >> 1;
            if (sorted[mid] == color)
            {
                return mid;
            }
            else if (sorted[mid] < color)
            {
                low = mid;
            }
            else
            {
                hi = mid;
            }
        }
    }

    private static void PrepareMapToPalette(
        ReadOnlySpan<uint> palette,
        int numColors,
        Span<uint> sorted,
        Span<uint> indexMap)
    {
        palette.Slice(0, numColors).CopyTo(sorted);
        sorted.Slice(0, numColors).Sort();
        for (int i = 0; i < numColors; i++)
        {
            indexMap[SearchColorNoIdx(sorted, palette[i], numColors)] = (uint)i;
        }
    }

    private static bool PaletteHasNonMonotonousDeltas(
        ReadOnlySpan<uint> palette,
        int numColors)
    {
        uint predict = 0;
        int signFound = 0;
        for (int i = 0; i < numColors; i++)
        {
            uint diff = SubPixels(palette[i], predict);
            uint rd = (diff >> 16) & 0xff;
            uint gd = (diff >> 8) & 0xff;
            uint bd = diff & 0xff;
            if (rd != 0)
            {
                signFound |= (rd < 0x80) ? 1 : 2;
            }

            if (gd != 0)
            {
                signFound |= (gd < 0x80) ? 8 : 16;
            }

            if (bd != 0)
            {
                signFound |= (bd < 0x80) ? 64 : 128;
            }

            predict = palette[i];
        }

        return (signFound & (signFound << 1)) != 0;
    }

    private static void PaletteSortMinimizeDeltas(
        ReadOnlySpan<uint> paletteSorted,
        int numColors,
        Span<uint> palette)
    {
        uint predict = 0;
        paletteSorted.Slice(0, numColors).CopyTo(palette);
        if (!PaletteHasNonMonotonousDeltas(paletteSorted, numColors))
        {
            return;
        }

        if ((numColors > 17) && (palette[0] == 0))
        {
            --numColors;
            (palette[numColors], palette[0]) = (palette[0], palette[numColors]);
        }

        for (int i = 0; i < numColors; i++)
        {
            int bestIx = i;
            uint bestScore = ~0u;
            for (int k = i; k < numColors; k++)
            {
                uint currentScore = PaletteColorDistance(palette[k], predict);
                if (bestScore > currentScore)
                {
                    bestScore = currentScore;
                    bestIx = k;
                }
            }

            (palette[bestIx], palette[i]) = (palette[i], palette[bestIx]);
            predict = palette[i];
        }
    }

    private static void CoOccurrenceFindMax(
        ReadOnlySpan<uint> cooccurrence,
        int numColors,
        out byte c1,
        out byte c2)
    {
        uint bestSum = 0;
        c1 = 0;
        for (int i = 0; i < numColors; i++)
        {
            uint sum = 0;
            for (int j = 0; j < numColors; j++)
            {
                sum += cooccurrence[(i * numColors) + j];
            }

            if (sum > bestSum)
            {
                bestSum = sum;
                c1 = (byte)i;
            }
        }

        c2 = 0;
        uint bestCooccurrence = 0;
        for (int i = 0; i < numColors; i++)
        {
            if (cooccurrence[(c1 * numColors) + i] > bestCooccurrence)
            {
                bestCooccurrence = cooccurrence[(c1 * numColors) + i];
                c2 = (byte)i;
            }
        }
    }

    private static void CoOccurrenceBuild(
        ReadOnlySpan<uint> argb,
        int width,
        int height,
        ReadOnlySpan<uint> palette,
        int numColors,
        Span<uint> cooccurrence)
    {
        var lines = new uint[2 * width];
        var lineTop = lines.AsSpan(0, width);
        var lineCurrent = lines.AsSpan(width, width);
        Span<uint> indexMap = stackalloc uint[MaxPaletteSize];
        Span<uint> paletteSorted = stackalloc uint[MaxPaletteSize];
        indexMap.Clear();
        uint previousPixel = ~argb[0];
        uint previousIndex = 0;
        PrepareMapToPalette(palette, numColors, paletteSorted, indexMap);
        for (int y = 0; y < height; y++)
        {
            var source = argb.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                uint pixel = source[x];
                if (pixel != previousPixel)
                {
                    previousIndex = indexMap[SearchColorNoIdx(paletteSorted, pixel, numColors)];
                    previousPixel = pixel;
                }

                lineCurrent[x] = previousIndex;
                if ((x > 0) && (previousIndex != lineCurrent[x - 1]))
                {
                    uint leftIndex = lineCurrent[x - 1];
                    cooccurrence[(int)((previousIndex * numColors) + leftIndex)]++;
                    cooccurrence[(int)((leftIndex * numColors) + previousIndex)]++;
                }

                if ((y > 0) && (previousIndex != lineTop[x]))
                {
                    uint topIndex = lineTop[x];
                    cooccurrence[(int)((previousIndex * numColors) + topIndex)]++;
                    cooccurrence[(int)((topIndex * numColors) + previousIndex)]++;
                }
            }

            var lineTmp = lineTop;
            lineTop = lineCurrent;
            lineCurrent = lineTmp;
        }
    }

    private static void PaletteSortModifiedZeng(
        ReadOnlySpan<uint> argb,
        int width,
        int height,
        ReadOnlySpan<uint> paletteIn,
        int numColors,
        Span<uint> palette)
    {
        if (numColors <= 1)
        {
            return;
        }

        var cooccurrence = new uint[numColors * numColors];
        CoOccurrenceBuild(argb, width, height, paletteIn, numColors, cooccurrence);
        Span<byte> remapping = stackalloc byte[MaxPaletteSize];
        Span<byte> sumIndex = stackalloc byte[MaxPaletteSize];
        Span<uint> sumValue = stackalloc uint[MaxPaletteSize];
        CoOccurrenceFindMax(cooccurrence, numColors, out remapping[0], out remapping[1]);
        uint first = 0;
        uint last = 1;
        uint numSums = (uint)numColors - 2;
        if (numSums > 0)
        {
            int bestSum = 0;
            sumIndex[0] = 0;
            sumValue[0] = 0;
            for (int i = 0, j = 0; i < numColors; i++)
            {
                if ((i == remapping[0]) || (i == remapping[1]))
                {
                    continue;
                }

                sumIndex[j] = (byte)i;
                sumValue[j] = cooccurrence[(i * numColors) + remapping[0]] + cooccurrence[(i * numColors) + remapping[1]];
                if (sumValue[j] > sumValue[bestSum])
                {
                    bestSum = j;
                }

                ++j;
            }

            while (numSums > 0)
            {
                byte bestIndex = sumIndex[bestSum];

                // Prepended or appended, as the delta says
                int delta = 0;
                int n = numColors - (int)numSums;
                for (uint ind = first, j = 0; ((ind + j) % (uint)numColors) != last + 1; ++j)
                {
                    ushort lj = remapping[(int)((ind + j) % (uint)numColors)];
                    delta += (n - 1 - (2 * (int)j)) * (int)cooccurrence[(bestIndex * numColors) + lj];
                }

                if (delta > 0)
                {
                    first = (first == 0) ? (uint)numColors - 1 : first - 1;
                    remapping[(int)first] = bestIndex;
                }
                else
                {
                    ++last;
                    remapping[(int)last] = bestIndex;
                }

                // bestSum out of the sums
                sumIndex[bestSum] = sumIndex[(int)numSums - 1];
                sumValue[bestSum] = sumValue[(int)numSums - 1];
                --numSums;

                // The sums updated, and the best of them
                bestSum = 0;
                for (int i = 0; i < numSums; i++)
                {
                    sumValue[i] += cooccurrence[(bestIndex * numColors) + sumIndex[i]];
                    if (sumValue[i] > sumValue[bestSum])
                    {
                        bestSum = i;
                    }
                }
            }
        }

        for (int i = 0; i < numColors; i++)
        {
            palette[i] = paletteIn[remapping[(int)((first + (uint)i) % (uint)numColors)]];
        }
    }

    private static void PaletteSort(
        int method,
        ReadOnlySpan<uint> argb,
        int width,
        int height,
        ReadOnlySpan<uint> paletteSorted,
        int numColors,
        Span<uint> palette)
    {
        switch (method)
        {
            case SortedDefault:
                if ((paletteSorted[0] == 0) && (numColors > 17))
                {
                    paletteSorted.Slice(1, numColors - 1).CopyTo(palette);
                    palette[numColors - 1] = 0;
                }
                else
                {
                    paletteSorted.Slice(0, numColors).CopyTo(palette);
                }

                break;

            case MinimizeDelta:
                PaletteSortMinimizeDeltas(paletteSorted, numColors, palette);
                break;

            case ModifiedZeng:
                // A single color keeps palette as it was
                if (numColors <= 1)
                {
                    break;
                }

                PaletteSortModifiedZeng(argb, width, height, paletteSorted, numColors, palette);
                break;
        }
    }

    private static uint ApplyPaletteHash1(
        uint color)
    {
        // The color without alpha
        return (uint)((color & 0x00ffffffu) * 4222244071ul) >> (32 - 11);
    }

    private static uint ApplyPaletteHash2(
        uint color)
    {
        return (uint)((color & 0x00ffffffu) * ((1ul << 31) - 1)) >> (32 - 11);
    }

    // Each pixel replaced by its palette index, packed xbits to a pixel (ApplyPalette)
    private static void ApplyPalette(
        ReadOnlySpan<uint> source,
        int sourceStride,
        Span<uint> destination,
        int destinationStride,
        ReadOnlySpan<uint> palette,
        int paletteSize,
        int width,
        int height,
        int xbits)
    {
        var row = new byte[width];
        if (paletteSize < 4)
        {
            uint previousPixel = palette[0];
            uint previousIndex = 0;
            for (int y = 0; y < height; y++)
            {
                var src = source.Slice(y * sourceStride, width);
                for (int x = 0; x < width; x++)
                {
                    uint pixel = src[x];
                    if (pixel != previousPixel)
                    {
                        previousIndex = (pixel == palette[0]) ? 0u : (pixel == palette[1]) ? 1u : (pixel == palette[2]) ? 2u : 3u;
                        previousPixel = pixel;
                    }

                    row[x] = (byte)previousIndex;
                }

                BundleColorMap(row, width, xbits, destination.Slice(y * destinationStride));
            }

            return;
        }

        // A perfect hash from color to index, if one of three is
        Span<ushort> buffer = stackalloc ushort[1 << 11];
        int hash;
        for (hash = 0; hash < 3; hash++)
        {
            bool useLut = true;
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer).Fill(0xff);
            for (int j = 0; j < paletteSize; j++)
            {
                uint index = (hash == 0) ? ((palette[j] >> 8) & 0xff) : (hash == 1) ? ApplyPaletteHash1(palette[j]) : ApplyPaletteHash2(palette[j]);
                if (buffer[(int)index] != 0xffff)
                {
                    useLut = false;
                    break;
                }

                buffer[(int)index] = (ushort)j;
            }

            if (useLut)
            {
                break;
            }
        }

        Span<uint> indexMap = stackalloc uint[MaxPaletteSize];
        Span<uint> paletteSorted = stackalloc uint[MaxPaletteSize];
        if (hash == 3)
        {
            PrepareMapToPalette(palette, paletteSize, paletteSorted, indexMap);
        }

        {
            uint previousPixel = palette[0];
            uint previousIndex = 0;
            for (int y = 0; y < height; y++)
            {
                ref uint src = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(source.Slice(y * sourceStride, width));
                ref byte indices = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(row);
                for (nint x = 0; x < width; x++)
                {
                    uint pixel = System.Runtime.CompilerServices.Unsafe.Add(ref src, x);
                    if (pixel != previousPixel)
                    {
                        previousIndex = hash switch
                        {
                            0 => buffer[(int)((pixel >> 8) & 0xff)],
                            1 => buffer[(int)ApplyPaletteHash1(pixel)],
                            2 => buffer[(int)ApplyPaletteHash2(pixel)],
                            _ => indexMap[SearchColorNoIdx(paletteSorted, pixel, paletteSize)],
                        };
                        previousPixel = pixel;
                    }

                    System.Runtime.CompilerServices.Unsafe.Add(ref indices, x) = (byte)previousIndex;
                }

                BundleColorMap(row, width, xbits, destination.Slice(y * destinationStride));
            }
        }
    }
}
