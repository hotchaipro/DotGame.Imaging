// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Part of the JPEG decoder (JpegDecoder.cs). The decoder's port notes and notices:
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, the whole image decoded
// at once and output a row at a time, and the Huffman decoding, vector forms and buffer handling new.
//
// The inverse DCT's arithmetic, the upsampling, the color conversion, the range limiting, the AC
// refinement, the color space guess, the block smoothing rule and the context rows follow jidctint.c,
// jdsample.c, jdcolor.c, jdcolext.c, jdmaster.c, jdphuff.c, jdhuff.c, jdmainct.c, jdcoefct.c and
// jdapimin.c, which carry these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1998, Thomas G. Lane.
//   Modified 2002-2018 by Guido Vollbeding.
//   Lossless JPEG Modifications: Copyright (C) 1999, Ken Murchison.
//   libjpeg-turbo Modifications:
//   Copyright 2009 Pierre Ossman <ossman@cendio.se> for Cendio AB
//   Copyright (C) 2009-2026, D. R. Commander.
//   Copyright (C) 2013, Linaro Limited.
//   Copyright (C) 2015, 2020, Google, Inc.
//   Copyright (C) 2018, Matthias Räncker.
//   Copyright (C) 2019-2020, Arm Limited.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.
//
// The Arm64 inverse DCT is ported from simd/arm/jidctint-neon.c, which carries this notice:
//   Copyright (C) 2020, Arm Limited.
//   Copyright (C) 2020, 2024-2025, D. R. Commander.
//
//   This software is provided 'as-is', without any express or implied
//   warranty.  In no event will the authors be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//   3. This notice may not be removed or altered from any source distribution.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.Jpeg;

internal static partial class JpegDecoder
{
    private sealed partial class Decoding
    {
        // Reads a scan's header and its entropy-coded data; returns the position of the next marker
        private int ReadScan(
            ReadOnlySpan<byte> header,
            int dataStart)
        {
            int count = (header.Length >= 1) ? header[0] : 0;
            if ((count < 1) || (count > 4) || (header.Length < 4 + (2 * count)))
            {
                throw new InvalidDataException("A JPEG scan header is malformed.");
            }

            var scan = new Component[count];
            for (int i = 0; i < count; i++)
            {
                int id = header[1 + (2 * i)];
                var c = Array.Find(this.components, x => x.Id == id) ?? throw new InvalidDataException("A JPEG scan names a component the frame hasn't.");
                c.DcTable = header[2 + (2 * i)] >> 4;
                c.AcTable = header[2 + (2 * i)] & 15;
                scan[i] = c;

                // libjpeg latches a component's quantization table at its first scan
                c.Quant ??= Array.ConvertAll(this.quantTables[c.QuantTable] ?? throw new InvalidDataException("A JPEG component's quantization table is missing."), q => (short)q);
            }

            int spectralStart = header[1 + (2 * count)];
            int spectralEnd = header[2 + (2 * count)];
            int successiveHigh = header[3 + (2 * count)] >> 4;
            int successiveLow = header[3 + (2 * count)] & 15;
            if (!this.progressive)
            {
                spectralStart = 0;
                spectralEnd = 63;
                successiveHigh = 0;
                successiveLow = 0;
            }
            else if ((spectralEnd > 63) || (spectralStart > spectralEnd) || ((spectralStart > 0) && (count != 1)) || ((spectralStart == 0) && (spectralEnd != 0)) || (successiveLow > 13))
            {
                throw new InvalidDataException("A JPEG progressive scan's parameters are invalid.");
            }

            foreach (var c in scan)
            {
                if ((this.progressive ? spectralStart == 0 : true) && (this.dcTables[c.DcTable] is null) && (successiveHigh == 0))
                {
                    throw new InvalidDataException("A JPEG scan's DC Huffman table is missing.");
                }

                if ((spectralEnd > 0) && (this.acTables[c.AcTable] is null))
                {
                    throw new InvalidDataException("A JPEG scan's AC Huffman table is missing.");
                }

                c.DcPredictor = 0;
                for (int k = spectralStart; k <= spectralEnd; k++)
                {
                    c.CoefficientBits[k] = successiveLow;
                }
            }

            this.eobRun = 0;
            this.reader.Reset(dataStart);
            int restartsLeft = this.restartInterval;
            int totalMcus = (count == 1) ? scan[0].WidthInBlocks * scan[0].HeightInBlocks : this.mcusPerLine * this.mcusPerColumn;
            if (!this.progressive)
            {
                this.DecodeBaselineScan(scan);
            }
            else
            {
                this.DecodeProgressiveScan(scan, spectralStart, spectralEnd, successiveHigh, successiveLow);
            }

            totalMcus = 0;

            for (int mcu = 0; mcu < totalMcus; mcu++)
            {
                if (this.restartInterval > 0)
                {
                    if (restartsLeft == 0)
                    {
                        this.Restart(scan);
                        restartsLeft = this.restartInterval;
                    }

                    restartsLeft--;
                }

                if (count == 1)
                {
                    var c = scan[0];
                    int row = mcu / c.WidthInBlocks;
                    int column = mcu % c.WidthInBlocks;
                    this.DecodeBlock(c, (row * c.BlocksPerLine) + column, spectralStart, spectralEnd, successiveHigh, successiveLow);
                }
                else
                {
                    int mcuRow = mcu / this.mcusPerLine;
                    int mcuColumn = mcu % this.mcusPerLine;
                    foreach (var c in scan)
                    {
                        for (int v = 0; v < c.V; v++)
                        {
                            for (int h = 0; h < c.H; h++)
                            {
                                int block = ((((mcuRow * c.V) + v) * c.BlocksPerLine) + (mcuColumn * c.H)) + h;
                                this.DecodeBlock(c, block, spectralStart, spectralEnd, successiveHigh, successiveLow);
                            }
                        }
                    }
                }
            }

            // The next marker: past the scan's data
            int position = this.reader.Position;
            while ((position + 1 < this.data.Length) && !((this.data[position] == 0xFF) && (this.data[position + 1] != 0) && ((this.data[position + 1] < 0xD0) || (this.data[position + 1] > 0xD7))))
            {
                position++;
            }

            return position;
        }

