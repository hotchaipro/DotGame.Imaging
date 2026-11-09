// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/backward_references_cost_enc.c), modified by Hot Chai Games LLC:
// written in C#, the intervals in arrays by index. libwebp carries this notice:
//   Copyright 2017 Google Inc. All Rights Reserved.
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
    private const int CostCacheIntervalSizeMax = 500;

    // The bit estimates of each symbol (CostModel)
    private sealed class CostModel
    {
        public readonly uint[] Alpha = new uint[256];
        public readonly uint[] Red = new uint[256];
        public readonly uint[] Blue = new uint[256];
        public readonly uint[] Distance = new uint[NumDistanceCodes];
        public uint[] Literal = [];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetLiteralCost(
            uint v) => (long)this.Alpha[v >> 24] + this.Red[(v >> 16) & 0xff] + this.Literal[(v >> 8) & 0xff] + this.Blue[v & 0xff];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetCacheCost(
            uint index) => this.Literal[NumLiteralCodes + NumLengthCodes + (int)index];

        public long GetLengthCost(
            int length)
        {
            PrefixEncodeBits(length, out int code, out int extraBits);
            return this.Literal[NumLiteralCodes + code] + ((long)extraBits << Log2PrecisionBits);
        }

        public long GetDistanceCost(
            int distance)
        {
            PrefixEncodeBits(distance, out int code, out int extraBits);
            return this.Distance[code] + ((long)extraBits << Log2PrecisionBits);
        }
    }

    private static void ConvertPopulationCountTableToBitEstimates(
        ReadOnlySpan<uint> populationCounts,
        Span<uint> output)
    {
        uint sum = 0;
        int nonzeros = 0;
        for (int i = 0; i < populationCounts.Length; i++)
        {
            sum += populationCounts[i];
            if (populationCounts[i] > 0)
            {
                ++nonzeros;
            }
        }

        if (nonzeros <= 1)
        {
            output.Slice(0, populationCounts.Length).Clear();
        }
        else
        {
            uint logSum = FastLog2(sum);
            for (int i = 0; i < populationCounts.Length; i++)
            {
                output[i] = logSum - FastLog2(populationCounts[i]);
            }
        }
    }

    private static void CostModelBuild(
        CostModel m,
        int xsize,
        int cacheBits,
        BackwardRefs refs)
    {
        var histogram = new Histogram(cacheBits);
        HistogramStoreRefs(refs, xsize, histogram);
        int literalSize = HistogramNumCodes(histogram.PaletteCodeBits);
        m.Literal = new uint[literalSize];
        ConvertPopulationCountTableToBitEstimates(histogram.Literal.AsSpan(0, literalSize), m.Literal);
        ConvertPopulationCountTableToBitEstimates(histogram.Red, m.Red);
        ConvertPopulationCountTableToBitEstimates(histogram.Blue, m.Blue);
        ConvertPopulationCountTableToBitEstimates(histogram.Alpha, m.Alpha);
        ConvertPopulationCountTableToBitEstimates(histogram.Distance, m.Distance);
    }

    // An interval of constant cost (CostInterval), linked by index. Index -1 is null.
    private struct CostInterval
    {
        public long Cost;
        public int Start;
        public int End;
        public int Index;
        public int Previous;
        public int Next;
    }

    // The intervals of constant cost (CostManager). At most CostCacheIntervalSizeMax are in the list
    // when one is added, and freed ones are reused first, so the array never grows.
    private sealed class CostManager
    {
        public readonly CostInterval[] Intervals = new CostInterval[CostCacheIntervalSizeMax];
        public int FreeHead = -1;
        public int Allocated;
        public int Head = -1;
        public int Count;

        public long[] CostCache = [];
        public long[] CacheIntervalCost = [];
        public int[] CacheIntervalStart = [];
        public int[] CacheIntervalEnd = [];
        public int CacheIntervalsSize;

        public long[] Costs = [];
        public ushort[] DistArray = [];

        public void Init(
            ushort[] distArray,
            int pixelCount,
            CostModel costModel)
        {
            int costCacheSize = (pixelCount > MaxLength) ? MaxLength : pixelCount;
            this.Head = -1;
            this.Count = 0;
            this.FreeHead = -1;
            this.Allocated = 0;
            this.DistArray = distArray;
            this.CostCache = new long[costCacheSize];
            for (int i = 0; i < costCacheSize; i++)
            {
                this.CostCache[i] = costModel.GetLengthCost(i);
            }

            this.CacheIntervalsSize = 1;
            for (int i = 1; i < costCacheSize; i++)
            {
                if (this.CostCache[i] != this.CostCache[i - 1])
                {
                    ++this.CacheIntervalsSize;
                }
            }

            this.CacheIntervalCost = new long[this.CacheIntervalsSize];
            this.CacheIntervalStart = new int[this.CacheIntervalsSize];
            this.CacheIntervalEnd = new int[this.CacheIntervalsSize];
            int current = 0;
            this.CacheIntervalStart[0] = 0;
            this.CacheIntervalEnd[0] = 1;
            this.CacheIntervalCost[0] = this.CostCache[0];
            for (int i = 1; i < costCacheSize; i++)
            {
                long costValue = this.CostCache[i];
                if (costValue != this.CacheIntervalCost[current])
                {
                    ++current;
                    this.CacheIntervalStart[current] = i;
                    this.CacheIntervalCost[current] = costValue;
                }

                this.CacheIntervalEnd[current] = i + 1;
            }

            this.Costs = new long[pixelCount];
            this.Costs.AsSpan().Fill(long.MaxValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref CostInterval At(
            int interval) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(this.Intervals), (nint)(uint)interval);

        public int NewInterval()
        {
            if (this.FreeHead >= 0)
            {
                int interval = this.FreeHead;
                this.FreeHead = this.At(interval).Next;
                return interval;
            }

            if (this.Allocated == this.Intervals.Length)
            {
                throw new InvalidOperationException();
            }

            return this.Allocated++;
        }
    }

    private static void UpdateCostPerInterval(
        CostManager manager,
        int start,
        int end,
        int position,
        long cost)
    {
        var costs = manager.Costs;
        var distArray = manager.DistArray;
        for (int i = start; i < end; i++)
        {
            if (costs[i] > cost)
            {
                costs[i] = cost;
                distArray[i] = (ushort)(i - position + 1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConnectIntervals(
        CostManager manager,
        int previous,
        int next)
    {
        if (previous >= 0)
        {
            manager.At(previous).Next = next;
        }
        else
        {
            manager.Head = next;
        }

        if (next >= 0)
        {
            manager.At(next).Previous = previous;
        }
    }

    private static void PopInterval(
        CostManager manager,
        int interval)
    {
        if (interval < 0)
        {
            return;
        }

        ref var item = ref manager.At(interval);
        ConnectIntervals(manager, item.Previous, item.Next);
        item.Next = manager.FreeHead;
        manager.FreeHead = interval;
        --manager.Count;
    }

    // The cost at index i from every stored interval over it, removing the ones that end before i
    // if doCleanIntervals
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateCostAtIndex(
        CostManager manager,
        int i,
        bool doCleanIntervals)
    {
        int current = manager.Head;
        if (current < 0)
        {
            return;
        }

        ref long cost = ref manager.Costs[i];
        while (current >= 0)
        {
            ref var item = ref manager.At(current);
            if (item.Start > i)
            {
                break;
            }

            int next = item.Next;
            if (item.End <= i)
            {
                if (doCleanIntervals)
                {
                    PopInterval(manager, current);
                }
            }
            else if (cost > item.Cost)
            {
                cost = item.Cost;
                manager.DistArray[i] = (ushort)(i - item.Index + 1);
            }

            current = next;
        }
    }

    private static void PositionOrphanInterval(
        CostManager manager,
        int current,
        int previous)
    {
        if (previous < 0)
        {
            previous = manager.Head;
        }

        int start = manager.At(current).Start;
        while ((previous >= 0) && (start < manager.At(previous).Start))
        {
            previous = manager.At(previous).Previous;
        }

        while ((previous >= 0) && (manager.At(previous).Next >= 0) && (manager.At(manager.At(previous).Next).Start < start))
        {
            previous = manager.At(previous).Next;
        }

        if (previous >= 0)
        {
            ConnectIntervals(manager, current, manager.At(previous).Next);
        }
        else
        {
            ConnectIntervals(manager, current, manager.Head);
        }

        ConnectIntervals(manager, previous, current);
    }

    private static void InsertInterval(
        CostManager manager,
        int intervalIn,
        long cost,
        int position,
        int start,
        int end)
    {
        if (start >= end)
        {
            return;
        }

        if (manager.Count >= CostCacheIntervalSizeMax)
        {
            UpdateCostPerInterval(manager, start, end, position, cost);
            return;
        }

        int intervalNew = manager.NewInterval();
        ref var item = ref manager.At(intervalNew);
        item.Cost = cost;
        item.Index = position;
        item.Start = start;
        item.End = end;
        PositionOrphanInterval(manager, intervalNew, intervalIn);
        ++manager.Count;
    }

    // A new interval's contributions, from position with its length and distance cost
    private static void PushInterval(
        CostManager manager,
        long distanceCost,
        int position,
        int length)
    {
        const int SkipDistance = 10;
        if (length < SkipDistance)
        {
            var costs = manager.Costs;
            var distArray = manager.DistArray;
            var costCache = manager.CostCache;
            for (int j = position; j < position + length; j++)
            {
                int k = j - position;
                long costTmp = distanceCost + costCache[k];
                if (costs[j] > costTmp)
                {
                    costs[j] = costTmp;
                    distArray[j] = (ushort)(k + 1);
                }
            }

            return;
        }

        int interval = manager.Head;
        for (int i = 0; (i < manager.CacheIntervalsSize) && (manager.CacheIntervalStart[i] < length); i++)
        {
            int start = position + manager.CacheIntervalStart[i];
            int end = position + ((manager.CacheIntervalEnd[i] > length) ? length : manager.CacheIntervalEnd[i]);
            long cost = distanceCost + manager.CacheIntervalCost[i];
            int intervalNext;
            for (; interval >= 0; interval = intervalNext)
            {
                ref var item = ref manager.At(interval);
                if (item.Start >= end)
                {
                    break;
                }

                intervalNext = item.Next;
                if (start >= item.End)
                {
                    continue;
                }

                if (cost >= item.Cost)
                {
                    // Worse than what's there: add what's before it
                    int startNew = item.End;
                    InsertInterval(manager, interval, cost, position, start, item.Start);
                    start = startNew;
                    if (start >= end)
                    {
                        break;
                    }

                    continue;
                }

                if (start <= item.Start)
                {
                    if (item.End <= end)
                    {
                        // The old interval is inside the new one
                        PopInterval(manager, interval);
                    }
                    else
                    {
                        item.Start = end;
                        break;
                    }
                }
                else
                {
                    if (end < item.End)
                    {
                        // The old interval holds the new one: split it
                        int endOriginal = item.End;
                        item.End = start;
                        InsertInterval(manager, interval, item.Cost, item.Index, end, endOriginal);
                        interval = item.Next;
                        break;
                    }
                    else
                    {
                        item.End = start;
                    }
                }
            }

            InsertInterval(manager, interval, cost, position, start, end);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddSingleLiteralWithCostModel(
        ReadOnlySpan<uint> argb,
        in ColorCache hashers,
        CostModel costModel,
        int idx,
        bool useColorCache,
        long previousCost,
        long[] cost,
        ushort[] distArray)
    {
        long costValue = previousCost;
        uint color = argb[idx];
        int ix = useColorCache ? hashers.Contains(color) : -1;
        if (ix >= 0)
        {
            costValue += DivRound(costModel.GetCacheCost((uint)ix) * 68, 100);
        }
        else
        {
            if (useColorCache)
            {
                hashers.Insert(color);
            }

            costValue += DivRound(costModel.GetLiteralCost(color) * 82, 100);
        }

        if (cost[idx] > costValue)
        {
            cost[idx] = costValue;
            distArray[idx] = 1;
        }
    }

    private static void BackwardReferencesHashChainDistanceOnly(
        int xsize,
        int ysize,
        ReadOnlySpan<uint> argb,
        int cacheBits,
        HashChain hashChain,
        BackwardRefs refs,
        ushort[] distArray)
    {
        int pixelCount = xsize * ysize;
        bool useColorCache = cacheBits > 0;
        var costModel = new CostModel();
        var hashers = useColorCache ? new ColorCache(cacheBits) : default;
        var manager = new CostManager();

        CostModelBuild(costModel, xsize, cacheBits, refs);
        manager.Init(distArray, pixelCount, costModel);
        distArray[0] = 0;
        AddSingleLiteralWithCostModel(argb, hashers, costModel, 0, useColorCache, 0, manager.Costs, distArray);

        // The literal costs in one table, so the loop keeps one register for them: alpha, red, blue,
        // then the literal codes (green, lengths and cache indices)
        var table = new uint[(3 * 256) + costModel.Literal.Length];
        costModel.Alpha.CopyTo(table, 0);
        costModel.Red.CopyTo(table, 256);
        costModel.Blue.CopyTo(table, 512);
        costModel.Literal.CopyTo(table, 768);
        if (useColorCache)
        {
            DistanceOnlyLoop<WithColorCache>(manager, hashChain, costModel, table, argb.Slice(0, pixelCount), hashers.Colors, hashers.HashShift, xsize);
        }
        else
        {
            DistanceOnlyLoop<NoColorCache>(manager, hashChain, costModel, table, argb.Slice(0, pixelCount), [], 0, xsize);
        }
    }

    private struct WithColorCache
    {
    }

    private struct NoColorCache
    {
    }

    // The matches' state across pixels in BackwardReferencesHashChainDistanceOnly
    private struct DistanceOnlyState
    {
        public long OffsetCost;
        public int OffsetPrevious;
        public int LengthPrevious;
        public int FirstOffsetIsConstant;
        public int Reach;
    }

    // The loop of BackwardReferencesHashChainDistanceOnly, made for a color cache or none, with the
    // rare work in calls so the loop's values stay in registers
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DistanceOnlyLoop<TCache>(
        CostManager manager,
        HashChain hashChain,
        CostModel costModel,
        uint[] table,
        ReadOnlySpan<uint> argb,
        uint[] cacheColors,
        int hashShift,
        int xsize)
        where TCache : struct
    {
        bool useColorCache = typeof(TCache) == typeof(WithColorCache);
        int pixelCount = argb.Length;
        if ((manager.Costs.Length < pixelCount) || (manager.DistArray.Length < pixelCount) || (hashChain.OffsetLength.Length < pixelCount))
        {
            throw new InvalidOperationException();
        }

        var state = new DistanceOnlyState { OffsetCost = -1, OffsetPrevious = -1, LengthPrevious = -1, FirstOffsetIsConstant = -1 };
        ref long costs = ref MemoryMarshal.GetArrayDataReference(manager.Costs);
        ref ushort distances = ref MemoryMarshal.GetArrayDataReference(manager.DistArray);
        ref uint offsetLength = ref MemoryMarshal.GetArrayDataReference(hashChain.OffsetLength);
        ref uint costTable = ref MemoryMarshal.GetArrayDataReference(table);
        ref uint colors = ref useColorCache ? ref MemoryMarshal.GetArrayDataReference(cacheColors) : ref costTable;
        ref uint pixels = ref MemoryMarshal.GetReference(argb);
        long previousCost = costs;
        for (int i = 1; i < pixelCount; i++)
        {
            uint offsetAndLength = Unsafe.Add(ref offsetLength, (nint)(uint)i);
            uint color = Unsafe.Add(ref pixels, (nint)(uint)i);

            // The pixel as a literal or a cache index (AddSingleLiteralWithCostModel)
            long costValue;
            if (useColorCache)
            {
                int key = HashPix(color, hashShift);
                ref uint entry = ref Unsafe.Add(ref colors, (nint)(uint)key);
                if (entry == color)
                {
                    costValue = previousCost + (long)((((ulong)Unsafe.Add(ref costTable, 768 + NumLiteralCodes + NumLengthCodes + key) * 68) + 50) / 100);
                }
                else
                {
                    entry = color;
                    costValue = previousCost + LiteralCost(ref costTable, color);
                }
            }
            else
            {
                costValue = previousCost + LiteralCost(ref costTable, color);
            }

            ref long cost = ref Unsafe.Add(ref costs, (nint)(uint)i);
            if (cost > costValue)
            {
                cost = costValue;
                Unsafe.Add(ref distances, (nint)(uint)i) = 1;
            }

            int offset = (int)(offsetAndLength >> MaxLengthBits);
            int length = (int)(offsetAndLength & ((1u << MaxLengthBits) - 1));
            if ((length >= 2) && ((offset != state.OffsetPrevious) || (state.FirstOffsetIsConstant != 0) || (i + length - 1 > state.Reach)))
            {
                PushMatch(manager, hashChain, costModel, ref state, i, offset, length, previousCost, xsize);
            }

            if (manager.Head >= 0)
            {
                UpdateCostAtIndexAndClean(manager, i);
            }

            state.OffsetPrevious = offset;
            state.LengthPrevious = length;
            previousCost = cost;
        }
    }

    // A literal's cost, with its discount (82%)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LiteralCost(
        ref uint costTable,
        uint color)
    {
        ulong literal = (ulong)Unsafe.Add(ref costTable, (nint)(color >> 24)) + Unsafe.Add(ref costTable, 256 + (nint)((color >> 16) & 0xff)) +
                        Unsafe.Add(ref costTable, 768 + (nint)((color >> 8) & 0xff)) + Unsafe.Add(ref costTable, 512 + (nint)(color & 0xff));
        return (long)(((literal * 82) + 50) / 100);
    }

    // The match at pixel i: a new interval for a new offset, else the one past reach where a run of
    // the same offset ends
    private static void PushMatch(
        CostManager manager,
        HashChain hashChain,
        CostModel costModel,
        ref DistanceOnlyState state,
        int i,
        int offset,
        int length,
        long previousCost,
        int xsize)
    {
        if (offset != state.OffsetPrevious)
        {
            int code = DistanceToPlaneCode(xsize, offset);
            state.OffsetCost = costModel.GetDistanceCost(code);
            state.FirstOffsetIsConstant = 1;
            PushInterval(manager, previousCost + state.OffsetCost, i, length);
            return;
        }

        // The same offset as the previous pixel's (a region of one color, say)
        if (state.FirstOffsetIsConstant != 0)
        {
            state.Reach = i - 1 + state.LengthPrevious - 1;
            state.FirstOffsetIsConstant = 0;
        }

        if (i + length - 1 > state.Reach)
        {
            state.Reach = PushSameOffset(manager, hashChain, i, offset, state.OffsetCost, state.Reach);
        }
    }

    // The interval from the pixel where a run of one offset ends, past reach; the new reach
    private static int PushSameOffset(
        CostManager manager,
        HashChain hashChain,
        int i,
        int offset,
        long offsetCost,
        int reach)
    {
        int lengthJ = 0;
        int j;
        for (j = i; j <= reach; j++)
        {
            lengthJ = hashChain.FindLength(j + 1);
            if (hashChain.FindOffset(j + 1) != offset)
            {
                lengthJ = hashChain.FindLength(j);
                break;
            }
        }

        UpdateCostAtIndex(manager, j - 1, false);
        UpdateCostAtIndex(manager, j, false);
        PushInterval(manager, manager.Costs[j - 1] + offsetCost, j, lengthJ);
        return j + lengthJ - 1;
    }

    // UpdateCostAtIndex with doCleanIntervals, in registers: the first of the cheapest intervals over
    // i sets its cost, as updating them in turn would
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void UpdateCostAtIndexAndClean(
        CostManager manager,
        int i)
    {
        ref CostInterval intervals = ref MemoryMarshal.GetArrayDataReference(manager.Intervals);
        ref long cost = ref manager.Costs[i];
        long best = cost;
        int bestIndex = -1;
        int current = manager.Head;
        while (current >= 0)
        {
            ref var item = ref Unsafe.Add(ref intervals, (nint)(uint)current);
            if (item.Start > i)
            {
                break;
            }

            int next = item.Next;
            if (item.End <= i)
            {
                // PopInterval
                int previous = item.Previous;
                if (previous >= 0)
                {
                    Unsafe.Add(ref intervals, (nint)(uint)previous).Next = next;
                }
                else
                {
                    manager.Head = next;
                }

                if (next >= 0)
                {
                    Unsafe.Add(ref intervals, (nint)(uint)next).Previous = previous;
                }

                item.Next = manager.FreeHead;
                manager.FreeHead = current;
                --manager.Count;
            }
            else if (best > item.Cost)
            {
                best = item.Cost;
                bestIndex = item.Index;
            }

            current = next;
        }

        if (bestIndex >= 0)
        {
            cost = best;
            manager.DistArray[i] = (ushort)(i - bestIndex + 1);
        }
    }

    // The references along the cheapest path (VP8LBackwardReferencesTraceBackwards)
    private static void BackwardReferencesTraceBackwards(
        int xsize,
        int ysize,
        ReadOnlySpan<uint> argb,
        int cacheBits,
        HashChain hashChain,
        BackwardRefs refsSource,
        BackwardRefs refsDestination)
    {
        int distArraySize = xsize * ysize;
        var distArray = new ushort[distArraySize];
        BackwardReferencesHashChainDistanceOnly(xsize, ysize, argb, cacheBits, hashChain, refsSource, distArray);

        // The path, packed at the end of distArray
        int path = distArraySize;
        int current = distArraySize - 1;
        while (current >= 0)
        {
            int k = distArray[current];
            --path;
            distArray[path] = (ushort)k;
            current -= k;
        }

        bool useColorCache = cacheBits > 0;
        var hashers = useColorCache ? new ColorCache(cacheBits) : default;
        refsDestination.Clear();
        int i = 0;
        for (int ix = path; ix < distArraySize; ix++)
        {
            int length = distArray[ix];
            if (length != 1)
            {
                int offset = hashChain.FindOffset(i);
                refsDestination.Add(PixOrCopy.Copy((uint)offset, length));
                if (useColorCache)
                {
                    var copied = argb.Slice(i, length);
                    ref uint colors = ref MemoryMarshal.GetArrayDataReference(hashers.Colors);
                    int shift = hashers.HashShift;
                    foreach (uint pix in copied)
                    {
                        Unsafe.Add(ref colors, (nint)(uint)HashPix(pix, shift)) = pix;
                    }
                }

                i += length;
            }
            else
            {
                int index = useColorCache ? hashers.Contains(argb[i]) : -1;
                if (index >= 0)
                {
                    refsDestination.Add(PixOrCopy.CacheIndex(index));
                }
                else
                {
                    if (useColorCache)
                    {
                        hashers.Insert(argb[i]);
                    }

                    refsDestination.Add(PixOrCopy.Literal(argb[i]));
                }

                ++i;
            }
        }
    }
}
