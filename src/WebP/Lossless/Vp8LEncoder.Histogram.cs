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
using System.Runtime.CompilerServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private const ushort NonTrivialSymbol = 0xffff;
    private const int NumPartitions = 4;
    private const int BinSize = NumPartitions * NumPartitions * NumPartitions;
    private const int MaxHistoGreedy = 100;

    private const int LiteralIndex = 0;
    private const int RedIndex = 1;
    private const int BlueIndex = 2;
    private const int AlphaIndex = 3;
    private const int DistanceIndex = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HistogramNumCodes(
        int paletteCodeBits)
    {
        return NumLiteralCodes + NumLengthCodes + ((paletteCodeBits > 0) ? (1 << paletteCodeBits) : 0);
    }

    // The counts of each symbol, and their costs (VP8LHistogram)
    private sealed class Histogram
    {
        public readonly uint[] Literal;
        public readonly uint[] Red = new uint[NumLiteralCodes];
        public readonly uint[] Blue = new uint[NumLiteralCodes];
        public readonly uint[] Alpha = new uint[NumLiteralCodes];
        public readonly uint[] Distance = new uint[NumDistanceCodes];
        public int PaletteCodeBits;
        public readonly ushort[] TrivialSymbol = new ushort[5];
        public ulong BitCost;
        public readonly ulong[] Costs = new ulong[5];
        public readonly bool[] IsUsed = new bool[5];
        public ushort BinId;

        public Histogram(
            int paletteCodeBits)
        {
            this.PaletteCodeBits = paletteCodeBits;
            this.Literal = new uint[HistogramNumCodes(paletteCodeBits)];
            this.StatsClear();
        }

        public void StatsClear()
        {
            for (int i = 0; i < 5; i++)
            {
                this.TrivialSymbol[i] = NonTrivialSymbol;
                this.IsUsed[i] = true;
            }

            this.BitCost = 0;
            Array.Clear(this.Costs);
        }

        // HistogramClear: everything zero, then the stats as new
        public void Clear()
        {
            Array.Clear(this.Literal);
            Array.Clear(this.Red);
            Array.Clear(this.Blue);
            Array.Clear(this.Alpha);
            Array.Clear(this.Distance);
            this.BinId = 0;
            this.StatsClear();
        }

        // Everything zero, as a set's histograms are when it's cleared (VP8LHistogramSetClear)
        public void Zero()
        {
            Array.Clear(this.Literal);
            Array.Clear(this.Red);
            Array.Clear(this.Blue);
            Array.Clear(this.Alpha);
            Array.Clear(this.Distance);
            Array.Clear(this.TrivialSymbol);
            this.BitCost = 0;
            Array.Clear(this.Costs);
            Array.Clear(this.IsUsed);
            this.BinId = 0;
        }

        public void CopyTo(
            Histogram destination)
        {
            this.Literal.AsSpan().CopyTo(destination.Literal);
            this.Red.AsSpan().CopyTo(destination.Red);
            this.Blue.AsSpan().CopyTo(destination.Blue);
            this.Alpha.AsSpan().CopyTo(destination.Alpha);
            this.Distance.AsSpan().CopyTo(destination.Distance);
            this.TrivialSymbol.AsSpan().CopyTo(destination.TrivialSymbol);
            destination.BitCost = this.BitCost;
            this.Costs.AsSpan().CopyTo(destination.Costs);
            this.IsUsed.AsSpan().CopyTo(destination.IsUsed);
            destination.BinId = this.BinId;
        }

        public Span<uint> Population(
            int index)
        {
            return index switch
            {
                LiteralIndex => this.Literal,
                RedIndex => this.Red,
                BlueIndex => this.Blue,
                AlphaIndex => this.Alpha,
                _ => this.Distance,
            };
        }
    }

    // A fixed set of histograms (VP8LHistogramSet): Blocks in the order they were made, which is
    // the order a clear restores, and Histograms as they are now arranged
    private sealed class HistogramSet
    {
        public readonly Histogram[] Blocks;
        public readonly Histogram[] Histograms;
        public int Size;

        public HistogramSet(
            int size,
            int cacheBits)
        {
            this.Blocks = new Histogram[size];
            this.Histograms = new Histogram[size];
            for (int i = 0; i < size; i++)
            {
                this.Blocks[i] = new Histogram(cacheBits);
                this.Histograms[i] = this.Blocks[i];
            }

            this.Size = size;
        }

        public int MaxSize => this.Blocks.Length;

        public void Clear()
        {
            for (int i = 0; i < this.Blocks.Length; i++)
            {
                this.Blocks[i].Zero();
                this.Histograms[i] = this.Blocks[i];
            }

            this.Size = this.Blocks.Length;
        }

        public void RemoveHistogram(
            int i)
        {
            this.Histograms[i] = this.Histograms[this.Size - 1];
            --this.Size;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HistogramAddSinglePixOrCopy(
        Histogram histogram,
        in PixOrCopy v,
        int distanceModifierXsize)
    {
        if (v.Mode == ModeLiteral)
        {
            uint argb = v.ArgbOrDistance;
            histogram.Alpha[argb >> 24]++;
            histogram.Red[(argb >> 16) & 0xff]++;
            histogram.Literal[(argb >> 8) & 0xff]++;
            histogram.Blue[argb & 0xff]++;
        }
        else if (v.Mode == ModeCacheIndex)
        {
            histogram.Literal[NumLiteralCodes + NumLengthCodes + (int)v.ArgbOrDistance]++;
        }
        else
        {
            PrefixEncodeBits(v.Length, out int code, out _);
            histogram.Literal[NumLiteralCodes + code]++;
            int distance = (int)v.ArgbOrDistance;
            if (distanceModifierXsize != 0)
            {
                distance = DistanceToPlaneCode(distanceModifierXsize, distance);
            }

            PrefixEncodeBits(distance, out code, out _);
            histogram.Distance[code]++;
        }
    }

    // VP8LHistogramStoreRefs: distances as plane codes of a row of xsize, unless xsize is 0
    private static void HistogramStoreRefs(
        BackwardRefs refs,
        int distanceModifierXsize,
        Histogram histogram)
    {
        foreach (ref readonly var v in refs.Span)
        {
            HistogramAddSinglePixOrCopy(histogram, v, distanceModifierXsize);
        }
    }

    // VP8LHistogramCreate
    private static void HistogramCreate(
        Histogram histogram,
        BackwardRefs refs,
        int paletteCodeBits)
    {
        // The histogram's literal array holds its largest cache; with fewer bits, the costs read
        // only the codes those have
        histogram.PaletteCodeBits = paletteCodeBits;
        histogram.Clear();
        HistogramStoreRefs(refs, 0, histogram);
    }

    //------------------------------------------------------------------------------
    // Entropy

    private static ulong BitsEntropyRefine(
        in BitEntropy entropy)
    {
        ulong mix;
        if (entropy.Nonzeros < 5)
        {
            if (entropy.Nonzeros <= 1)
            {
                return 0;
            }

            // Two symbols are 0 and 1 in a Huffman code: mix in a little entropy to favor good
            // clustering when they're combined
            if (entropy.Nonzeros == 2)
            {
                return (ulong)DivRound((long)((99 * ((ulong)entropy.Sum << Log2PrecisionBits)) + entropy.Entropy), 100);
            }

            mix = (entropy.Nonzeros == 3) ? 950ul : 700ul;
        }
        else
        {
            mix = 627;
        }

        ulong minLimit = (ulong)((2 * entropy.Sum) - entropy.MaxValue) << Log2PrecisionBits;
        minLimit = (ulong)DivRound((long)((mix * minLimit) + ((1000 - mix) * entropy.Entropy)), 1000);
        return (entropy.Entropy < minLimit) ? minLimit : entropy.Entropy;
    }

    private static ulong BitsEntropy(
        ReadOnlySpan<uint> array)
    {
        BitsEntropyUnrefined(array, out var entropy);
        return BitsEntropyRefine(entropy);
    }

    private static ulong InitialHuffmanCost()
    {
        const ulong HuffmanCodeOfHuffmanCodeSize = CodeLengthCodes * 3;
        return (HuffmanCodeOfHuffmanCodeSize << Log2PrecisionBits) - (ulong)DivRound(91L << Log2PrecisionBits, 10);
    }

    private static ulong FinalHuffmanCost(
        in Streaks stats)
    {
        ulong result = InitialHuffmanCost();
        uint extra = (uint)((stats.Counts0 * 1600) + (240 * stats.Streaks01));
        extra += (uint)((stats.Counts1 * 2640) + (720 * stats.Streaks11));
        extra += (uint)(1840 * stats.Streaks00);
        extra += (uint)(3360 * stats.Streaks10);
        return result + ((ulong)extra << (Log2PrecisionBits - 10));
    }

    private static ulong PopulationCost(
        ReadOnlySpan<uint> population,
        out ushort trivialSymbol,
        out bool isUsed)
    {
        GetEntropyUnrefined(population, out var bitEntropy, out var stats);
        trivialSymbol = (bitEntropy.Nonzeros == 1) ? (ushort)bitEntropy.NonzeroCode : NonTrivialSymbol;
        isUsed = (stats.Streaks10 != 0) || (stats.Streaks11 != 0);
        return BitsEntropyRefine(bitEntropy) + FinalHuffmanCost(stats);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<uint> GetPopulation(
        Histogram histogram,
        int index)
    {
        return index switch
        {
            LiteralIndex => histogram.Literal.AsSpan(0, HistogramNumCodes(histogram.PaletteCodeBits)),
            RedIndex => histogram.Red,
            BlueIndex => histogram.Blue,
            AlphaIndex => histogram.Alpha,
            _ => histogram.Distance,
        };
    }

    private static ulong GetCombinedEntropy(
        Histogram h1,
        Histogram h2,
        int index)
    {
        bool isH1Used = h1.IsUsed[index];
        bool isH2Used = h2.IsUsed[index];
        bool isTrivial = (h1.TrivialSymbol[index] != NonTrivialSymbol) && (h1.TrivialSymbol[index] == h2.TrivialSymbol[index]);
        if (isTrivial || !isH1Used || !isH2Used)
        {
            return isH1Used ? h1.Costs[index] : h2.Costs[index];
        }

        var x = GetPopulation(h1, index);
        var y = GetPopulation(h2, index);
        GetCombinedEntropyUnrefined(x, y, out var bitEntropy, out var stats);
        return BitsEntropyRefine(bitEntropy) + FinalHuffmanCost(stats);
    }

    // VP8LHistogramEstimateBits
    private static ulong HistogramEstimateBits(
        Histogram h)
    {
        ulong cost = 0;
        for (int i = 0; i < 5; i++)
        {
            cost += PopulationCost(GetPopulation(h, i), out _, out _);
        }

        cost += (ulong)(ExtraCost(h.Literal.AsSpan(NumLiteralCodes, NumLengthCodes)) + ExtraCost(h.Distance)) << Log2PrecisionBits;
        return cost;
    }

    // b += a, saturating at long.MaxValue
    private static void SaturateAdd(
        ulong a,
        ref long b)
    {
        if ((b < 0) || ((long)a <= long.MaxValue - b))
        {
            b += (long)a;
        }
        else
        {
            b = long.MaxValue;
        }
    }

    private static bool GetCombinedHistogramEntropy(
        Histogram a,
        Histogram b,
        long costThresholdIn,
        out ulong cost,
        Span<ulong> costs)
    {
        ulong costThreshold = (ulong)costThresholdIn;
        cost = 0;
        if (costThresholdIn <= 0)
        {
            return false;
        }

        for (int i = 0; i < 5; i++)
        {
            costs[i] = GetCombinedEntropy(a, b, i);
            cost += costs[i];
            if (cost >= costThreshold)
            {
                return false;
            }
        }

        return true;
    }

    private static void HistogramAdd(
        Histogram h1,
        Histogram h2,
        Histogram output)
    {
        for (int i = 0; i < 5; i++)
        {
            var p1 = GetPopulation(h1, i);
            var p2 = GetPopulation(h2, i);
            var pout = output.Population(i).Slice(0, p1.Length);
            if (ReferenceEquals(h2, output))
            {
                if (h1.IsUsed[i])
                {
                    if (output.IsUsed[i])
                    {
                        AddVectorEq(p1, pout);
                    }
                    else
                    {
                        p1.CopyTo(pout);
                    }
                }
            }
            else
            {
                if (h1.IsUsed[i])
                {
                    if (h2.IsUsed[i])
                    {
                        AddVector(p1, p2, pout);
                    }
                    else
                    {
                        p1.CopyTo(pout);
                    }
                }
                else if (h2.IsUsed[i])
                {
                    p2.CopyTo(pout);
                }
                else
                {
                    pout.Clear();
                }
            }
        }

        for (int i = 0; i < 5; i++)
        {
            output.TrivialSymbol[i] = (h1.TrivialSymbol[i] == h2.TrivialSymbol[i]) ? h1.TrivialSymbol[i] : NonTrivialSymbol;
            output.IsUsed[i] = h1.IsUsed[i] || h2.IsUsed[i];
        }
    }

    private static void UpdateHistogramCost(
        ulong bitCost,
        ReadOnlySpan<ulong> costs,
        Histogram h)
    {
        h.BitCost = bitCost;
        costs.Slice(0, 5).CopyTo(h.Costs);
    }

    private static bool HistogramAddEval(
        Histogram a,
        Histogram b,
        Histogram output,
        long costThreshold)
    {
        ulong sumCost = a.BitCost + b.BitCost;
        Span<ulong> costs = stackalloc ulong[5];
        SaturateAdd(sumCost, ref costThreshold);
        if (!GetCombinedHistogramEntropy(a, b, costThreshold, out ulong bitCost, costs))
        {
            return false;
        }

        HistogramAdd(a, b, output);
        UpdateHistogramCost(bitCost, costs, output);
        return true;
    }

    private static bool HistogramAddThresh(
        Histogram a,
        Histogram b,
        long costThreshold,
        out long costOut)
    {
        Span<ulong> costs = stackalloc ulong[5];
        SaturateAdd(a.BitCost, ref costThreshold);
        if (!GetCombinedHistogramEntropy(a, b, costThreshold, out ulong cost, costs))
        {
            costOut = 0;
            return false;
        }

        costOut = (long)cost - (long)a.BitCost;
        return true;
    }

    private static void ComputeHistogramCost(
        Histogram h)
    {
        for (int i = 0; i < 5; i++)
        {
            h.Costs[i] = PopulationCost(GetPopulation(h, i), out h.TrivialSymbol[i], out h.IsUsed[i]);
        }

        h.BitCost = h.Costs[LiteralIndex] + h.Costs[RedIndex] + h.Costs[BlueIndex] + h.Costs[AlphaIndex] + h.Costs[DistanceIndex];
    }

    private static int GetBinIdForEntropy(
        ulong min,
        ulong max,
        ulong value)
    {
        ulong range = max - min;
        if (range > 0)
        {
            ulong delta = value - min;
            return (int)((NumPartitions - 1e-6) * delta / range);
        }

        return 0;
    }
}