        // A sequential scan: each block decoded with the bits in locals, then straight through the
        // IDCT into its component's plane
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void DecodeBaselineScan(
            Component[] scan)
        {
            ulong bits = 0;
            int count = 0;
            int position = this.reader.Position;
            bool atMarker = false;
            Span<short> block = stackalloc short[64];
            block.Clear();
            int restartsLeft = this.restartInterval;
            bool single = scan.Length == 1;
            int rows = single ? scan[0].HeightInBlocks : this.mcusPerColumn;
            int columns = single ? scan[0].WidthInBlocks : this.mcusPerLine;
            for (int mcuRow = 0; mcuRow < rows; mcuRow++)
            {
                for (int mcuColumn = 0; mcuColumn < columns; mcuColumn++)
                {
                    if (this.restartInterval > 0)
                    {
                        if (restartsLeft == 0)
                        {
                            this.reader.Position = position;
                            this.Restart(scan);
                            position = this.reader.Position;
                            bits = 0;
                            count = 0;
                            atMarker = false;
                            restartsLeft = this.restartInterval;
                        }

                        restartsLeft--;
                    }

                    if (single)
                    {
                        var c = scan[0];
                        DecodeBaselineBlock(ref bits, ref count, ref position, ref atMarker, this.data, this.dcTables[c.DcTable]!, this.acTables[c.AcTable]!, ref c.DcPredictor, block);
                        Idct.Islow(block, c.Quant!, c.Plane.AsSpan((mcuRow * 8 * c.Stride) + (mcuColumn * 8)), c.Stride);
                        block.Clear();
                        continue;
                    }

                    foreach (var c in scan)
                    {
                        var dc = this.dcTables[c.DcTable]!;
                        var ac = this.acTables[c.AcTable]!;
                        for (int v = 0; v < c.V; v++)
                        {
                            int row = (mcuRow * c.V) + v;
                            for (int h = 0; h < c.H; h++)
                            {
                                DecodeBaselineBlock(ref bits, ref count, ref position, ref atMarker, this.data, dc, ac, ref c.DcPredictor, block);
                                Idct.Islow(block, c.Quant!, c.Plane.AsSpan((row * 8 * c.Stride) + (((mcuColumn * c.H) + h) * 8)), c.Stride);
                                block.Clear();
                            }
                        }
                    }
                }
            }

            this.reader.Position = position;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DecodeBaselineBlock(
            ref ulong bits,
            ref int count,
            ref int position,
            ref bool atMarker,
            byte[] data,
            HuffmanTable dc,
            HuffmanTable ac,
            ref int predictor,
            Span<short> block)
        {
            ref short coefficients = ref MemoryMarshal.GetReference(block);
            ref int natural = ref MemoryMarshal.GetArrayDataReference(NaturalOrder);
            if (count < 32)
            {
                Refill(ref bits, ref count, ref position, ref atMarker, data);
            }

            int t = DecodeSymbol(ref bits, ref count, dc);
            predictor += Receive(ref bits, ref count, t);
            coefficients = (short)predictor;

            ref int fastAc = ref MemoryMarshal.GetArrayDataReference(ac.FastAc);
            for (int k = 1; k < 64; k++)
            {
                if (count < 32)
                {
                    Refill(ref bits, ref count, ref position, ref atMarker, data);
                }

                int fast = Unsafe.Add(ref fastAc, (int)(bits >> (64 - HuffmanTable.LookupBits)));
                if (fast != 0)
                {
                    int length = fast & 0xFF;
                    bits <<= length;
                    count -= length;
                    if ((fast & HuffmanTable.EndOfBlock) != 0)
                    {
                        break;
                    }

                    k += (fast >> 8) & 15;
                    Unsafe.Add(ref coefficients, Unsafe.Add(ref natural, k)) = (short)(fast >> 16);
                    continue;
                }

                int rs = DecodeSymbol(ref bits, ref count, ac);
                int run = rs >> 4;
                int size = rs & 15;
                if (size != 0)
                {
                    k += run;
                    Unsafe.Add(ref coefficients, Unsafe.Add(ref natural, k)) = (short)Receive(ref bits, ref count, size);
                }
                else
                {
                    if (run != 15)
                    {
                        break;
                    }

                    k += 15;
                }
            }
        }

        // A Huffman code's symbol, from at least 16 bits in the buffer
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DecodeSymbol(
            ref ulong bits,
            ref int count,
            HuffmanTable table)
        {
            int entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table.Lookup), (int)(bits >> (64 - HuffmanTable.LookupBits)));
            if (entry != 0)
            {
                int length = entry >> 8;
                bits <<= length;
                count -= length;
                return entry & 0xFF;
            }

