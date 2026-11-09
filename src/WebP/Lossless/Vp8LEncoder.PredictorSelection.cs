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
    // VP8LOptimizeSampling: the largest squares of one value the image is made of
    private static int OptimizeSampling(
        Span<uint> image,
        int fullWidth,
        int fullHeight,
        int bits,
        int maxBits)
    {
        int width = SubSampleSize(fullWidth, bits);
        int height = SubSampleSize(fullHeight, bits);
        int bestBits = bits;
        int squareSize;

        // Rows first
        while (bestBits < maxBits)
        {
            int newSquareSize = 1 << (bestBits + 1 - bits);
            bool isGood = true;
            squareSize = 1 << (bestBits - bits);
            for (int y = 0; y + squareSize < height; y += newSquareSize)
            {
                if (!image.Slice(y * width, width).SequenceEqual(image.Slice((y + squareSize) * width, width)))
                {
                    isGood = false;
                    break;
                }
            }

            if (isGood)
            {
                ++bestBits;
            }
            else
            {
                break;
            }
        }

        if (bestBits == bits)
        {
            return bits;
        }

        // Then columns
        while (bestBits > bits)
        {
            bool isGood = true;
            squareSize = 1 << (bestBits - bits);
            for (int y = 0; isGood && (y < height); y++)
            {
                for (int x = 0; isGood && (x < width); x += squareSize)
                {
                    for (int i = x + 1; i < Math.Min(x + squareSize, width); i++)
                    {
                        if (image[(y * width) + i] != image[(y * width) + x])
                        {
                            isGood = false;
                            break;
                        }
                    }
                }
            }

            if (isGood)
            {
                break;
            }

            --bestBits;
        }

        if (bestBits == bits)
        {
            return bits;
        }

        int oldWidth = width;
        squareSize = 1 << (bestBits - bits);
        width = SubSampleSize(fullWidth, bestBits);
        height = SubSampleSize(fullHeight, bestBits);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[(y * width) + x] = image[squareSize * ((y * oldWidth) + x)];
            }
        }

        return bestBits;
    }

    // The best predictor of each tile, and the best sampling (GetBestPredictorsAndSubSampling)
    private static int GetBestPredictorsAndSubSampling(
        int width,
        int height,
        int minBits,
        int maxBits,
        Span<uint> scratch,
        ReadOnlySpan<uint> argb,
        int maxQuantization,
        bool exact,
        bool usedSubtractGreen,
        uint[][] allModes,
        out uint[] bestMode)
    {
        uint tilesPerRow = (uint)SubSampleSize(width, minBits);
        uint tilesPerCol = (uint)SubSampleSize(height, minBits);
        uint maxSubsamplingIndex = (uint)(maxBits - minBits);
        int numArgb = (int)(maxSubsamplingIndex + 1) * NumPredModes * HistoSize;
        var allArgb = new uint[numArgb];
        var allAccumulatedArgb = new uint[(maxSubsamplingIndex + 1) * HistoSize];
        var allPredHistos = new uint[(maxSubsamplingIndex + 1) * NumPredModes];
        uint maxTileSize = 1u << (int)maxSubsamplingIndex;
        uint updateUpToIndex = (uint)(Math.Max(Math.Min(4, maxBits), minBits) - minBits);
        uint localTileX = 0, localTileY = 0;
        uint maxTileX = 0, maxTileY = 0;
        uint tileX = 0, tileY = 0;

        while (tileY < tilesPerCol)
        {
            ComputeResidualsForTile(width, height, (int)tileX, (int)tileY, minBits, (int)updateUpToIndex, allArgb, scratch, argb, maxQuantization, exact, usedSubtractGreen);

            // The super-tiles that are complete
            uint subsamplingIndex = 0;
            while (true)
            {
                uint superTileX = tileX >> (int)subsamplingIndex;
                uint superTileY = tileY >> (int)subsamplingIndex;
                uint superTilesPerRow = (uint)SubSampleSize(width, minBits + (int)subsamplingIndex);
                GetBestPredictorForTile(allArgb, (int)subsamplingIndex, (int)superTileX, (int)superTileY, (int)superTilesPerRow, allAccumulatedArgb, allModes, allPredHistos);
                if (subsamplingIndex == maxSubsamplingIndex)
                {
                    break;
                }

                ++subsamplingIndex;
                if ((subsamplingIndex > updateUpToIndex) && (subsamplingIndex <= maxSubsamplingIndex))
                {
                    AddVectorEq(
                        allArgb.AsSpan((int)(subsamplingIndex - 1) * NumPredModes * HistoSize, HistoSize * NumPredModes),
                        allArgb.AsSpan((int)subsamplingIndex * NumPredModes * HistoSize, HistoSize * NumPredModes));
                }

                if (!(((tileX == tilesPerRow - 1) || ((localTileX + 1) % (1u << (int)subsamplingIndex) == 0)) &&
                      ((tileY == tilesPerCol - 1) || ((localTileY + 1) % (1u << (int)subsamplingIndex) == 0))))
                {
                    --subsamplingIndex;
                    break;
                }
            }

            // The finished tiles' histograms back to zero
            allArgb.AsSpan(0, HistoSize * NumPredModes * (int)(subsamplingIndex + 1)).Clear();
            if (subsamplingIndex == maxSubsamplingIndex)
            {
                // A new max-tile
                if (tileX == tilesPerRow - 1)
                {
                    maxTileX = 0;
                    ++maxTileY;
                }
                else
                {
                    ++maxTileX;
                }

                localTileX = 0;
                localTileY = 0;
            }
            else
            {
                // On with the Z order
                uint coordX = localTileX >> (int)subsamplingIndex;
                uint coordY = localTileY >> (int)subsamplingIndex;
                if ((tileX == tilesPerRow - 1) && (coordX % 2 == 0))
                {
                    ++coordY;
                }
                else
                {
                    if (coordX % 2 == 0)
                    {
                        ++coordX;
                    }
                    else
                    {
                        ++coordY;
                        --coordX;
                    }
                }

                localTileX = coordX << (int)subsamplingIndex;
                localTileY = coordY << (int)subsamplingIndex;
            }

            tileX = (maxTileX * maxTileSize) + localTileX;
            tileY = (maxTileY * maxTileSize) + localTileY;
        }

        // The best sampling
        long bestCost = long.MaxValue;
        int bestBits = 0;
        bestMode = allModes[0];
        for (uint subsamplingIndex = 0; subsamplingIndex <= maxSubsamplingIndex; subsamplingIndex++)
        {
            var accumulated = allAccumulatedArgb.AsSpan((int)subsamplingIndex * HistoSize, HistoSize);
            long cost = (long)ShannonEntropy(allPredHistos.AsSpan((int)subsamplingIndex * NumPredModes, NumPredModes));
            for (int plane = 0; plane < 4; plane++)
            {
                cost += (long)ShannonEntropy(accumulated.Slice(plane * 256, 256));
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestBits = minBits + (int)subsamplingIndex;
                bestMode = allModes[subsamplingIndex];
            }
        }

        return OptimizeSampling(bestMode, width, height, bestBits, MaxTransformBits);
    }

    private static void GetBestPredictorForTile(
        ReadOnlySpan<uint> allArgb,
        int subsamplingIndex,
        int tileX,
        int tileY,
        int tilesPerRow,
        Span<uint> allAccumulatedArgb,
        uint[][] allModes,
        Span<uint> allPredHistos)
    {
        var accumulatedArgb = allAccumulatedArgb.Slice(subsamplingIndex * HistoSize, HistoSize);
        var modes = allModes[subsamplingIndex];
        var predHistos = allPredHistos.Slice(subsamplingIndex * NumPredModes);
        int leftMode = (tileX > 0) ? (int)((modes[(tileY * tilesPerRow) + tileX - 1] >> 8) & 0xff) : 0xff;
        int aboveMode = (tileY > 0) ? (int)((modes[((tileY - 1) * tilesPerRow) + tileX] >> 8) & 0xff) : 0xff;
        long bestDiff = long.MaxValue;
        int bestModeIndex = 0;
        var bestHisto = allArgb.Slice(0, HistoSize);
        for (int mode = 0; mode < NumPredModes; mode++)
        {
            var histoArgb = allArgb.Slice(((subsamplingIndex * NumPredModes) + mode) * HistoSize, HistoSize);
            long currentDiff = PredictionCostSpatialHistogram(accumulatedArgb, histoArgb, mode, leftMode, aboveMode);
            if (currentDiff < bestDiff)
            {
                bestHisto = histoArgb;
                bestDiff = currentDiff;
                bestModeIndex = mode;
            }
        }

        AddVectorEq(bestHisto, accumulatedArgb);
        modes[(tileY * tilesPerRow) + tileX] = ArgbBlack | ((uint)bestModeIndex << 8);
        predHistos[bestModeIndex]++;
    }

    // VP8LResidualImage: the best predictor image into 'image', and argb as residuals; returns the
    // predictor image's bits
    private static int ResidualImage(
        int width,
        int height,
        int minBits,
        int maxBits,
        bool lowEffort,
        Span<uint> argb,
        Span<uint> scratch,
        Span<uint> image,
        int nearLosslessQuality,
        bool exact,
        bool usedSubtractGreen)
    {
        int maxQuantization = 1 << NearLosslessBits(nearLosslessQuality);
        int bestBits;
        if (lowEffort)
        {
            int tilesPerRow = SubSampleSize(width, maxBits);
            int tilesPerCol = SubSampleSize(height, maxBits);
            image.Slice(0, tilesPerRow * tilesPerCol).Fill(ArgbBlack | (PredLowEffort << 8));
            bestBits = maxBits;
        }
        else
        {
            var modes = new uint[maxBits - minBits + 1][];
            for (int bits = minBits; bits <= maxBits; bits++)
            {
                modes[bits - minBits] = new uint[SubSampleSize(width, bits) * SubSampleSize(height, bits)];
            }

            bestBits = GetBestPredictorsAndSubSampling(width, height, minBits, maxBits, scratch, argb, maxQuantization, exact, usedSubtractGreen, modes, out uint[] bestMode);
            bestMode.AsSpan(0, SubSampleSize(width, bestBits) * SubSampleSize(height, bestBits)).CopyTo(image);
        }

        CopyImageWithPrediction(width, height, bestBits, image, scratch, argb, lowEffort, maxQuantization, exact, usedSubtractGreen);
        return bestBits;
    }

    //------------------------------------------------------------------------------
    // Color transform
}
