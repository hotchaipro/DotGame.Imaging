// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/utils/huffman_utils.c and .h), modified by Hot Chai Games LLC: written in
// C#, the tables' segments one array that grows. libwebp carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

/// <summary>
/// A Huffman table entry: the bits the code takes, and its symbol or second-level offset. Read as one
/// little-endian word by the decoder: the bits in the low byte, the value in the high half.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 4)]
internal struct Vp8LHuffmanCode
{
    [FieldOffset(0)]
    public byte Bits;

    [FieldOffset(2)]
    public ushort Value;
}

/// <summary>A packed table entry: up to four literals' ARGB at once, or a non-literal's symbol.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Vp8LHuffmanCode32
{
    public int Bits;
    public uint Value;
}

/// <summary>
/// Huffman tables for VP8L's codes (libwebp's VP8LBuildHuffmanTable): a root table of
/// <c>rootBits</c> bits, and second-level tables for longer codes, all in one array.
/// </summary>
internal sealed class Vp8LHuffmanTables
{
    public const int TableBits = 8;
    public const int TableMask = (1 << TableBits) - 1;
    public const int LengthsTableBits = 7;
    public const int LengthsTableMask = (1 << LengthsTableBits) - 1;
    public const int MaxAllowedCodeLength = 15;

    public Vp8LHuffmanCode[] Codes;
    public int Used;

    private readonly int[] count = new int[MaxAllowedCodeLength + 1];
    private readonly int[] offset = new int[MaxAllowedCodeLength + 1];
    private ushort[] sorted = new ushort[512];

    public Vp8LHuffmanTables(
        int size)
    {
        // Every entry a table uses is written before it's read
        this.Codes = GC.AllocateUninitializedArray<Vp8LHuffmanCode>(Math.Max(size, 1));
    }

    /// <summary>
    /// Builds a table at <see cref="Used"/> (not moving it on), and gives its size, or 0 if the code
    /// lengths aren't a complete code. <paramref name="start"/> is where it is.
    /// </summary>
    public int Build(
        int rootBits,
        ReadOnlySpan<int> codeLengths,
        out int start)
    {
        start = this.Used;
        int totalSize = this.BuildTable(null, 0, rootBits, codeLengths);
        if (totalSize == 0)
        {
            return 0;
        }

        // Room for the table, and as much again (the size is the first pass's, as libwebp's is)
        if (this.Used + (2 * totalSize) >= this.Codes.Length)
        {
            Array.Resize(ref this.Codes, Math.Max(this.Codes.Length * 2, this.Used + (2 * totalSize) + 1));
        }

        if (this.sorted.Length < codeLengths.Length)
        {
            this.sorted = new ushort[codeLengths.Length];
        }

        this.BuildTable(this.Codes, this.Used, rootBits, codeLengths);
        return totalSize;
    }

    /// <summary>Whether the code lengths are a complete code (a table of them, built nowhere).</summary>
    public int Validate(
        int rootBits,
        ReadOnlySpan<int> codeLengths)
    {
        return this.BuildTable(null, 0, rootBits, codeLengths);
    }

    // libwebp's BuildHuffmanTable: with a null table, only the size, after checking the code
    private int BuildTable(
        Vp8LHuffmanCode[]? table,
        int rootStart,
        int rootBits,
        ReadOnlySpan<int> codeLengths)
    {
        var count = this.count;
        var offset = this.offset;
        var sorted = this.sorted;
        Array.Clear(count);
        int codeLengthsSize = codeLengths.Length;
        int totalSize = 1 << rootBits;

        for (int symbol = 0; symbol < codeLengthsSize; symbol++)
        {
            if (codeLengths[symbol] > MaxAllowedCodeLength)
            {
                return 0;
            }

            count[codeLengths[symbol]]++;
        }

        if (count[0] == codeLengthsSize)
        {
            return 0;
        }

        offset[1] = 0;
        for (int len = 1; len < MaxAllowedCodeLength; len++)
        {
            if (count[len] > (1 << len))
            {
                return 0;
            }

            offset[len + 1] = offset[len] + count[len];
        }

        for (int symbol = 0; symbol < codeLengthsSize; symbol++)
        {
            int length = codeLengths[symbol];
            if (length > 0)
            {
                if (table is not null)
                {
                    if (offset[length] >= codeLengthsSize)
                    {
                        return 0;
                    }

                    sorted[offset[length]++] = (ushort)symbol;
                }
                else
                {
                    offset[length]++;
                }
            }
        }

        // One symbol: a zero-length code
        if (offset[MaxAllowedCodeLength] == 1)
        {
            if (table is not null)
            {
                var code = new Vp8LHuffmanCode { Bits = 0, Value = sorted[0] };
                table.AsSpan(rootStart, totalSize).Fill(code);
            }

            return totalSize;
        }

        uint low = 0xffffffff;
        uint mask = (uint)totalSize - 1;
        uint key = 0;
        int numNodes = 1;
        int numOpen = 1;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int tableStart = rootStart;
        int next = 0;

        int step = 2;
        for (int len = 1; len <= rootBits; len++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0)
            {
                return 0;
            }

            if (table is null)
            {
                continue;
            }

            for (; count[len] > 0; count[len]--)
            {
                var code = new Vp8LHuffmanCode { Bits = (byte)len, Value = sorted[next++] };
                Replicate(table, tableStart + (int)key, step, tableSize, code);
                key = NextKey(key, len);
            }
        }

        step = 2;
        for (int len = rootBits + 1; len <= MaxAllowedCodeLength; len++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0)
            {
                return 0;
            }

            for (; count[len] > 0; count[len]--)
            {
                if ((key & mask) != low)
                {
                    if (table is not null)
                    {
                        tableStart += tableSize;
                    }

                    tableBits = NextTableBitSize(count, len, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    low = key & mask;
                    if (table is not null)
                    {
                        table[rootStart + (int)low].Bits = (byte)(tableBits + rootBits);
                        table[rootStart + (int)low].Value = (ushort)(tableStart - rootStart - (int)low);
                    }
                }

                if (table is not null)
                {
                    var code = new Vp8LHuffmanCode { Bits = (byte)(len - rootBits), Value = sorted[next++] };
                    Replicate(table, tableStart + (int)(key >> rootBits), step, tableSize, code);
                }

                key = NextKey(key, len);
            }
        }

        if (numNodes != (2 * offset[MaxAllowedCodeLength]) - 1)
        {
            return 0;
        }

        return totalSize;
    }

    // The next key in bit-reversed order
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NextKey(
        uint key,
        int len)
    {
        uint step = 1u << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return (step != 0) ? ((key & (step - 1)) + step) : key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Replicate(
        Vp8LHuffmanCode[] table,
        int start,
        int step,
        int end,
        Vp8LHuffmanCode code)
    {
        // In range: the caller's tables were sized for the code
        ref var first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), start);
        do
        {
            end -= step;
            Unsafe.Add(ref first, end) = code;
        }
        while (end > 0);
    }

    private static int NextTableBitSize(
        int[] count,
        int len,
        int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < MaxAllowedCodeLength)
        {
            left -= count[len];
            if (left <= 0)
            {
                break;
            }

            len++;
            left <<= 1;
        }

        return len - rootBits;
    }
}
