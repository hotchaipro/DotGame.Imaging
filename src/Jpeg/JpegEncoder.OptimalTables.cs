// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, the runs found from
// a bit mask of the nonzero coefficients.
//
// The symbol statistics and the optimal tables follow jchuff.c, which carries this notice:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1997, Thomas G. Lane.
//   Lossless JPEG Modifications: Copyright (C) 1999, Ken Murchison.
//   libjpeg-turbo Modifications:
//   Copyright (C) 2009-2011, 2014-2016, 2018-2026, D. R. Commander.
//   Copyright (C) 2015, Matthieu Darbois.
//   Copyright (C) 2018, Matthias Räncker.
//   Copyright (C) 2020, Arm Limited.
//   Copyright (C) 2022, Felix Hanau.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.

using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // encode_mcu_gather over the whole scan: the symbols each table would code, then its optimal table
    private void GatherSequential(
        Scan scan)
    {
        foreach (var c in scan.Components)
        {
            Array.Clear(this.dcCounts[c.DcTable]);
            Array.Clear(this.acCounts[c.AcTable]);
        }

        Span<int> predictions = stackalloc int[4];
        predictions.Clear();
        int togo = scan.RestartInterval;
        for (int row = 0; row < scan.McuRows; row++)
        {
            for (int column = 0; column < scan.McusPerRow; column++)
            {
                if (scan.RestartInterval != 0)
                {
                    if (togo == 0)
                    {
                        predictions.Clear();
                        togo = scan.RestartInterval;
                    }

                    togo--;
                }

                for (int b = 0; b < scan.BlocksInMcu; b++)
                {
                    int ci = scan.BlockComponent[b];
                    var c = scan.Components[ci];
                    var block = c.Coefficients.AsSpan(McuOrigin(scan, c, column, row) + scan.BlockOffset[b], 64);
                    CountBlock(block, predictions[ci], this.dcCounts[c.DcTable], this.acCounts[c.AcTable]);
                    predictions[ci] = block[0];
                }
            }
        }

        // finish_pass_gather: each table once
        Span<bool> dcDone = stackalloc bool[2];
        Span<bool> acDone = stackalloc bool[2];
        dcDone.Clear();
        acDone.Clear();
        foreach (var c in scan.Components)
        {
            if (!dcDone[c.DcTable])
            {
                this.dcTables[c.DcTable] = OptimalTable(this.dcCounts[c.DcTable]);
                dcDone[c.DcTable] = true;
            }

            if (!acDone[c.AcTable])
            {
                this.acTables[c.AcTable] = OptimalTable(this.acCounts[c.AcTable]);
                acDone[c.AcTable] = true;
            }
        }
    }

    // htest_one_block
    private static void CountBlock(
        ReadOnlySpan<short> block,
        int lastDc,
        long[] dcCounts,
        long[] acCounts)
    {
        int temp = block[0] - lastDc;
        dcCounts[32 - BitOperations.LeadingZeroCount((uint)Math.Abs(temp))]++;
        ulong nonZero = NonZeroMask(ref MemoryMarshal.GetReference(block)) & ~1UL;
        int last = 0;
        while (nonZero != 0)
        {
            int k = BitOperations.TrailingZeroCount(nonZero);
            nonZero &= nonZero - 1;
            int run = k - last - 1;
            last = k;
            while (run > 15)
            {
                acCounts[0xF0]++;
                run -= 16;
            }

            acCounts[(run << 4) + 32 - BitOperations.LeadingZeroCount((uint)Math.Abs((int)block[k]))]++;
        }

        if (last != 63)
        {
            acCounts[0]++;
        }
    }

    // jpeg_gen_optimal_table: the Huffman code of the counts (the JPEG standard's K.2), its lengths
    // limited to 16 bits, with the reserved symbol 256 keeping any code from being all ones
    private static HuffmanTable OptimalTable(
        long[] counts)
    {
        const int MaxLength = 32;
        Span<byte> bits = stackalloc byte[MaxLength + 2];
        Span<int> bitPosition = stackalloc int[MaxLength + 1];
        Span<int> codeSize = stackalloc int[257];
        Span<int> nonZeroIndex = stackalloc int[257];
        Span<int> others = stackalloc int[257];
        Span<long> freq = stackalloc long[257];
        bits.Clear();
        codeSize.Clear();
        others.Fill(-1);
        counts.CopyTo(freq);
        freq[256] = 1;

        int symbols = 0;
        for (int i = 0; i < 257; i++)
        {
            if (freq[i] != 0)
            {
                nonZeroIndex[symbols] = i;
                freq[symbols] = freq[i];
                symbols++;
            }
        }

        while (true)
        {
            // The two least frequent, the larger index first among equals
            int c1 = -1;
            int c2 = -1;
            long v = 1000000000L;
            long v2 = 1000000000L;
            for (int i = 0; i < symbols; i++)
            {
                if (freq[i] <= v2)
                {
                    if (freq[i] <= v)
                    {
                        c2 = c1;
                        v2 = v;
                        v = freq[i];
                        c1 = i;
                    }
                    else
                    {
                        v2 = freq[i];
                        c2 = i;
                    }
                }
            }

            if (c2 < 0)
            {
                break;
            }

            freq[c1] += freq[c2];
            freq[c2] = 1000000001L;
            codeSize[c1]++;
            while (others[c1] >= 0)
            {
                c1 = others[c1];
                codeSize[c1]++;
            }

            others[c1] = c2;
            codeSize[c2]++;
            while (others[c2] >= 0)
            {
                c2 = others[c2];
                codeSize[c2]++;
            }
        }

        for (int i = 0; i < symbols; i++)
        {
            bits[codeSize[i]]++;
        }

        int p = 0;
        for (int i = 1; i <= MaxLength; i++)
        {
            bitPosition[i] = p;
            p += bits[i];
        }

        // Codes longer than 16 bits moved up the tree (K.3's Adjust_BITS)
        int length;
        for (length = MaxLength; length > 16; length--)
        {
            while (bits[length] > 0)
            {
                int j = length - 2;
                while (bits[j] == 0)
                {
                    j--;
                }

                bits[length] -= 2;
                bits[length - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        // The reserved symbol's code, the longest, removed
        while (bits[length] == 0)
        {
            length--;
        }

        bits[length]--;

        var values = new byte[256];
        for (int i = 0; i < symbols - 1; i++)
        {
            values[bitPosition[codeSize[i]]] = (byte)nonZeroIndex[i];
            bitPosition[codeSize[i]]++;
        }

        return new HuffmanTable(bits[..17], values);
    }

    /// <summary>A Huffman table: code counts by length (index 1 to 16) and symbols.</summary>
    private sealed class HuffmanTable
    {
        public HuffmanTable(
            ReadOnlySpan<byte> bits,
            ReadOnlySpan<byte> values)
        {
            this.Bits = bits.ToArray();
            this.Values = new byte[256];
            values.CopyTo(this.Values);
        }

        public byte[] Bits { get; }

        public byte[] Values { get; }

        // Whether a DHT marker has sent it
        public bool Sent { get; set; }
    }
}
