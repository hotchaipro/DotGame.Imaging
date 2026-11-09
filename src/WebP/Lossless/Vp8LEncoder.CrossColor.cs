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
    private static long PredictionCostCrossColor(
        ReadOnlySpan<uint> accumulated,
        ReadOnlySpan<uint> counts)
    {
        const ulong ExpValue = 240;
        return (long)CombinedShannonEntropy(counts, accumulated) + PredictionCostBias(counts, 3, ExpValue);
    }

    private static long GetPredictionCostCrossColorRed(
        ReadOnlySpan<uint> argb,
        int stride,
        int tileWidth,
        int tileHeight,
        Multipliers previousX,
        Multipliers previousY,
        int greenToRed,
        ReadOnlySpan<uint> accumulatedRedHisto)
    {
        Span<uint> histogram = stackalloc uint[256];
        histogram.Clear();
        CollectColorRedTransforms(argb, stride, tileWidth, tileHeight, greenToRed, histogram);
        long currentDiff = PredictionCostCrossColor(accumulatedRedHisto, histogram);
        if ((byte)greenToRed == previousX.GreenToRed)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        if ((byte)greenToRed == previousY.GreenToRed)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        if (greenToRed == 0)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        return currentDiff;
    }

    private static void GetBestGreenToRed(
        ReadOnlySpan<uint> argb,
        int stride,
        int tileWidth,
        int tileHeight,
        Multipliers previousX,
        Multipliers previousY,
        int quality,
        ReadOnlySpan<uint> accumulatedRedHisto,
        ref Multipliers bestTx)
    {
        int maxIters = 4 + ((7 * quality) >> 8);
        int greenToRedBest = 0;
        long bestDiff = GetPredictionCostCrossColorRed(argb, stride, tileWidth, tileHeight, previousX, previousY, greenToRedBest, accumulatedRedHisto);
        for (int iter = 0; iter < maxIters; iter++)
        {
            int delta = 32 >> iter;
            for (int offset = -delta; offset <= delta; offset += 2 * delta)
            {
                int greenToRedCurrent = offset + greenToRedBest;
                long currentDiff = GetPredictionCostCrossColorRed(argb, stride, tileWidth, tileHeight, previousX, previousY, greenToRedCurrent, accumulatedRedHisto);
                if (currentDiff < bestDiff)
                {
                    bestDiff = currentDiff;
                    greenToRedBest = greenToRedCurrent;
                }
            }
        }

        bestTx.GreenToRed = (byte)(greenToRedBest & 0xff);
    }

    private static long GetPredictionCostCrossColorBlue(
        ReadOnlySpan<uint> argb,
        int stride,
        int tileWidth,
        int tileHeight,
        Multipliers previousX,
        Multipliers previousY,
        int greenToBlue,
        int redToBlue,
        ReadOnlySpan<uint> accumulatedBlueHisto)
    {
        Span<uint> histogram = stackalloc uint[256];
        histogram.Clear();
        CollectColorBlueTransforms(argb, stride, tileWidth, tileHeight, greenToBlue, redToBlue, histogram);
        long currentDiff = PredictionCostCrossColor(accumulatedBlueHisto, histogram);
        if ((byte)greenToBlue == previousX.GreenToBlue)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        if ((byte)greenToBlue == previousY.GreenToBlue)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        if ((byte)redToBlue == previousX.RedToBlue)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        if ((byte)redToBlue == previousY.RedToBlue)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        if (greenToBlue == 0)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        if (redToBlue == 0)
        {
            currentDiff -= 3L << Log2PrecisionBits;
        }

        return currentDiff;
    }

    private static void GetBestGreenRedToBlue(
        ReadOnlySpan<uint> argb,
        int stride,
        int tileWidth,
        int tileHeight,
        Multipliers previousX,
        Multipliers previousY,
        int quality,
        ReadOnlySpan<uint> accumulatedBlueHisto,
        ref Multipliers bestTx)
    {
        const int NumAxis = 8;
        ReadOnlySpan<sbyte> offsets = [0, -1, 0, 1, -1, 0, 1, 0, -1, -1, -1, 1, 1, -1, 1, 1];
        ReadOnlySpan<sbyte> deltaLut = [16, 16, 8, 4, 2, 2, 2];
        int iters = (quality < 25) ? 1 : (quality > 50) ? 7 : 4;
        int greenToBlueBest = 0;
        int redToBlueBest = 0;
        long bestDiff = GetPredictionCostCrossColorBlue(argb, stride, tileWidth, tileHeight, previousX, previousY, greenToBlueBest, redToBlueBest, accumulatedBlueHisto);
        for (int iter = 0; iter < iters; iter++)
        {
            int delta = deltaLut[iter];
            for (int axis = 0; axis < NumAxis; axis++)
            {
                int greenToBlueCurrent = (offsets[axis * 2] * delta) + greenToBlueBest;
                int redToBlueCurrent = (offsets[(axis * 2) + 1] * delta) + redToBlueBest;
                long currentDiff = GetPredictionCostCrossColorBlue(argb, stride, tileWidth, tileHeight, previousX, previousY, greenToBlueCurrent, redToBlueCurrent, accumulatedBlueHisto);
                if (currentDiff < bestDiff)
                {
                    bestDiff = currentDiff;
                    greenToBlueBest = greenToBlueCurrent;
                    redToBlueBest = redToBlueCurrent;
                }

                if ((quality < 25) && (iter == 4))
                {
                    break;
                }
            }

            if ((delta == 2) && (greenToBlueBest == 0) && (redToBlueBest == 0))
            {
                break;
            }
        }

        bestTx.GreenToBlue = (byte)(greenToBlueBest & 0xff);
        bestTx.RedToBlue = (byte)(redToBlueBest & 0xff);
    }

    // VP8LColorSpaceTransform: returns the color transform image's bits
    private static int ColorSpaceTransform(
        int width,
        int height,
        int bits,
        int quality,
        Span<uint> argb,
        Span<uint> image)
    {
        int maxTileSize = 1 << bits;
        int tileXsize = SubSampleSize(width, bits);
        int tileYsize = SubSampleSize(height, bits);
        Span<uint> accumulatedRedHisto = stackalloc uint[256];
        Span<uint> accumulatedBlueHisto = stackalloc uint[256];
        accumulatedRedHisto.Clear();
        accumulatedBlueHisto.Clear();
        var previousX = default(Multipliers);
        var previousY = default(Multipliers);
        for (int tileY = 0; tileY < tileYsize; tileY++)
        {
            for (int tileX = 0; tileX < tileXsize; tileX++)
            {
                int tileXOffset = tileX * maxTileSize;
                int tileYOffset = tileY * maxTileSize;
                int allXMax = Math.Min(tileXOffset + maxTileSize, width);
                int allYMax = Math.Min(tileYOffset + maxTileSize, height);
                int offset = (tileY * tileXsize) + tileX;
                if (tileY != 0)
                {
                    uint code = image[offset - tileXsize];
                    previousY.GreenToRed = (byte)code;
                    previousY.GreenToBlue = (byte)(code >> 8);
                    previousY.RedToBlue = (byte)(code >> 16);
                }

                // GetBestColorTransformForTile
                int tileWidth = allXMax - tileXOffset;
                int tileHeight = allYMax - tileYOffset;
                var tileArgb = argb.Slice((tileYOffset * width) + tileXOffset);
                var bestTx = default(Multipliers);
                GetBestGreenToRed(tileArgb, width, tileWidth, tileHeight, previousX, previousY, quality, accumulatedRedHisto, ref bestTx);
                GetBestGreenRedToBlue(tileArgb, width, tileWidth, tileHeight, previousX, previousY, quality, accumulatedBlueHisto, ref bestTx);
                previousX = bestTx;
                image[offset] = 0xff000000u | ((uint)previousX.RedToBlue << 16) | ((uint)previousX.GreenToBlue << 8) | previousX.GreenToRed;

                // CopyTileWithColorTransform
                for (int y = tileYOffset; y < allYMax; y++)
                {
                    TransformColor(previousX, argb.Slice((y * width) + tileXOffset, allXMax - tileXOffset));
                }

                // The accumulated histograms, without the pixels backward references will take
                for (int y = tileYOffset; y < allYMax; y++)
                {
                    int ix = (y * width) + tileXOffset;
                    int ixEnd = ix + allXMax - tileXOffset;
                    for (; ix < ixEnd; ix++)
                    {
                        uint pix = argb[ix];
                        if ((ix >= 2) && (pix == argb[ix - 2]) && (pix == argb[ix - 1]))
                        {
                            continue;
                        }

                        if ((ix >= width + 2) && (argb[ix - 2] == argb[ix - width - 2]) && (argb[ix - 1] == argb[ix - width - 1]) && (pix == argb[ix - width]))
                        {
                            continue;
                        }

                        accumulatedRedHisto[(int)((pix >> 16) & 0xff)]++;
                        accumulatedBlueHisto[(int)(pix & 0xff)]++;
                    }
                }
            }
        }

        return OptimizeSampling(image, width, height, bits, MaxTransformBits);
    }

    //------------------------------------------------------------------------------
    // Near lossless preprocessing (near_lossless_enc.c)
}
