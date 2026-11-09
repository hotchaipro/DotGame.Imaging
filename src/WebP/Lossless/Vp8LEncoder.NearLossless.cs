// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/predictor_enc.c and near_lossless_enc.c), modified by Hot Chai Games
// LLC: written in C#. libwebp carries this notice:
//   Copyright 2016 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private static uint FindClosestDiscretized(
        uint a,
        int bits)
    {
        uint mask = (1u << bits) - 1;
        uint biased = a + (mask >> 1) + ((a >> bits) & 1);
        if (biased > 0xff)
        {
            return 0xff;
        }

        return biased & ~mask;
    }

    private static uint ClosestDiscretizedArgb(
        uint a,
        int bits)
    {
        return (FindClosestDiscretized(a >> 24, bits) << 24) |
               (FindClosestDiscretized((a >> 16) & 0xff, bits) << 16) |
               (FindClosestDiscretized((a >> 8) & 0xff, bits) << 8) |
               FindClosestDiscretized(a & 0xff, bits);
    }

    private static bool IsNear(
        uint a,
        uint b,
        int limit)
    {
        for (int k = 0; k < 4; k++)
        {
            int delta = (int)((a >> (k * 8)) & 0xff) - (int)((b >> (k * 8)) & 0xff);
            if ((delta >= limit) || (delta <= -limit))
            {
                return false;
            }
        }

        return true;
    }

    private static void NearLosslessPass(
        int xsize,
        int ysize,
        ReadOnlySpan<uint> argbSource,
        int stride,
        int limitBits,
        Span<uint> copyBuffer,
        Span<uint> argbDestination)
    {
        int limit = 1 << limitBits;
        var previousRow = copyBuffer.Slice(0, xsize);
        var currentRow = copyBuffer.Slice(xsize, xsize);
        var nextRow = copyBuffer.Slice(2 * xsize, xsize);
        argbSource.Slice(0, xsize).CopyTo(currentRow);
        argbSource.Slice(stride, xsize).CopyTo(nextRow);
        for (int y = 0; y < ysize; y++)
        {
            int source = y * stride;
            int destination = y * xsize;
            if ((y == 0) || (y == ysize - 1))
            {
                argbSource.Slice(source, xsize).CopyTo(argbDestination.Slice(destination));
            }
            else
            {
                argbSource.Slice(source + stride, xsize).CopyTo(nextRow);
                argbDestination[destination] = argbSource[source];
                argbDestination[destination + xsize - 1] = argbSource[source + xsize - 1];
                for (int x = 1; x < xsize - 1; x++)
                {
                    uint pixel = currentRow[x];
                    bool smooth = IsNear(pixel, currentRow[x - 1], limit) && IsNear(pixel, currentRow[x + 1], limit) &&
                                  IsNear(pixel, previousRow[x], limit) && IsNear(pixel, nextRow[x], limit);
                    argbDestination[destination + x] = smooth ? pixel : ClosestDiscretizedArgb(pixel, limitBits);
                }
            }

            var temp = previousRow;
            previousRow = currentRow;
            currentRow = nextRow;
            nextRow = temp;
        }
    }

    // VP8ApplyNearLossless
    private static void ApplyNearLossless(
        int xsize,
        int ysize,
        ReadOnlySpan<uint> argb,
        int quality,
        Span<uint> argbDestination)
    {
        const int MinDimForNearLossless = 64;
        int limitBits = NearLosslessBits(quality);
        if (((xsize < MinDimForNearLossless) && (ysize < MinDimForNearLossless)) || (ysize < 3))
        {
            argb.Slice(0, xsize * ysize).CopyTo(argbDestination);
            return;
        }

        var copyBuffer = new uint[xsize * 3];
        NearLosslessPass(xsize, ysize, argb, xsize, limitBits, copyBuffer, argbDestination);
        for (int i = limitBits - 1; i != 0; --i)
        {
            NearLosslessPass(xsize, ysize, argbDestination, xsize, i, copyBuffer, argbDestination);
        }
    }
}
