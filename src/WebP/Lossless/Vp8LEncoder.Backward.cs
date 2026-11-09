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
    private const int MaxColorCacheBits = 10;
    private const int HashBits = 18;
    private const int HashSize = 1 << HashBits;
    private const int MaxLengthBits = 12;
    private const int WindowSizeBits = 20;
    private const int MaxLength = (1 << MaxLengthBits) - 1;
    private const int WindowSize = (1 << WindowSizeBits) - 120;
    private const int MinLength = 4;

    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;

    private const byte ModeLiteral = 0;
    private const byte ModeCacheIndex = 1;
    private const byte ModeCopy = 2;

    private const int Lz77Standard = 1;
    private const int Lz77Rle = 2;
    private const int Lz77Box = 4;

    // A literal pixel, a color cache index, or a copy of len pixels from distance back (PixOrCopy)
    [StructLayout(LayoutKind.Sequential)]
    private struct PixOrCopy
    {
        public uint ArgbOrDistance;
        public ushort Length;
        public byte Mode;

        public static PixOrCopy Copy(
            uint distance,
            int length) => new() { Mode = ModeCopy, ArgbOrDistance = distance, Length = (ushort)length };

        public static PixOrCopy CacheIndex(
            int index) => new() { Mode = ModeCacheIndex, ArgbOrDistance = (uint)index, Length = 1 };

        public static PixOrCopy Literal(
            uint argb) => new() { Mode = ModeLiteral, ArgbOrDistance = argb, Length = 1 };
    }

    // The backward references, in order (VP8LBackwardRefs)
    private sealed class BackwardRefs
    {
        public PixOrCopy[] Items = new PixOrCopy[256];
        public int Count;

        public Span<PixOrCopy> Span => this.Items.AsSpan(0, this.Count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(
            PixOrCopy value)
        {
            var items = this.Items;
            int count = this.Count;
            if ((uint)count < (uint)items.Length)
            {
                items[count] = value;
                this.Count = count + 1;
            }
            else
            {
                this.AddGrowing(value);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AddGrowing(
            PixOrCopy value)
        {
            Array.Resize(ref this.Items, this.Items.Length * 2);
            this.Items[this.Count++] = value;
        }

        public void Clear()
        {
            this.Count = 0;
        }

        public void CloneTo(
            BackwardRefs destination)
        {
            if (destination.Items.Length < this.Count)
            {
                destination.Items = new PixOrCopy[this.Items.Length];
            }

            this.Span.CopyTo(destination.Items);
            destination.Count = this.Count;
        }

        public static void Swap(
            BackwardRefs a,
            BackwardRefs b)
        {
            (a.Items, b.Items) = (b.Items, a.Items);
            (a.Count, b.Count) = (b.Count, a.Count);
        }
    }

    // Each pixel's best match: its distance in the high 20 bits, its length in the low 12 (VP8LHashChain)
    private sealed class HashChain
    {
        public uint[] OffsetLength = [];

        public void Init(
            int size)
        {
            if (this.OffsetLength.Length < size)
            {
                this.OffsetLength = new uint[size];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindOffset(
            int position) => (int)(this.OffsetLength[position] >> MaxLengthBits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FindLength(
            int position) => (int)(this.OffsetLength[position] & ((1u << MaxLengthBits) - 1));
    }

    // The color cache: the last color seen with each hash (VP8LColorCache)
    private struct ColorCache
    {
        public uint[] Colors;
        public int HashShift;

        public ColorCache(
            int hashBits)
        {
            this.Colors = new uint[1 << hashBits];
            this.HashShift = 32 - hashBits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Insert(
            uint argb) => this.Colors[HashPix(argb, this.HashShift)] = argb;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int GetIndex(
            uint argb) => HashPix(argb, this.HashShift);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int Contains(
            uint argb)
        {
            int key = HashPix(argb, this.HashShift);
            return (this.Colors[key] == argb) ? key : -1;
        }
    }

    private static ReadOnlySpan<byte> PlaneToCodeLut =>
    [
        96, 73, 55, 39, 23, 13, 5, 1, 255, 255, 255, 255, 255, 255, 255, 255,
        101, 78, 58, 42, 26, 16, 8, 2, 0, 3, 9, 17, 27, 43, 59, 79,
        102, 86, 62, 46, 32, 20, 10, 6, 4, 7, 11, 21, 33, 47, 63, 87,
        105, 90, 70, 52, 37, 28, 18, 14, 12, 15, 19, 29, 38, 53, 71, 91,
        110, 99, 82, 66, 48, 35, 30, 24, 22, 25, 31, 36, 49, 67, 83, 100,
        115, 108, 94, 76, 64, 50, 44, 40, 34, 41, 45, 51, 65, 77, 95, 109,
        118, 113, 103, 92, 80, 68, 60, 56, 54, 57, 61, 69, 81, 93, 104, 114,
        119, 116, 111, 106, 97, 88, 84, 74, 72, 75, 85, 89, 98, 107, 112, 117,
    ];

    private static int DistanceToPlaneCode(
        int xsize,
        int distance)
    {
        int yoffset = distance / xsize;
        int xoffset = distance - (yoffset * xsize);
        if ((xoffset <= 8) && (yoffset < 8))
        {
            return PlaneToCodeLut[(yoffset * 16) + 8 - xoffset] + 1;
        }
        else if ((xoffset > xsize - 8) && (yoffset < 7))
        {
            return PlaneToCodeLut[((yoffset + 1) * 16) + 8 + (xsize - xoffset)] + 1;
        }

        return distance + 120;
    }

    // FindMatchLength: 0 unless the arrays match at bestLength, else how far they match
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindMatchLength(
        ReadOnlySpan<uint> argb,
        int position1,
        int position2,
        int bestLength,
        int maxLimit)
    {
        if (argb[position1 + bestLength] != argb[position2 + bestLength])
        {
            return 0;
        }

        return VectorMismatch(argb.Slice(position1), argb.Slice(position2), maxLimit);
    }

    //------------------------------------------------------------------------------
    // Hash chain

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetPixPairHash64(
        uint argb0,
        uint argb1)
    {
        uint key = argb1 * 0xc6a4a793u;
        key += argb0 * 0x5bd1e996u;
        return key >> (32 - HashBits);
    }

    private static int GetWindowSizeForHashChain(
        int quality,
        int xsize)
    {
        int maxWindowSize = (quality > 75) ? WindowSize : (quality > 50) ? (xsize << 8) : (quality > 25) ? (xsize << 6) : (xsize << 4);
        return (maxWindowSize > WindowSize) ? WindowSize : maxWindowSize;
    }

    [ThreadStatic]
    private static int[]? hashToFirstIndex;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void HashChainFill(
        HashChain p,
        int quality,
        ReadOnlySpan<uint> argb,
        int xsize,
        int ysize,
        bool lowEffort)
    {
        int size = xsize * ysize;
        int iterMax = 8 + ((quality * quality) / 128);
        uint windowSize = (uint)GetWindowSizeForHashChain(quality, xsize);
        var offsetLength = p.OffsetLength.AsSpan(0, size);
        if (size <= 2)
        {
            offsetLength[0] = 0;
            offsetLength[size - 1] = 0;
            return;
        }

        // The chain linking pixels with the same hash, in offsetLength for now. The positions below
        // stay inside the image: pos + 2 < size in the first loop, and the chain's are earlier ones.
        var chain = MemoryMarshal.Cast<uint, int>(offsetLength);
        ref int chainRef = ref MemoryMarshal.GetReference(chain);
        ref uint pixels = ref MemoryMarshal.GetReference(argb.Slice(0, size));
        var hashToFirst = hashToFirstIndex ??= new int[HashSize];
        MemoryMarshal.AsBytes(hashToFirst.AsSpan()).Fill(0xff);
        ref int firstRef = ref MemoryMarshal.GetArrayDataReference(hashToFirst);
        bool argbComp = pixels == Unsafe.Add(ref pixels, 1);
        int pos;
        for (pos = 0; pos < size - 2;)
        {
            uint hashCode;
            bool argbCompNext = Unsafe.Add(ref pixels, pos + 1) == Unsafe.Add(ref pixels, pos + 2);
            if (argbComp && argbCompNext)
            {
                // Pixels of the same color: hashed with how many there are
                uint color = Unsafe.Add(ref pixels, pos);
                uint length = 1;
                while ((pos + (int)length + 2 < size) && (Unsafe.Add(ref pixels, pos + (int)length + 2) == color))
                {
                    ++length;
                }

                if (length > MaxLength)
                {
                    MemoryMarshal.AsBytes(chain.Slice(pos, (int)(length - MaxLength))).Fill(0xff);
                    pos += (int)(length - MaxLength);
                    length = MaxLength;
                }

                while (length != 0)
                {
                    hashCode = GetPixPairHash64(color, length--);
                    ref int first = ref Unsafe.Add(ref firstRef, (nint)hashCode);
                    Unsafe.Add(ref chainRef, pos) = first;
                    first = pos++;
                }

                argbComp = false;
            }
            else
            {
                hashCode = GetPixPairHash64(Unsafe.Add(ref pixels, pos), Unsafe.Add(ref pixels, pos + 1));
                ref int first = ref Unsafe.Add(ref firstRef, (nint)hashCode);
                Unsafe.Add(ref chainRef, pos) = first;
                first = pos++;
                argbComp = argbCompNext;
            }
        }

        chain[pos] = hashToFirst[GetPixPairHash64(argb[pos], argb[pos + 1])];

        // The best match at each pixel, from the right
        offsetLength[0] = 0;
        offsetLength[size - 1] = 0;
        ref uint offsetLengthRef = ref MemoryMarshal.GetReference(offsetLength);
        for (uint basePosition = (uint)size - 2; basePosition > 0;)
        {
            int maxLen = Math.Min(size - 1 - (int)basePosition, MaxLength);
            int start = (int)basePosition;
            int iter = iterMax;
            int bestLength = 0;
            uint bestDistance = 0;
            int minPos = (basePosition > windowSize) ? (int)(basePosition - windowSize) : 0;
            int lengthMax = (maxLen < 256) ? maxLen : 256;
            ref uint startRef = ref Unsafe.Add(ref pixels, start);
            pos = Unsafe.Add(ref chainRef, start);
            if (!lowEffort)
            {
                int currentLength;
                if (basePosition >= (uint)xsize)
                {
                    currentLength = MatchLength(ref Unsafe.Subtract(ref startRef, xsize), ref startRef, bestLength, maxLen);
                    if (currentLength > bestLength)
                    {
                        bestLength = currentLength;
                        bestDistance = (uint)xsize;
                    }

                    --iter;
                }

                currentLength = MatchLength(ref Unsafe.Subtract(ref startRef, 1), ref startRef, bestLength, maxLen);
                if (currentLength > bestLength)
                {
                    bestLength = currentLength;
                    bestDistance = 1;
                }

                --iter;
                if (bestLength == MaxLength)
                {
                    pos = minPos - 1;
                }
            }

            uint bestArgb = Unsafe.Add(ref startRef, bestLength);
            for (; (pos >= minPos) && (--iter != 0); pos = Unsafe.Add(ref chainRef, pos))
            {
                ref uint candidate = ref Unsafe.Add(ref pixels, pos);
                if (Unsafe.Add(ref candidate, bestLength) != bestArgb)
                {
                    continue;
                }

                int currentLength = Mismatch(ref candidate, ref startRef, maxLen);
                if (bestLength < currentLength)
                {
                    bestLength = currentLength;
                    bestDistance = basePosition - (uint)pos;
                    bestArgb = Unsafe.Add(ref startRef, bestLength);
                    if (bestLength >= lengthMax)
                    {
                        break;
                    }
                }
            }

            // The match extended to the left while the intervals keep matching
            uint maxBasePosition = basePosition;
            while (true)
            {
                Unsafe.Add(ref offsetLengthRef, (nint)basePosition) = (bestDistance << MaxLengthBits) | (uint)bestLength;
                --basePosition;
                if ((bestDistance == 0) || (basePosition == 0))
                {
                    break;
                }

                if ((basePosition < bestDistance) || (Unsafe.Add(ref pixels, (nint)(basePosition - bestDistance)) != Unsafe.Add(ref pixels, (nint)basePosition)))
                {
                    break;
                }

                if ((bestLength == MaxLength) && (bestDistance != 1) && (basePosition + MaxLength < maxBasePosition))
                {
                    break;
                }

                if (bestLength < MaxLength)
                {
                    ++bestLength;
                    maxBasePosition = basePosition;
                }
            }
        }
    }

    // FindMatchLength by refs: 0 unless the pixels match at bestLength, else how far they match
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MatchLength(
        ref uint a,
        ref uint b,
        int bestLength,
        int maxLimit)
    {
        if (Unsafe.Add(ref a, bestLength) != Unsafe.Add(ref b, bestLength))
        {
            return 0;
        }

        return Mismatch(ref a, ref b, maxLimit);
    }

    // VectorMismatch by refs: how many of the first length pixels match, the first few one by one
    // as most matches are short
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mismatch(
        ref uint a,
        ref uint b,
        int length)
    {
        int k = 0;
        int scalar = (length < 4) ? length : 4;
        for (; k < scalar; k++)
        {
            if (Unsafe.Add(ref a, k) != Unsafe.Add(ref b, k))
            {
                return k;
            }
        }

        if (k == length)
        {
            return k;
        }

        return k + MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref a, k), length - k).CommonPrefixLength(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref b, k), length - k));
    }

    //------------------------------------------------------------------------------
    // LZ77
}