            int found = DecodeLongSymbol(bits, table);
            int longLength = found >> 8;
            bits <<= longLength;
            count -= longLength;
            return found & 0xFF;
        }

        // A code longer than the lookup: (its length << 8) | its symbol. Takes and returns values,
        // so the caller's bit buffer stays in registers.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int DecodeLongSymbol(
            ulong bits,
            HuffmanTable table)
        {
            int look = (int)(bits >> 48);
            int length = HuffmanTable.LookupBits + 1;
            int code = look >> (16 - length);
            while (code > table.MaxCode[length])
            {
                length++;
                if (length > 16)
                {
                    // A code no table has: damaged data, read as 0 (libjpeg warns and goes on)
                    return 16 << 8;
                }

                code = look >> (16 - length);
            }

            return (length << 8) | table.Symbols[(code + table.ValueOffset[length]) & 0xFF];
        }

        // n bits as T.81's EXTEND: the upper half positive, the lower negative
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Receive(
            ref ulong bits,
            ref int count,
            int n)
        {
            if (n == 0)
            {
                return 0;
            }

            int value = (int)(bits >> (64 - n));
            bits <<= n;
            count -= n;
            return value + (((value - (1 << (n - 1))) >> 31) & ((-1 << n) + 1));
        }

        // At least 32 bits in the buffer: eight bytes at once when none of them is 0xFF (the bits
        // past the whole bytes taken are the next byte's, the same ones the next refill puts there),
        // otherwise a byte at a time with T.81's stuffing, and zeros from a marker on
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Refill(
            ref ulong bits,
            ref int count,
            ref int position,
            ref bool atMarker,
            byte[] data)
        {
            if (!atMarker && (position + 8 <= data.Length))
            {
                ulong word = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), position)));
                ulong inverted = ~word;
                if (((inverted - 0x0101010101010101UL) & ~inverted & 0x8080808080808080UL) == 0)
                {
                    bits |= word >> count;
                    int taken = (63 - count) >> 3;
                    position += taken;
                    count += taken * 8;
                    return;
                }
            }

            (bits, count, position, atMarker) = RefillSlowly(bits, count, position, atMarker, data);
        }

        // A byte at a time, by value (see DecodeLongSymbol)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static (ulong Bits, int Count, int Position, bool AtMarker) RefillSlowly(
            ulong bits,
            int count,
            int position,
            bool atMarker,
            byte[] data)
        {
            while (count <= 56)
            {
                ulong b = 0;
                if (!atMarker && (position < data.Length))
                {
                    b = data[position];
                    if (b == 0xFF)
                    {
                        int next = (position + 1 < data.Length) ? data[position + 1] : 0xD9;
                        if (next == 0)
                        {
                            position += 2;
                        }
                        else
                        {
                            atMarker = true;
                            b = 0;
                        }
                    }
                    else
                    {
                        position++;
                    }
                }

                // Whole bytes only from here: clear what a fast refill left past them
                bits &= (count == 0) ? 0 : ~(ulong.MaxValue >> count);
                bits |= b << (56 - count);
                count += 8;
            }

            return (bits, count, position, atMarker);
        }
    }
}
