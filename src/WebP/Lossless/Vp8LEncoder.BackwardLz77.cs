// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/backward_references_enc.c and src/utils/color_cache_utils.c),
// modified by Hot Chai Games LLC: written in C#, the references in one growing array rather than a list of
// blocks. libwebp carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddSingleLiteral(
        uint pixel,
        bool useColorCache,
        in ColorCache hashers,
        BackwardRefs refs)
    {
        if (useColorCache)
        {
            int key = hashers.GetIndex(pixel);
            if (hashers.Colors[key] == pixel)
            {
                refs.Add(PixOrCopy.CacheIndex(key));
            }
            else
            {
                refs.Add(PixOrCopy.Literal(pixel));
                hashers.Colors[key] = pixel;
            }
        }
        else
        {
            refs.Add(PixOrCopy.Literal(pixel));
        }
    }

    private static void BackwardReferencesRle(
        int xsize,
        int ysize,
        ReadOnlySpan<uint> argb,
        int cacheBits,
        BackwardRefs refs)
    {
        int pixelCount = xsize * ysize;
        bool useColorCache = cacheBits > 0;
        var hashers = useColorCache ? new ColorCache(cacheBits) : default;
        refs.Clear();
        AddSingleLiteral(argb[0], useColorCache, hashers, refs);
        int i = 1;
        while (i < pixelCount)
        {
            int maxLen = Math.Min(pixelCount - i, MaxLength);
            int rleLength = FindMatchLength(argb, i, i - 1, 0, maxLen);
            int previousRowLength = (i < xsize) ? 0 : FindMatchLength(argb, i, i - xsize, 0, maxLen);
            if ((rleLength >= previousRowLength) && (rleLength >= MinLength))
            {
                refs.Add(PixOrCopy.Copy(1, rleLength));
                i += rleLength;
            }
            else if (previousRowLength >= MinLength)
            {
                refs.Add(PixOrCopy.Copy((uint)xsize, previousRowLength));
                if (useColorCache)
                {
                    for (int k = 0; k < previousRowLength; k++)
                    {
                        hashers.Insert(argb[i + k]);
                    }
                }

                i += previousRowLength;
            }
            else
            {
                AddSingleLiteral(argb[i], useColorCache, hashers, refs);
                i++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BackwardReferencesLz77(
        int xsize,
        int ysize,
        ReadOnlySpan<uint> argb,
        int cacheBits,
        HashChain hashChain,
        BackwardRefs refs)
    {
        int iLastCheck = -1;
        bool useColorCache = cacheBits > 0;
        int pixelCount = xsize * ysize;
        var hashers = useColorCache ? new ColorCache(cacheBits) : default;
        refs.Clear();
        var offsetLength = hashChain.OffsetLength.AsSpan(0, pixelCount);
        for (int i = 0; i < pixelCount;)
        {
            int offset = (int)(offsetLength[i] >> MaxLengthBits);
            int length = (int)(offsetLength[i] & ((1u << MaxLengthBits) - 1));
            if (length >= MinLength)
            {
                int lengthStart = length;
                int maxReach = 0;
                int jMax = (i + lengthStart >= pixelCount) ? pixelCount - 1 : i + lengthStart;
                iLastCheck = (i > iLastCheck) ? i : iLastCheck;

                // The best match for this pixel and the next together: [i, j) then j's best match
                for (int j = iLastCheck + 1; j <= jMax; j++)
                {
                    int lengthJ = (int)(offsetLength[j] & ((1u << MaxLengthBits) - 1));
                    int reach = j + ((lengthJ >= MinLength) ? lengthJ : 1);
                    if (reach > maxReach)
                    {
                        length = j - i;
                        maxReach = reach;
                        if (maxReach >= pixelCount)
                        {
                            break;
                        }
                    }
                }
            }
            else
            {
                length = 1;
            }

            if (length == 1)
            {
                AddSingleLiteral(argb[i], useColorCache, hashers, refs);
            }
            else
            {
                refs.Add(PixOrCopy.Copy((uint)offset, length));
                if (useColorCache)
                {
                    ref uint colors = ref MemoryMarshal.GetArrayDataReference(hashers.Colors);
                    int shift = hashers.HashShift;
                    foreach (uint pix in argb.Slice(i, length))
                    {
                        Unsafe.Add(ref colors, (nint)(uint)HashPix(pix, shift)) = pix;
                    }
                }
            }

            i += length;
        }
    }

    // LZ77 with the matches limited to the 32 nearest distances of the plane code
    private static void BackwardReferencesLz77Box(
        int xsize,
        int ysize,
        ReadOnlySpan<uint> argb,
        int cacheBits,
        HashChain hashChainBest,
        HashChain hashChain,
        BackwardRefs refs)
    {
        const int WindowOffsetsSizeMax = 32;
        int pixelCount = xsize * ysize;
        Span<int> windowOffsets = stackalloc int[WindowOffsetsSizeMax];
        Span<int> windowOffsetsNew = stackalloc int[WindowOffsetsSizeMax];
        windowOffsets.Clear();
        windowOffsetsNew.Clear();
        int windowOffsetsSize = 0;
        int windowOffsetsNewSize = 0;
        var counts = new ushort[pixelCount];
        int bestOffsetPrevious = -1, bestLengthPrevious = -1;

        // How many times each pixel repeats from its position
        {
            int i = pixelCount - 2;
            counts[i + 1] = 1;
            for (; i >= 0; --i)
            {
                if (argb[i] == argb[i + 1])
                {
                    counts[i] = (ushort)(counts[i + 1] + ((counts[i + 1] != MaxLength) ? 1 : 0));
                }
                else
                {
                    counts[i] = 1;
                }
            }
        }

        // The offsets around a pixel, in the plane code's spiral order
        for (int y = 0; y <= 6; y++)
        {
            for (int x = -6; x <= 6; x++)
            {
                int offset = (y * xsize) + x;
                if (offset <= 0)
                {
                    continue;
                }

                int planeCode = DistanceToPlaneCode(xsize, offset) - 1;
                if (planeCode >= WindowOffsetsSizeMax)
                {
                    continue;
                }

                windowOffsets[planeCode] = offset;
            }
        }

        for (int i = 0; i < WindowOffsetsSizeMax; i++)
        {
            if (windowOffsets[i] == 0)
            {
                continue;
            }

            windowOffsets[windowOffsetsSize++] = windowOffsets[i];
        }

        // The offsets that reach pixels P - 1 can't with any of them
        for (int i = 0; i < windowOffsetsSize; i++)
        {
            bool isReachable = false;
            for (int j = 0; (j < windowOffsetsSize) && !isReachable; j++)
            {
                isReachable |= windowOffsets[i] == windowOffsets[j] + 1;
            }

            if (!isReachable)
            {
                windowOffsetsNew[windowOffsetsNewSize++] = windowOffsets[i];
            }
        }

        var offsetLength = hashChain.OffsetLength;
        offsetLength[0] = 0;
        for (int i = 1; i < pixelCount; i++)
        {
            int bestLength = hashChainBest.FindLength(i);
            int bestOffset = 0;
            bool doCompute = true;
            if (bestLength >= MaxLength)
            {
                bestOffset = hashChainBest.FindOffset(i);
                for (int ind = 0; ind < windowOffsetsSize; ind++)
                {
                    if (bestOffset == windowOffsets[ind])
                    {
                        doCompute = false;
                        break;
                    }
                }
            }

            if (doCompute)
            {
                bool usePrevious = (bestLengthPrevious > 1) && (bestLengthPrevious < MaxLength);
                int numInd = usePrevious ? windowOffsetsNewSize : windowOffsetsSize;
                bestLength = usePrevious ? bestLengthPrevious - 1 : 0;
                bestOffset = usePrevious ? bestOffsetPrevious : 0;
                for (int ind = 0; ind < numInd; ind++)
                {
                    int currentLength = 0;
                    int j = i;
                    int jOffset = usePrevious ? i - windowOffsetsNew[ind] : i - windowOffsets[ind];
                    if ((jOffset < 0) || (argb[jOffset] != argb[i]))
                    {
                        continue;
                    }

                    do
                    {
                        int countsJOffset = counts[jOffset];
                        int countsJ = counts[j];
                        if (countsJOffset != countsJ)
                        {
                            currentLength += (countsJOffset < countsJ) ? countsJOffset : countsJ;
                            break;
                        }

                        currentLength += countsJOffset;
                        jOffset += countsJOffset;
                        j += countsJOffset;
                    }
                    while ((currentLength <= MaxLength) && (j < pixelCount) && (argb[jOffset] == argb[j]));

                    if (bestLength < currentLength)
                    {
                        bestOffset = usePrevious ? windowOffsetsNew[ind] : windowOffsets[ind];
                        if (currentLength >= MaxLength)
                        {
                            bestLength = MaxLength;
                            break;
                        }
                        else
                        {
                            bestLength = currentLength;
                        }
                    }
                }
            }

            if (bestLength <= MinLength)
            {
                offsetLength[i] = 0;
                bestOffsetPrevious = 0;
                bestLengthPrevious = 0;
            }
            else
            {
                offsetLength[i] = ((uint)bestOffset << MaxLengthBits) | (uint)bestLength;
                bestOffsetPrevious = bestOffset;
                bestLengthPrevious = bestLength;
            }
        }

        offsetLength[0] = 0;
        BackwardReferencesLz77(xsize, ysize, argb, cacheBits, hashChain, refs);
    }

    private static void BackwardReferences2DLocality(
        int xsize,
        BackwardRefs refs)
    {
        var items = refs.Span;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Mode == ModeCopy)
            {
                items[i].ArgbOrDistance = (uint)DistanceToPlaneCode(xsize, (int)items[i].ArgbOrDistance);
            }
        }
    }
}
