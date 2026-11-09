// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/histogram_enc.c), modified by Hot Chai Games LLC: written in C#.
// libwebp carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private static void HistogramBuild(
        int xsize,
        int histoBits,
        BackwardRefs backwardRefs,
        HistogramSet imageHisto)
    {
        int x = 0, y = 0;
        int histoXsize = SubSampleSize(xsize, histoBits);
        var histograms = imageHisto.Histograms;
        imageHisto.Clear();
        foreach (ref readonly var v in backwardRefs.Span)
        {
            int ix = ((y >> histoBits) * histoXsize) + (x >> histoBits);
            HistogramAddSinglePixOrCopy(histograms[ix], v, 0);
            x += v.Length;
            while (x >= xsize)
            {
                x -= xsize;
                ++y;
            }
        }
    }

    private static void HistogramCopyAndAnalyze(
        HistogramSet origHisto,
        Histogram?[] origHistograms,
        HistogramSet imageHisto)
    {
        var histograms = imageHisto.Histograms;
        imageHisto.Size = 0;
        for (int i = 0; i < origHisto.MaxSize; i++)
        {
            var histo = origHisto.Histograms[i];
            ComputeHistogramCost(histo);
            if (!histo.IsUsed[LiteralIndex] && !histo.IsUsed[RedIndex] && !histo.IsUsed[BlueIndex] && !histo.IsUsed[AlphaIndex] && !histo.IsUsed[DistanceIndex])
            {
                origHistograms[i] = null;
            }
            else
            {
                origHistograms[i] = histo;
                histo.CopyTo(histograms[imageHisto.Size]);
                ++imageHisto.Size;
            }
        }
    }

    private static void HistogramAnalyzeEntropyBin(
        HistogramSet imageHisto,
        bool lowEffort)
    {
        var histograms = imageHisto.Histograms;
        int histoSize = imageHisto.Size;
        ulong literalMax = 0, literalMin = ulong.MaxValue;
        ulong redMax = 0, redMin = ulong.MaxValue;
        ulong blueMax = 0, blueMin = ulong.MaxValue;
        for (int i = 0; i < histoSize; i++)
        {
            var h = histograms[i];
            literalMax = Math.Max(literalMax, h.Costs[LiteralIndex]);
            literalMin = Math.Min(literalMin, h.Costs[LiteralIndex]);
            redMax = Math.Max(redMax, h.Costs[RedIndex]);
            redMin = Math.Min(redMin, h.Costs[RedIndex]);
            blueMax = Math.Max(blueMax, h.Costs[BlueIndex]);
            blueMin = Math.Min(blueMin, h.Costs[BlueIndex]);
        }

        for (int i = 0; i < histoSize; i++)
        {
            var h = histograms[i];
            int binId = GetBinIdForEntropy(literalMin, literalMax, h.Costs[LiteralIndex]);
            if (!lowEffort)
            {
                binId = (binId * NumPartitions) + GetBinIdForEntropy(redMin, redMax, h.Costs[RedIndex]);
                binId = (binId * NumPartitions) + GetBinIdForEntropy(blueMin, blueMax, h.Costs[BlueIndex]);
            }

            h.BinId = (ushort)binId;
        }
    }

    private static void HistogramCombineEntropyBin(
        HistogramSet imageHisto,
        Histogram currentCombo,
        int numBins,
        int combineCostFactor,
        bool lowEffort)
    {
        var histograms = imageHisto.Histograms;
        Span<short> binFirst = stackalloc short[BinSize];
        Span<ushort> binFailures = stackalloc ushort[BinSize];
        for (int i = 0; i < numBins; i++)
        {
            binFirst[i] = -1;
            binFailures[i] = 0;
        }

        for (int idx = 0; idx < imageHisto.Size;)
        {
            int binId = histograms[idx].BinId;
            int first = binFirst[binId];
            if (first == -1)
            {
                binFirst[binId] = (short)idx;
                ++idx;
            }
            else if (lowEffort)
            {
                HistogramAdd(histograms[idx], histograms[first], histograms[first]);
                imageHisto.RemoveHistogram(idx);
            }
            else
            {
                // Merge idx into first, which share the bin
                ulong bitCost = histograms[idx].BitCost;
                long bitCostThreshold = -DivRound((long)bitCost * combineCostFactor, 100);
                if (HistogramAddEval(histograms[first], histograms[idx], currentCombo, bitCostThreshold))
                {
                    const int MaxCombineFailures = 32;
                    bool tryCombine =
                        (currentCombo.TrivialSymbol[RedIndex] != NonTrivialSymbol) &&
                        (currentCombo.TrivialSymbol[BlueIndex] != NonTrivialSymbol) &&
                        (currentCombo.TrivialSymbol[AlphaIndex] != NonTrivialSymbol);
                    if (!tryCombine)
                    {
                        tryCombine =
                            (histograms[idx].TrivialSymbol[RedIndex] == NonTrivialSymbol) ||
                            (histograms[idx].TrivialSymbol[BlueIndex] == NonTrivialSymbol) ||
                            (histograms[idx].TrivialSymbol[AlphaIndex] == NonTrivialSymbol);
                        tryCombine &=
                            (histograms[first].TrivialSymbol[RedIndex] == NonTrivialSymbol) ||
                            (histograms[first].TrivialSymbol[BlueIndex] == NonTrivialSymbol) ||
                            (histograms[first].TrivialSymbol[AlphaIndex] == NonTrivialSymbol);
                    }

                    if (tryCombine || (binFailures[binId] >= MaxCombineFailures))
                    {
                        // The merged histogram to its final slot
                        (currentCombo, histograms[first]) = (histograms[first], currentCombo);
                        imageHisto.RemoveHistogram(idx);
                    }
                    else
                    {
                        ++binFailures[binId];
                        ++idx;
                    }
                }
                else
                {
                    ++idx;
                }
            }
        }

        if (lowEffort)
        {
            for (int idx = 0; idx < imageHisto.Size; idx++)
            {
                ComputeHistogramCost(histograms[idx]);
            }
        }
    }

    // Lehmer's random number generator, 48271 modulo 2^31 - 1
    private static uint MyRand(
        ref uint seed)
    {
        seed = (uint)(((ulong)seed * 48271u) % 2147483647u);
        return seed;
    }

    // A pair of histograms, a candidate for merging (HistogramPair)
    private struct HistogramPair
    {
        public int Idx1;
        public int Idx2;
        public long CostDiff;
        public ulong CostCombo;
        public ulong Cost0;
        public ulong Cost1;
        public ulong Cost2;
        public ulong Cost3;
        public ulong Cost4;

        [System.Diagnostics.CodeAnalysis.UnscopedRef]
        public Span<ulong> Costs => System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref this.Cost0, 5);
    }

    private sealed class HistoQueue
    {
        public readonly HistogramPair[] Queue;
        public int Size;
        public readonly int MaxSize;

        public HistoQueue(
            int maxSize)
        {
            this.MaxSize = maxSize;
            this.Queue = new HistogramPair[maxSize + 1];
        }

        public void PopPair(
            int pair)
        {
            this.Queue[pair] = this.Queue[this.Size - 1];
            --this.Size;
        }

        public void UpdateHead(
            int pair)
        {
            if (this.Queue[pair].CostDiff < this.Queue[0].CostDiff)
            {
                (this.Queue[0], this.Queue[pair]) = (this.Queue[pair], this.Queue[0]);
            }
        }
    }

    private static void HistoQueueFixPair(
        int badId,
        int goodId,
        ref HistogramPair pair)
    {
        if (pair.Idx1 == badId)
        {
            pair.Idx1 = goodId;
        }

        if (pair.Idx2 == badId)
        {
            pair.Idx2 = goodId;
        }

        if (pair.Idx1 > pair.Idx2)
        {
            (pair.Idx1, pair.Idx2) = (pair.Idx2, pair.Idx1);
        }
    }

    private static bool HistoQueueUpdatePair(
        Histogram h1,
        Histogram h2,
        long costThreshold,
        ref HistogramPair pair)
    {
        long sumCost = (long)(h1.BitCost + h2.BitCost);
        SaturateAdd((ulong)sumCost, ref costThreshold);
        if (!GetCombinedHistogramEntropy(h1, h2, costThreshold, out pair.CostCombo, pair.Costs))
        {
            return false;
        }

        pair.CostDiff = (long)pair.CostCombo - sumCost;
        return true;
    }

    private static long HistoQueuePush(
        HistoQueue histoQueue,
        Histogram[] histograms,
        int idx1,
        int idx2,
        long threshold)
    {
        if (histoQueue.Size == histoQueue.MaxSize)
        {
            return 0;
        }

        if (idx1 > idx2)
        {
            (idx1, idx2) = (idx2, idx1);
        }

        var pair = new HistogramPair { Idx1 = idx1, Idx2 = idx2 };
        if (!HistoQueueUpdatePair(histograms[idx1], histograms[idx2], threshold, ref pair))
        {
            return 0;
        }

        histoQueue.Queue[histoQueue.Size++] = pair;
        histoQueue.UpdateHead(histoQueue.Size - 1);
        return pair.CostDiff;
    }

    private static void HistogramCombineGreedy(
        HistogramSet imageHisto)
    {
        int imageHistoSize = imageHisto.Size;
        var histograms = imageHisto.Histograms;
        var histoQueue = new HistoQueue(imageHistoSize * imageHistoSize);
        for (int i = 0; i < imageHistoSize; i++)
        {
            for (int j = i + 1; j < imageHistoSize; j++)
            {
                HistoQueuePush(histoQueue, histograms, i, j, 0);
            }
        }

        while (histoQueue.Size > 0)
        {
            int idx1 = histoQueue.Queue[0].Idx1;
            int idx2 = histoQueue.Queue[0].Idx2;
            HistogramAdd(histograms[idx2], histograms[idx1], histograms[idx1]);
            UpdateHistogramCost(histoQueue.Queue[0].CostCombo, histoQueue.Queue[0].Costs, histograms[idx1]);
            imageHisto.RemoveHistogram(idx2);

            // The pairs with either of the merged histograms go
            for (int i = 0; i < histoQueue.Size;)
            {
                ref var p = ref histoQueue.Queue[i];
                if ((p.Idx1 == idx1) || (p.Idx2 == idx1) || (p.Idx1 == idx2) || (p.Idx2 == idx2))
                {
                    histoQueue.PopPair(i);
                }
                else
                {
                    HistoQueueFixPair(imageHisto.Size, idx2, ref p);
                    histoQueue.UpdateHead(i);
                    ++i;
                }
            }

            // The pairs with the merged histogram come
            for (int i = 0; i < imageHisto.Size; i++)
            {
                if (i == idx1)
                {
                    continue;
                }

                HistoQueuePush(histoQueue, imageHisto.Histograms, idx1, i, 0);
            }
        }
    }

    private static bool HistogramCombineStochastic(
        HistogramSet imageHisto,
        int minClusterSize)
    {
        uint seed = 1;
        int triesWithNoSuccess = 0;
        int outerIters = imageHisto.Size;
        int numTriesNoSuccess = outerIters / 2;
        var histograms = imageHisto.Histograms;
        const int HistoQueueSize = 9;
        if (imageHisto.Size < minClusterSize)
        {
            return true;
        }

        var histoQueue = new HistoQueue(HistoQueueSize);
        for (int iter = 0; (iter < outerIters) && (imageHisto.Size >= minClusterSize) && (++triesWithNoSuccess < numTriesNoSuccess); iter++)
        {
            long bestCost = (histoQueue.Size == 0) ? 0 : histoQueue.Queue[0].CostDiff;
            uint randRange = (uint)((imageHisto.Size - 1) * imageHisto.Size);
            int numTries = imageHisto.Size / 2;
            for (int j = 0; (imageHisto.Size >= 2) && (j < numTries); j++)
            {
                // Two different histograms at random
                uint tmp = MyRand(ref seed) % randRange;
                uint idx1 = tmp / (uint)(imageHisto.Size - 1);
                uint idx2 = tmp % (uint)(imageHisto.Size - 1);
                if (idx2 >= idx1)
                {
                    ++idx2;
                }

                long currentCost = HistoQueuePush(histoQueue, histograms, (int)idx1, (int)idx2, bestCost);
                if (currentCost < 0)
                {
                    bestCost = currentCost;
                    if (histoQueue.Size == histoQueue.MaxSize)
                    {
                        break;
                    }
                }
            }

            if (histoQueue.Size == 0)
            {
                continue;
            }

            int bestIdx1 = histoQueue.Queue[0].Idx1;
            int bestIdx2 = histoQueue.Queue[0].Idx2;
            HistogramAdd(histograms[bestIdx2], histograms[bestIdx1], histograms[bestIdx1]);
            UpdateHistogramCost(histoQueue.Queue[0].CostCombo, histoQueue.Queue[0].Costs, histograms[bestIdx1]);
            imageHisto.RemoveHistogram(bestIdx2);

            for (int j = 0; j < histoQueue.Size;)
            {
                ref var p = ref histoQueue.Queue[j];
                bool isIdx1Best = (p.Idx1 == bestIdx1) || (p.Idx1 == bestIdx2);
                bool isIdx2Best = (p.Idx2 == bestIdx1) || (p.Idx2 == bestIdx2);
                if (isIdx1Best && isIdx2Best)
                {
                    histoQueue.PopPair(j);
                    continue;
                }

                if (isIdx1Best || isIdx2Best)
                {
                    HistoQueueFixPair(bestIdx2, bestIdx1, ref p);
                    if (!HistoQueueUpdatePair(histograms[p.Idx1], histograms[p.Idx2], 0, ref p))
                    {
                        histoQueue.PopPair(j);
                        continue;
                    }
                }

                HistoQueueFixPair(imageHisto.Size, bestIdx2, ref p);
                histoQueue.UpdateHead(j);
                ++j;
            }

            triesWithNoSuccess = 0;
        }

        return imageHisto.Size <= minClusterSize;
    }

    // The best output histogram for each input one, and the outputs recomputed from them
    private static void HistogramRemap(
        HistogramSet input,
        Histogram?[] inputHistograms,
        HistogramSet output,
        Span<uint> symbols)
    {
        var outHisto = output.Histograms;
        int inSize = output.MaxSize;
        int outSize = output.Size;
        if (outSize > 1)
        {
            for (int i = 0; i < inSize; i++)
            {
                int bestOut = 0;
                long bestBits = long.MaxValue;
                if (inputHistograms[i] is null)
                {
                    symbols[i] = symbols[i - 1];
                    continue;
                }

                for (int k = 0; k < outSize; k++)
                {
                    if (HistogramAddThresh(outHisto[k], inputHistograms[i]!, bestBits, out long currentBits))
                    {
                        bestBits = currentBits;
                        bestOut = k;
                    }
                }

                symbols[i] = (uint)bestOut;
            }
        }
        else
        {
            for (int i = 0; i < inSize; i++)
            {
                symbols[i] = 0;
            }
        }

        output.Clear();
        output.Size = outSize;
        for (int i = 0; i < inSize; i++)
        {
            if (inputHistograms[i] is not Histogram histogram)
            {
                continue;
            }

            int idx = (int)symbols[i];
            HistogramAdd(histogram, outHisto[idx], outHisto[idx]);
        }
    }

    private static int GetCombineCostFactor(
        int histoSize,
        int quality)
    {
        int combineCostFactor = 16;
        if (quality < 90)
        {
            if (histoSize > 256)
            {
                combineCostFactor /= 2;
            }

            if (histoSize > 512)
            {
                combineCostFactor /= 2;
            }

            if (histoSize > 1024)
            {
                combineCostFactor /= 2;
            }

            if (quality <= 50)
            {
                combineCostFactor /= 2;
            }
        }

        return combineCostFactor;
    }

    // VP8LGetHistoImageSymbols
    private static void GetHistoImageSymbols(
        int xsize,
        int ysize,
        BackwardRefs refs,
        int quality,
        bool lowEffort,
        int histogramBits,
        int cacheBits,
        HistogramSet imageHisto,
        Histogram tmpHisto,
        Span<uint> histogramSymbols)
    {
        int histoXsize = (histogramBits != 0) ? SubSampleSize(xsize, histogramBits) : 1;
        int histoYsize = (histogramBits != 0) ? SubSampleSize(ysize, histogramBits) : 1;
        int imageHistoRawSize = histoXsize * histoYsize;
        var origHisto = new HistogramSet(imageHistoRawSize, cacheBits);
        var origHistograms = new Histogram?[imageHistoRawSize];
        int entropyCombineNumBins = lowEffort ? NumPartitions : BinSize;

        HistogramBuild(xsize, histogramBits, refs, origHisto);

        HistogramCopyAndAnalyze(origHisto, origHistograms, imageHisto);
        bool entropyCombine = (imageHisto.Size > entropyCombineNumBins * 2) && (quality < 100);
        if (entropyCombine)
        {
            int combineCostFactor = GetCombineCostFactor(imageHistoRawSize, quality);
            HistogramAnalyzeEntropyBin(imageHisto, lowEffort);
            HistogramCombineEntropyBin(imageHisto, tmpHisto, entropyCombineNumBins, combineCostFactor, lowEffort);
        }

        // No stochastic or greedy combining for low effort
        if (!lowEffort || !entropyCombine)
        {
            // A cubic ramp between 1 and MaxHistoGreedy
            int thresholdSize = (int)(1 + DivRound((long)quality * quality * quality * (MaxHistoGreedy - 1), 100 * 100 * 100));
            bool doGreedy = HistogramCombineStochastic(imageHisto, thresholdSize);
            if (doGreedy)
            {
                HistogramCombineGreedy(imageHisto);
            }
        }

        HistogramRemap(origHisto, origHistograms, imageHisto, histogramSymbols);
    }
}
