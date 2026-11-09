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
    // The cache bits giving the lowest entropy, up to bestCacheBits (CalculateBestCacheSize)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int CalculateBestCacheSize(
        ReadOnlySpan<uint> argb,
        int quality,
        BackwardRefs refs,
        int bestCacheBits)
    {
        int cacheBitsMax = (quality <= 25) ? 0 : bestCacheBits;
        if (cacheBitsMax == 0)
        {
            return 0;
        }

        // Each size's cache and counts in one array, flat: the cache of i bits at 1 << i (2^i colors),
        // and the counts of size i at counts[i]: its literal codes, then red, blue and alpha
        var caches = new uint[2 << cacheBitsMax];
        Span<int> countsAt = stackalloc int[MaxColorCacheBits + 2];
        int total = 0;
        for (int i = 0; i <= cacheBitsMax; i++)
        {
            countsAt[i] = total;
            total += HistogramNumCodes(i) + (3 * 256);
        }

        countsAt[cacheBitsMax + 1] = total;
        var counts = new uint[total];
        ref uint cache = ref MemoryMarshal.GetArrayDataReference(caches);
        ref uint count = ref MemoryMarshal.GetArrayDataReference(counts);
        ref int at = ref MemoryMarshal.GetReference(countsAt);
        ref uint pixels = ref MemoryMarshal.GetReference(argb);
        int shift = 32 - cacheBitsMax;
        int pixel = 0;
        foreach (ref readonly var v in refs.Span)
        {
            if (v.Mode == ModeLiteral)
            {
                uint pix = Unsafe.Add(ref pixels, pixel++);
                int g = (int)((pix >> 8) & 0xff);
                int r = (int)((pix >> 16) & 0xff) + NumLiteralCodes + NumLengthCodes;
                int b = (int)(pix & 0xff) + NumLiteralCodes + NumLengthCodes + 256;
                int a = (int)(pix >> 24) + NumLiteralCodes + NumLengthCodes + 512;
                int key = HashPix(pix, shift);

                // No cache: the literal codes have no cache entries, so red starts at 280
                Unsafe.Add(ref count, g)++;
                Unsafe.Add(ref count, r)++;
                Unsafe.Add(ref count, b)++;
                Unsafe.Add(ref count, a)++;
                for (int i = cacheBitsMax; i >= 1; --i, key >>= 1)
                {
                    ref uint entry = ref Unsafe.Add(ref cache, (1 << i) + key);
                    int baseAt = Unsafe.Add(ref at, i);
                    if (entry == pix)
                    {
                        Unsafe.Add(ref count, baseAt + NumLiteralCodes + NumLengthCodes + key)++;
                    }
                    else
                    {
                        entry = pix;
                        int colors = 1 << i;
                        Unsafe.Add(ref count, baseAt + g)++;
                        Unsafe.Add(ref count, baseAt + colors + r)++;
                        Unsafe.Add(ref count, baseAt + colors + b)++;
                        Unsafe.Add(ref count, baseAt + colors + a)++;
                    }
                }
            }
            else
            {
                // The distance and length histograms are the same at every size; only the length
                // prefix, which is in the literal histogram, counts
                int length = v.Length;
                uint argbPrevious = Unsafe.Add(ref pixels, pixel) ^ 0xffffffffu;
                PrefixEncode(length, out int code, out _, out _);
                for (int i = 0; i <= cacheBitsMax; i++)
                {
                    Unsafe.Add(ref count, Unsafe.Add(ref at, i) + NumLiteralCodes + code)++;
                }

                do
                {
                    uint pix = Unsafe.Add(ref pixels, pixel);
                    if (pix != argbPrevious)
                    {
                        int key = HashPix(pix, shift);
                        for (int i = cacheBitsMax; i >= 1; --i, key >>= 1)
                        {
                            Unsafe.Add(ref cache, (1 << i) + key) = pix;
                        }

                        argbPrevious = pix;
                    }

                    pixel++;
                }
                while (--length != 0);
            }
        }

        ulong entropyMin = ulong.MaxValue;
        int best = 0;
        for (int i = 0; i <= cacheBitsMax; i++)
        {
            var histogram = new Histogram(i);
            int literalSize = HistogramNumCodes(i);
            var source = counts.AsSpan(countsAt[i], literalSize + (3 * 256));
            source.Slice(0, literalSize).CopyTo(histogram.Literal);
            source.Slice(literalSize, 256).CopyTo(histogram.Red);
            source.Slice(literalSize + 256, 256).CopyTo(histogram.Blue);
            source.Slice(literalSize + 512, 256).CopyTo(histogram.Alpha);
            ulong entropy = HistogramEstimateBits(histogram);
            if ((i == 0) || (entropy < entropyMin))
            {
                entropyMin = entropy;
                best = i;
            }
        }

        return best;
    }

    // The references rewritten for a color cache of cacheBits (BackwardRefsWithLocalCache)
    private static void BackwardRefsWithLocalCache(
        ReadOnlySpan<uint> argb,
        int cacheBits,
        BackwardRefs refs)
    {
        int pixelIndex = 0;
        var hashers = new ColorCache(cacheBits);
        ref uint colors = ref MemoryMarshal.GetArrayDataReference(hashers.Colors);
        int shift = hashers.HashShift;
        var items = refs.Span;
        ref uint pixels = ref MemoryMarshal.GetReference(argb);
        int pixelCount = argb.Length;
        foreach (ref var v in items)
        {
            if (v.Mode == ModeLiteral)
            {
                uint argbLiteral = v.ArgbOrDistance;
                int key = HashPix(argbLiteral, shift);
                ref uint entry = ref Unsafe.Add(ref colors, (nint)(uint)key);
                if (entry == argbLiteral)
                {
                    v = PixOrCopy.CacheIndex(key);
                }
                else
                {
                    entry = argbLiteral;
                }

                ++pixelIndex;
            }
            else
            {
                int end = pixelIndex + v.Length;
                if ((uint)end > (uint)pixelCount)
                {
                    throw new InvalidOperationException();
                }

                for (; pixelIndex < end; pixelIndex++)
                {
                    uint pix = Unsafe.Add(ref pixels, (nint)(uint)pixelIndex);
                    Unsafe.Add(ref colors, (nint)(uint)HashPix(pix, shift)) = pix;
                }
            }
        }
    }

    // GetBackwardReferences: refs[offset] gets the best references with a color cache, and if
    // doNoCache, refs[offset + 1] the best without; the next one is scratch
    private static void GetBackwardReferences(
        int width,
        int height,
        ReadOnlySpan<uint> argb,
        int quality,
        int lz77TypesToTry,
        int cacheBitsMax,
        bool doNoCache,
        HashChain hashChain,
        BackwardRefs[] refs,
        int offset,
        ref int cacheBitsBest,
        Scratch scratch)
    {
        Span<int> lz77TypesBest = stackalloc int[2];
        Span<ulong> bitCostsBest = stackalloc ulong[2];
        lz77TypesBest.Clear();
        bitCostsBest[0] = ulong.MaxValue;
        bitCostsBest[1] = ulong.MaxValue;
        var refsTmp = refs[offset + (doNoCache ? 2 : 1)];
        var histogram = scratch.GetHistogram(MaxColorCacheBits);
        HashChain? hashChainBox = null;

        for (int lz77Type = 1; lz77TypesToTry != 0; lz77TypesToTry &= ~lz77Type, lz77Type <<= 1)
        {
            ulong bitCost = 0;
            if ((lz77TypesToTry & lz77Type) == 0)
            {
                continue;
            }

            switch (lz77Type)
            {
                case Lz77Rle:
                    BackwardReferencesRle(width, height, argb, 0, refsTmp);
                    break;

                case Lz77Standard:
                    // With no cache: the ideal LZ77 with a color cache is much the same
                    BackwardReferencesLz77(width, height, argb, 0, hashChain, refsTmp);
                    break;

                case Lz77Box:
                    hashChainBox = scratch.HashChainBox;
                    hashChainBox.Init(width * height);
                    BackwardReferencesLz77Box(width, height, argb, 0, hashChain, hashChainBox, refsTmp);
                    break;
            }

            // The case with no cache first
            for (int i = 1; i >= 0; --i)
            {
                int cacheBits = (i == 1) ? 0 : cacheBitsMax;
                if ((i == 1) && !doNoCache)
                {
                    continue;
                }

                if (i == 0)
                {
                    cacheBits = CalculateBestCacheSize(argb, quality, refsTmp, cacheBits);
                    if (cacheBits > 0)
                    {
                        BackwardRefsWithLocalCache(argb, cacheBits, refsTmp);
                    }
                }

                if ((i == 0) && doNoCache && (cacheBits == 0))
                {
                    // The cost is the one found with no cache
                }
                else
                {
                    HistogramCreate(histogram, refsTmp, cacheBits);
                    bitCost = HistogramEstimateBits(histogram);
                }

                if (bitCost < bitCostsBest[i])
                {
                    if (i == 1)
                    {
                        refsTmp.CloneTo(refs[offset + 1]);
                    }
                    else
                    {
                        BackwardRefs.Swap(refsTmp, refs[offset]);
                    }

                    bitCostsBest[i] = bitCost;
                    lz77TypesBest[i] = lz77Type;
                    if (i == 0)
                    {
                        cacheBitsBest = cacheBits;
                    }
                }
            }
        }

        // Improve on plain LZ77 at high quality
        for (int i = 1; i >= 0; --i)
        {
            if ((i == 1) && !doNoCache)
            {
                continue;
            }

            if (((lz77TypesBest[i] == Lz77Standard) || (lz77TypesBest[i] == Lz77Box)) && (quality >= 25))
            {
                var hashChainTmp = (lz77TypesBest[i] == Lz77Standard) ? hashChain : hashChainBox!;
                int cacheBits = (i == 1) ? 0 : cacheBitsBest;
                BackwardReferencesTraceBackwards(width, height, argb, cacheBits, hashChainTmp, refs[offset + i], refsTmp);
                HistogramCreate(histogram, refsTmp, cacheBits);
                ulong bitCostTrace = HistogramEstimateBits(histogram);
                if (bitCostTrace < bitCostsBest[i])
                {
                    BackwardRefs.Swap(refsTmp, refs[offset + i]);
                }
            }

            BackwardReferences2DLocality(width, refs[offset + i]);
            if ((i == 1) && (lz77TypesBest[0] == lz77TypesBest[1]) && (cacheBitsBest == 0))
            {
                // The same LZ77 with no cache: copy it and stop here
                refs[offset + 1].CloneTo(refs[offset]);
                break;
            }
        }
    }

    // VP8LGetBackwardReferences
    private static void GetBackwardReferences(
        int width,
        int height,
        ReadOnlySpan<uint> argb,
        int quality,
        bool lowEffort,
        int lz77TypesToTry,
        int cacheBitsMax,
        bool doNoCache,
        HashChain hashChain,
        BackwardRefs[] refs,
        int offset,
        out int cacheBitsBest,
        Scratch scratch)
    {
        if (lowEffort)
        {
            cacheBitsBest = 0;
            BackwardReferencesLz77(width, height, argb, 0, hashChain, refs[offset]);
            BackwardReferences2DLocality(width, refs[offset]);
        }
        else
        {
            cacheBitsBest = 0;
            GetBackwardReferences(width, height, argb, quality, lz77TypesToTry, cacheBitsMax, doNoCache, hashChain, refs, offset, ref cacheBitsBest, scratch);
        }
    }
}
