// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/vp8l_dec.c: DecodeImageData), modified by Hot Chai Games LLC:
// written in C#, the pixels far from the stream's end decoded by a loop that needs no slow paths.
// libwebp carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LDecoder
{
    // Where a pixel loop is: its reader, and the pixel, row and column it's at
    private struct LoopState
    {
        public Vp8LBitReader Reader;
        public int Src;
        public int Row;
        public int Col;
        public int LastCached;
        public int GroupIndex;
    }

    // The most bytes an iteration of the fast loop reads: two fills of 4, and two reads of extra bits
    // that shift in up to 8 each
    private const int FastLoopMargin = 32;

    private sealed partial class Decoding
    {
        // DecodeImageData's loop while more than FastLoopMargin bytes are left: every fill and read
        // of extra bits takes its fast path, and none reaches the end of the stream, so the loop
        // calls nothing and checks for no end (libwebp's checks are all false here). It stops at the
        // margin, or at the last pixel, and the careful loop goes on from where it stopped.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private LoopState DecodeFast(
            uint[] image,
            int w,
            int srcEnd,
            int srcLast,
            LoopState state)
        {
            ref byte input = ref MemoryMarshal.GetArrayDataReference(data);
            var meta = this.hdr;
            ref var codes = ref MemoryMarshal.GetArrayDataReference(meta.Tables.Codes);
            ref var packed = ref MemoryMarshal.GetArrayDataReference(meta.Packed);
            ref var groups = ref MemoryMarshal.GetArrayDataReference(meta.Groups);
            ref uint pixels = ref MemoryMarshal.GetArrayDataReference(image);
            uint[]? cache = (meta.ColorCacheSize > 0) ? meta.ColorCache : null;
            int cacheShift = meta.ColorCacheShift;
            int mask = meta.HuffmanMask;
            const int lengthCodeLimit = NumLiteralCodes + NumLengthCodes;
            int colorCacheLimit = lengthCodeLimit + meta.ColorCacheSize;
            int fastEnd = state.Reader.Length - FastLoopMargin;

            ulong value = state.Reader.Value;
            int bitPos = state.Reader.BitPos;
            int pos = state.Reader.Pos;
            int src = state.Src;
            int row = state.Row;
            int col = state.Col;
            int lastCached = state.LastCached;
            int groupIndex = state.GroupIndex;
            while ((src < srcLast) && (pos < fastEnd))
            {
                if ((col & mask) == 0)
                {
                    groupIndex = MetaIndex(meta, col, row);
                }

                ref var group = ref Unsafe.Add(ref groups, groupIndex);
                if (group.IsTrivialCode)
                {
                    Unsafe.Add(ref pixels, src) = group.LiteralArb;
                }
                else
                {
                    Fill(ref input, ref value, ref bitPos, ref pos);
                    int code;
                    if (group.UsePackedTable)
                    {
                        ref var packedEntry = ref Unsafe.Add(ref packed, (groupIndex * PackedTableSize) + (int)((uint)(value >> bitPos) & (PackedTableSize - 1)));
                        int entryBits = packedEntry.Bits;
                        uint entryValue = packedEntry.Value;
                        if (entryBits < BitsSpecialMarker)
                        {
                            bitPos += entryBits;
                            Unsafe.Add(ref pixels, src) = entryValue;
                            goto AdvanceByOne;
                        }

                        bitPos += entryBits - BitsSpecialMarker;
                        code = (int)entryValue;
                    }
                    else
                    {
                        code = Symbol(ref codes, group.Green, value, ref bitPos);
                    }

                    if (code < NumLiteralCodes)
                    {
                        if (group.IsTrivialLiteral)
                        {
                            Unsafe.Add(ref pixels, src) = group.LiteralArb | ((uint)code << 8);
                        }
                        else
                        {
                            uint red = (uint)Symbol(ref codes, group.Red, value, ref bitPos);
                            Fill(ref input, ref value, ref bitPos, ref pos);
                            uint blue = (uint)Symbol(ref codes, group.Blue, value, ref bitPos);
                            uint alpha = (uint)Symbol(ref codes, group.Alpha, value, ref bitPos);
                            Unsafe.Add(ref pixels, src) = (alpha << 24) | (red << 16) | ((uint)code << 8) | blue;
                        }
                    }
                    else if (code < lengthCodeLimit)
                    {
                        // A copy of earlier pixels
                        int length = Distance(code - NumLiteralCodes, ref input, ref value, ref bitPos, ref pos);
                        int distSymbol = Symbol(ref codes, group.Dist, value, ref bitPos);
                        Fill(ref input, ref value, ref bitPos, ref pos);
                        int distCode = Distance(distSymbol, ref input, ref value, ref bitPos, ref pos);
                        int dist = PlaneCodeToDistance(w, distCode);
                        if ((src < dist) || (srcEnd - src < length))
                        {
                            throw Damaged();
                        }

                        CopyPixels(ref Unsafe.Add(ref pixels, src), dist, length);
                        src += length;
                        col += length;
                        while (col >= w)
                        {
                            col -= w;
                            row++;
                        }

                        if ((col & mask) != 0)
                        {
                            groupIndex = MetaIndex(meta, col, row);
                        }

                        if (cache is not null)
                        {
                            InsertIntoCache(cache, cacheShift, ref pixels, ref lastCached, src);
                        }

                        continue;
                    }
                    else if (code < colorCacheLimit)
                    {
                        InsertIntoCache(cache!, cacheShift, ref pixels, ref lastCached, src);
                        Unsafe.Add(ref pixels, src) = cache![code - lengthCodeLimit];
                    }
                    else
                    {
                        throw Damaged();
                    }
                }

            AdvanceByOne:
                src++;
                col++;
                if (col >= w)
                {
                    col = 0;
                    row++;
                    if (cache is not null)
                    {
                        InsertIntoCache(cache, cacheShift, ref pixels, ref lastCached, src);
                    }
                }
            }

            state.Reader.Value = value;
            state.Reader.BitPos = bitPos;
            state.Reader.Pos = pos;
            state.Src = src;
            state.Row = row;
            state.Col = col;
            state.LastCached = lastCached;
            state.GroupIndex = groupIndex;
            return state;
        }

        // The fast loop's fill: 32 bits more when 32 are used (eight bytes or more are left)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Fill(
            ref byte input,
            ref ulong value,
            ref int bitPos,
            ref int pos)
        {
            if (bitPos >= 32)
            {
                value = (value >> 32) | ((ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input, pos)) << 32);
                bitPos -= 32;
                pos += 4;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Symbol(
            ref Vp8LHuffmanCode codes,
            int table,
            ulong value,
            ref int bitPos)
        {
            uint bits = (uint)(value >> bitPos);
            int index = table + (int)(bits & Vp8LHuffmanTables.TableMask);
            uint entry = Unsafe.As<Vp8LHuffmanCode, uint>(ref Unsafe.Add(ref codes, index));
            int extra = (int)(entry & 0xff) - Vp8LHuffmanTables.TableBits;
            if (extra > 0)
            {
                bitPos += Vp8LHuffmanTables.TableBits;
                bits = (uint)(value >> bitPos);
                index += (int)(entry >> 16) + (int)(bits & ((1u << extra) - 1));
                entry = Unsafe.As<Vp8LHuffmanCode, uint>(ref Unsafe.Add(ref codes, index));
            }

            bitPos += (int)(entry & 0xff);
            return (int)(entry >> 16);
        }

        // A length or distance and its extra bits, read and the bytes they used shifted out at once
        // (as libwebp's ShiftBytes does a byte at a time)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Distance(
            int symbol,
            ref byte input,
            ref ulong value,
            ref int bitPos,
            ref int pos)
        {
            if (symbol < 4)
            {
                return symbol + 1;
            }

            int extraBits = (symbol - 2) >> 1;
            int offset = (2 + (symbol & 1)) << extraBits;
            uint extra = (uint)(value >> bitPos) & ((1u << extraBits) - 1);
            bitPos += extraBits;
            int shift = bitPos >> 3;
            if (shift != 0)
            {
                ulong next = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, pos));
                value = ((value >> ((8 * shift) - 1)) >> 1) | (next << (64 - (8 * shift)));
                pos += shift;
                bitPos -= 8 * shift;
            }

            return offset + (int)extra + 1;
        }

        // A copy of earlier pixels with no calls: four at a time where the copy doesn't overlap
        // within them, one pixel repeated, and the shortest distances a pixel at a time
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyPixels(
            ref uint target,
            int dist,
            int length)
        {
            ref uint source = ref Unsafe.Subtract(ref target, dist);
            int i = 0;
            if (dist >= 16)
            {
                // Sixteen pixels at a time, none of which the same step writes
                for (; i + 16 <= length; i += 16)
                {
                    var a = Vector128.LoadUnsafe(ref source, (nuint)i);
                    var b = Vector128.LoadUnsafe(ref source, (nuint)(i + 4));
                    var c = Vector128.LoadUnsafe(ref source, (nuint)(i + 8));
                    var e = Vector128.LoadUnsafe(ref source, (nuint)(i + 12));
                    a.StoreUnsafe(ref target, (nuint)i);
                    b.StoreUnsafe(ref target, (nuint)(i + 4));
                    c.StoreUnsafe(ref target, (nuint)(i + 8));
                    e.StoreUnsafe(ref target, (nuint)(i + 12));
                }
            }

            if (dist >= 4)
            {
                for (; i + 4 <= length; i += 4)
                {
                    Vector128.LoadUnsafe(ref source, (nuint)i).StoreUnsafe(ref target, (nuint)i);
                }
            }
            else if (dist == 1)
            {
                var repeated = Vector128.Create(source);
                for (; i + 16 <= length; i += 16)
                {
                    repeated.StoreUnsafe(ref target, (nuint)i);
                    repeated.StoreUnsafe(ref target, (nuint)(i + 4));
                    repeated.StoreUnsafe(ref target, (nuint)(i + 8));
                    repeated.StoreUnsafe(ref target, (nuint)(i + 12));
                }

                for (; i + 4 <= length; i += 4)
                {
                    repeated.StoreUnsafe(ref target, (nuint)i);
                }
            }

            for (; i < length; i++)
            {
                Unsafe.Add(ref target, i) = Unsafe.Add(ref source, i);
            }
        }
    }
}
