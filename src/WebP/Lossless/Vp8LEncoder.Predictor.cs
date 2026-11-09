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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private const int HistoSize = 4 * 256;
    private const long SpatialPredictorBias = 15L << Log2PrecisionBits;
    private const int PredLowEffort = 11;
    private const uint MaskAlpha = 0xff000000;
    private const int NumPredModes = 14;
    private const int MinTransformBits = 2;
    private const int NumTransformBits = 3;
    private const int MaxTransformBits = MinTransformBits + (1 << NumTransformBits) - 1;

    // A bias toward residuals near 0 ('expValue' is in hundredths)
    private static long PredictionCostBias(
        ReadOnlySpan<uint> counts,
        ulong weight0,
        ulong expValue)
    {
        const int SignificantSymbols = 256 >> 4;
        const ulong ExpDecayFactor = 6;
        ulong bits = (weight0 * counts[0]) << Log2PrecisionBits;
        expValue <<= Log2PrecisionBits;
        for (int i = 1; i < SignificantSymbols; i++)
        {
            bits += (ulong)DivRound((long)(expValue * (counts[i] + counts[256 - i])), 100);
            expValue = (ulong)DivRound((long)(ExpDecayFactor * expValue), 10);
        }

        return -DivRound((long)bits, 10);
    }

    private static long PredictionCostSpatialHistogram(
        ReadOnlySpan<uint> accumulated,
        ReadOnlySpan<uint> tile,
        int mode,
        int leftMode,
        int aboveMode)
    {
        long result = 0;
        for (int i = 0; i < 4; i++)
        {
            const ulong ExpValue = 94;
            result += PredictionCostBias(tile.Slice(i * 256, 256), 1, ExpValue);
            result += (long)CombinedShannonEntropy(tile.Slice(i * 256, 256), accumulated.Slice(i * 256, 256));
        }

        if (mode == leftMode)
        {
            result -= SpatialPredictorBias;
        }

        if (mode == aboveMode)
        {
            result -= SpatialPredictorBias;
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateHisto(
        Span<uint> histoArgb,
        uint argb)
    {
        histoArgb[(int)(argb >> 24)]++;
        histoArgb[256 + (int)((argb >> 16) & 0xff)]++;
        histoArgb[512 + (int)((argb >> 8) & 0xff)]++;
        histoArgb[768 + (int)(argb & 0xff)]++;
    }

    // PredictBatch: the residuals of current[xStart..xStart+count) into output
    private static void PredictBatch(
        int mode,
        int xStart,
        int y,
        int numPixels,
        ReadOnlySpan<uint> current,
        ReadOnlySpan<uint> upper,
        Span<uint> output)
    {
        int o = 0;
        if (xStart == 0)
        {
            output[0] = (y == 0) ? SubPixels(current[0], ArgbBlack) : SubPixels(current[0], upper[0]);
            ++xStart;
            ++o;
            --numPixels;
        }

        if (y == 0)
        {
            PredictorSub(1, current, upper, xStart, numPixels, output.Slice(o));
        }
        else
        {
            PredictorSub(mode, current, upper, xStart, numPixels, output.Slice(o));
        }
    }

    private static int MaxDiffBetweenPixels(
        uint p1,
        uint p2)
    {
        int diffA = Math.Abs((int)(p1 >> 24) - (int)(p2 >> 24));
        int diffR = Math.Abs((int)((p1 >> 16) & 0xff) - (int)((p2 >> 16) & 0xff));
        int diffG = Math.Abs((int)((p1 >> 8) & 0xff) - (int)((p2 >> 8) & 0xff));
        int diffB = Math.Abs((int)(p1 & 0xff) - (int)(p2 & 0xff));
        return Math.Max(Math.Max(diffA, diffR), Math.Max(diffG, diffB));
    }

    private static int MaxDiffAroundPixel(
        uint current,
        uint up,
        uint down,
        uint left,
        uint right)
    {
        int diffUp = MaxDiffBetweenPixels(current, up);
        int diffDown = MaxDiffBetweenPixels(current, down);
        int diffLeft = MaxDiffBetweenPixels(current, left);
        int diffRight = MaxDiffBetweenPixels(current, right);
        return Math.Max(Math.Max(diffUp, diffDown), Math.Max(diffLeft, diffRight));
    }

    private static uint AddGreenToBlueAndRed(
        uint argb)
    {
        uint green = (argb >> 8) & 0xff;
        uint redBlue = argb & 0x00ff00ffu;
        redBlue += (green << 16) | green;
        redBlue &= 0x00ff00ffu;
        return (argb & 0xff00ff00u) | redBlue;
    }

    // MaxDiffsForRow: argb is the row's first pixel at argbStart, rows stride apart
    private static void MaxDiffsForRow(
        int width,
        int stride,
        ReadOnlySpan<uint> argb,
        int argbStart,
        Span<byte> maxDiffs,
        bool usedSubtractGreen)
    {
        if (width <= 2)
        {
            return;
        }

        uint current = argb[argbStart];
        uint right = argb[argbStart + 1];
        if (usedSubtractGreen)
        {
            current = AddGreenToBlueAndRed(current);
            right = AddGreenToBlueAndRed(right);
        }

        for (int x = 1; x < width - 1; x++)
        {
            uint up = argb[argbStart - stride + x];
            uint down = argb[argbStart + stride + x];
            uint left = current;
            current = right;
            right = argb[argbStart + x + 1];
            if (usedSubtractGreen)
            {
                up = AddGreenToBlueAndRed(up);
                down = AddGreenToBlueAndRed(down);
                right = AddGreenToBlueAndRed(right);
            }

            maxDiffs[x] = (byte)MaxDiffAroundPixel(current, up, down, left, right);
        }
    }

    private static byte NearLosslessComponent(
        byte value,
        byte predict,
        byte boundary,
        int quantization)
    {
        int residual = (value - predict) & 0xff;
        int boundaryResidual = (boundary - predict) & 0xff;
        int lower = residual & ~(quantization - 1);
        int upper = lower + quantization;
        int bias = (((boundary - value) & 0xff) < boundaryResidual) ? 1 : 0;
        if (residual - lower < upper - residual + bias)
        {
            if ((residual > boundaryResidual) && (lower <= boundaryResidual))
            {
                return (byte)(lower + (quantization >> 1));
            }

            return (byte)lower;
        }
        else
        {
            if ((residual <= boundaryResidual) && (upper > boundaryResidual))
            {
                return (byte)(lower + (quantization >> 1));
            }

            return (byte)(upper & 0xff);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte NearLosslessDiff(
        byte a,
        byte b)
    {
        return (byte)((a - b) & 0xff);
    }

    private static uint NearLossless(
        uint value,
        uint predict,
        int maxQuantization,
        int maxDiff,
        bool usedSubtractGreen)
    {
        byte newGreen = 0;
        byte greenDiff = 0;
        if (maxDiff <= 2)
        {
            return SubPixels(value, predict);
        }

        int quantization = maxQuantization;
        while (quantization >= maxDiff)
        {
            quantization >>= 1;
        }

        byte a;
        if (((value >> 24) == 0) || ((value >> 24) == 0xff))
        {
            a = NearLosslessDiff((byte)(value >> 24), (byte)(predict >> 24));
        }
        else
        {
            a = NearLosslessComponent((byte)(value >> 24), (byte)(predict >> 24), 0xff, quantization);
        }

        byte g = NearLosslessComponent((byte)(value >> 8), (byte)(predict >> 8), 0xff, quantization);
        if (usedSubtractGreen)
        {
            newGreen = (byte)(((predict >> 8) + g) & 0xff);
            greenDiff = NearLosslessDiff(newGreen, (byte)(value >> 8));
        }

        byte r = NearLosslessComponent(NearLosslessDiff((byte)(value >> 16), greenDiff), (byte)(predict >> 16), (byte)(0xff - newGreen), quantization);
        byte b = NearLosslessComponent(NearLosslessDiff((byte)value, greenDiff), (byte)predict, (byte)(0xff - newGreen), quantization);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    // GetResidual: the residuals of current[xStart..xEnd) into output; without exact, transparent
    // pixels' colors and near lossless change current (and upper's last pixel)
    private static void GetResidual(
        int width,
        int height,
        Span<uint> upper,
        Span<uint> current,
        ReadOnlySpan<byte> maxDiffs,
        int mode,
        int xStart,
        int xEnd,
        int y,
        int maxQuantization,
        bool exact,
        bool usedSubtractGreen,
        Span<uint> output)
    {
        if (exact)
        {
            PredictBatch(mode, xStart, y, xEnd - xStart, current, upper, output);
            return;
        }

        for (int x = xStart; x < xEnd; x++)
        {
            uint predict;
            uint residual;
            if (y == 0)
            {
                predict = (x == 0) ? ArgbBlack : current[x - 1];
            }
            else if (x == 0)
            {
                predict = upper[x];
            }
            else
            {
                predict = Predict(mode, current[x - 1], upper, x);
            }

            if ((maxQuantization == 1) || (mode == 0) || (y == 0) || (y == height - 1) || (x == 0) || (x == width - 1))
            {
                residual = SubPixels(current[x], predict);
            }
            else
            {
                residual = NearLossless(current[x], predict, maxQuantization, maxDiffs[x], usedSubtractGreen);
                current[x] = AddPixels(predict, residual);
            }

            if ((current[x] & MaskAlpha) == 0)
            {
                // Transparent: the residual's color is 0, and so the pixel's color is its prediction's
                residual &= MaskAlpha;
                current[x] = predict & ~MaskAlpha;
                if ((x == 0) && (y != 0))
                {
                    upper[width] = current[0];
                }
            }

            output[x - xStart] = residual;
        }
    }

    // The residuals of each predictor over a tile, into the histograms (ComputeResidualsForTile)
    private static void ComputeResidualsForTile(
        int width,
        int height,
        int tileX,
        int tileY,
        int minBits,
        int updateUpToIndex,
        Span<uint> allArgb,
        Span<uint> scratch,
        ReadOnlySpan<uint> argb,
        int maxQuantization,
        bool exact,
        bool usedSubtractGreen)
    {
        int startX = tileX << minBits;
        int startY = tileY << minBits;
        int tileSize = 1 << minBits;
        int maxY = Math.Min(tileSize, height - startY);
        int maxX = Math.Min(tileSize, width - startX);
        int haveLeft = (startX > 0) ? 1 : 0;
        int contextStartX = startX - haveLeft;
        int contextWidth = maxX + haveLeft + ((maxX < width - startX) ? 1 : 0);

        // Rows one pixel wider than the image, so the last pixel's top right is the next row's first
        var upperRow = scratch.Slice(0, width + 1);
        var currentRow = scratch.Slice(width + 1, width + 1);
        var maxDiffs = MemoryMarshal.AsBytes(scratch.Slice(2 * (width + 1)));
        Span<uint> residuals = stackalloc uint[1 << MaxTransformBits];
        for (int mode = 0; mode < NumPredModes; mode++)
        {
            var histoArgb = allArgb.Slice(mode * HistoSize, HistoSize);
            if (startY > 0)
            {
                // The row above the tile, which becomes the first upper row
                argb.Slice(((startY - 1) * width) + contextStartX, maxX + haveLeft + 1).CopyTo(currentRow.Slice(contextStartX));
            }

            for (int relativeY = 0; relativeY < maxY; relativeY++)
            {
                int y = startY + relativeY;
                var tmp = upperRow;
                upperRow = currentRow;
                currentRow = tmp;
                argb.Slice((y * width) + contextStartX, maxX + haveLeft + ((y + 1 < height) ? 1 : 0)).CopyTo(currentRow.Slice(contextStartX));
                if ((maxQuantization > 1) && (y >= 1) && (y + 1 < height))
                {
                    MaxDiffsForRow(contextWidth, width, argb, (y * width) + contextStartX, maxDiffs.Slice(contextStartX), usedSubtractGreen);
                }

                GetResidual(width, height, upperRow, currentRow, maxDiffs, mode, startX, startX + maxX, y, maxQuantization, exact, usedSubtractGreen, residuals);
                for (int relativeX = 0; relativeX < maxX; relativeX++)
                {
                    UpdateHisto(histoArgb, residuals[relativeX]);
                }

                for (int subsamplingIndex = 1; subsamplingIndex <= updateUpToIndex; subsamplingIndex++)
                {
                    var superHisto = allArgb.Slice(((subsamplingIndex * NumPredModes) + mode) * HistoSize, HistoSize);
                    for (int relativeX = 0; relativeX < maxX; relativeX++)
                    {
                        UpdateHisto(superHisto, residuals[relativeX]);
                    }
                }
            }
        }
    }

    // The image as residuals of its tiles' predictors (CopyImageWithPrediction)
    private static void CopyImageWithPrediction(
        int width,
        int height,
        int bits,
        ReadOnlySpan<uint> modes,
        Span<uint> scratch,
        Span<uint> argb,
        bool lowEffort,
        int maxQuantization,
        bool exact,
        bool usedSubtractGreen)
    {
        int tilesPerRow = SubSampleSize(width, bits);
        var upperRow = scratch.Slice(0, width + 1);
        var currentRow = scratch.Slice(width + 1, width + 1);
        var diffs = MemoryMarshal.AsBytes(scratch.Slice(2 * (width + 1)));
        var currentMaxDiffs = diffs.Slice(0, width);
        var lowerMaxDiffs = diffs.Slice(width, width);
        for (int y = 0; y < height; y++)
        {
            var tmp32 = upperRow;
            upperRow = currentRow;
            currentRow = tmp32;
            argb.Slice(y * width, width + ((y + 1 < height) ? 1 : 0)).CopyTo(currentRow);
            if (lowEffort)
            {
                PredictBatch(PredLowEffort, 0, y, width, currentRow, upperRow, argb.Slice(y * width));
            }
            else
            {
                if (maxQuantization > 1)
                {
                    // The lower row's max diffs, while argb still holds the current row's pixels
                    var tmp8 = currentMaxDiffs;
                    currentMaxDiffs = lowerMaxDiffs;
                    lowerMaxDiffs = tmp8;
                    if (y + 2 < height)
                    {
                        MaxDiffsForRow(width, width, argb, (y + 1) * width, lowerMaxDiffs, usedSubtractGreen);
                    }
                }

                for (int x = 0; x < width;)
                {
                    int mode = (int)((modes[((y >> bits) * tilesPerRow) + (x >> bits)] >> 8) & 0xff);
                    int xEnd = x + (1 << bits);
                    if (xEnd > width)
                    {
                        xEnd = width;
                    }

                    GetResidual(width, height, upperRow, currentRow, currentMaxDiffs, mode, x, xEnd, y, maxQuantization, exact, usedSubtractGreen, argb.Slice((y * width) + x));
                    x = xEnd;
                }
            }
        }
    }
}
