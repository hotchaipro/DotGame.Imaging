// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, with vector forms new.
//
// The standard tables, the code tables, the block coding, the 64-bit bit buffer and its flushing, the
// restarts, the statistics and the optimal tables follow jstdhuff.c and jchuff.c, which carry these
// notices:
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
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // jstdhuff.c's tables (the JPEG standard's K.3): code counts by length, then the symbols
    private static ReadOnlySpan<byte> DcLuminanceBits => [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];

    private static ReadOnlySpan<byte> DcChrominanceBits => [0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];

    private static ReadOnlySpan<byte> DcValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static ReadOnlySpan<byte> AcLuminanceBits => [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];

    private static ReadOnlySpan<byte> AcLuminanceValues =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private static ReadOnlySpan<byte> AcChrominanceBits => [0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static ReadOnlySpan<byte> AcChrominanceValues =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    // Each table's codes by symbol (jpeg_make_c_derived_tbl): the code in the low 16 bits, its length
    // above them, or 0 for a symbol the table hasn't
    private readonly uint[][] dcCodes = new uint[2][];
    private readonly uint[][] acCodes = new uint[2][];

    // The same, packed for the sequential coder (PackCodes)
    private readonly ulong[][] dcPacked = new ulong[2][];
    private readonly ulong[][] acPacked = new ulong[2][];

    // Each table's symbol counts, 257 of them (the last for jpeg_gen_optimal_table's reserved symbol)
    private readonly long[][] dcCounts = [new long[257], new long[257]];
    private readonly long[][] acCounts = [new long[257], new long[257]];

    // The bit buffer (libjpeg-turbo's 64-bit one): bits written from the top as they fill it
    private ulong bitBuffer;
    private int freeBits = 64;

    // The scan's DC predictions, by component in the scan, and its restart state
    private readonly int[] lastDc = new int[4];
    private int restartsToGo;
    private int nextRestart;
    private int restartInterval;

    // std_huff_tables
    private void StandardHuffmanTables()
    {
        this.dcTables[0] = new HuffmanTable(DcLuminanceBits, DcValues);
        this.acTables[0] = new HuffmanTable(AcLuminanceBits, AcLuminanceValues);
        if (this.components.Length > 1)
        {
            this.dcTables[1] = new HuffmanTable(DcChrominanceBits, DcValues);
            this.acTables[1] = new HuffmanTable(AcChrominanceBits, AcChrominanceValues);
        }
    }

    // start_pass_huff, for output: the code tables of the scan's tables, the predictions and restarts
    private void StartHuffman(
        Scan scan)
    {
        foreach (var c in scan.Components)
        {
            if ((scan.Ss == 0) && (scan.Ah == 0))
            {
                this.dcCodes[c.DcTable] = MakeCodes(this.dcTables[c.DcTable]);
                this.dcPacked[c.DcTable] = PackCodes(this.dcCodes[c.DcTable]);
            }

            if (scan.Se != 0)
            {
                this.acCodes[c.AcTable] = MakeCodes(this.acTables[c.AcTable]);
                this.acPacked[c.AcTable] = PackCodes(this.acCodes[c.AcTable]);
            }
        }

        Array.Clear(this.lastDc);
        this.bitBuffer = 0;
        this.freeBits = 64;
        this.restartInterval = scan.RestartInterval;
        this.restartsToGo = scan.RestartInterval;
        this.nextRestart = 0;
    }

    // finish_pass_huff: the bits left, padded with ones
    private void FinishHuffman()
    {
        this.Reserve(16);
        ulong buffer = this.bitBuffer;
        int free = this.freeBits;
        int position = this.outputPosition;
        FlushBits(ref buffer, ref free, this.output, ref position);
        this.bitBuffer = buffer;
        this.freeBits = free;
        this.outputPosition = position;
    }

    // jpeg_make_c_derived_tbl: the canonical codes of a table's code lengths
    // Each symbol's code shifted up by the count of value bits the symbol says follow it (its low 4
    // bits: a DC symbol's are all of it), in the low 32 bits, and the code's length plus that count
    // above them
    private static ulong[] PackCodes(
        uint[] codes)
    {
        var packed = new ulong[256];
        for (int s = 0; s < 256; s++)
        {
            int extra = s & 15;
            uint code = codes[s];
            packed[s] = ((ulong)((code & 0xFFFF) << extra)) | ((ulong)((code >> 16) + extra) << 32);
        }

        return packed;
    }

    private static uint[] MakeCodes(
        HuffmanTable table)
    {
        Span<byte> sizes = stackalloc byte[257];
        Span<uint> codes = stackalloc uint[257];
        int p = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < table.Bits[length]; i++)
            {
                sizes[p++] = (byte)length;
            }
        }

        sizes[p] = 0;
        int count = p;
        uint code = 0;
        int size = sizes[0];
        p = 0;
        while (sizes[p] != 0)
        {
            while (sizes[p] == size)
            {
                codes[p++] = code;
                code++;
            }

            code <<= 1;
            size++;
        }

        var result = new uint[256];
        for (p = 0; p < count; p++)
        {
            result[table.Values[p]] = codes[p] | ((uint)sizes[p] << 16);
        }

        return result;
    }

    // encode_mcu_huff over MCU rows firstRow..firstRow + rowCount - 1 of the scan, which are at MCU row
    // firstRow - arrayRow of the coefficients
    private void EncodeSequential(
        Scan scan,
        int firstRow,
        int rowCount,
        int arrayRow)
    {
        var scanComponents = scan.Components;
        var blockComponent = scan.BlockComponent;
        var blockOffset = scan.BlockOffset;
        int blocks = scan.BlocksInMcu;
        ulong buffer = this.bitBuffer;
        int free = this.freeBits;
        var output = this.output;
        int position = this.outputPosition;
        for (int row = firstRow; row < firstRow + rowCount; row++)
        {
            for (int column = 0; column < scan.McusPerRow; column++)
            {
                if (position > OutputChunk)
                {
                    this.outputPosition = position;
                    this.FlushOutput();
                    position = 0;
                }

                if (this.restartInterval != 0)
                {
                    if (this.restartsToGo == 0)
                    {
                        FlushBits(ref buffer, ref free, output, ref position);
                        output[position++] = 0xFF;
                        output[position++] = (byte)(0xD0 + this.nextRestart);
                        this.nextRestart = (this.nextRestart + 1) & 7;
                        Array.Clear(this.lastDc);
                        this.restartsToGo = this.restartInterval;
                    }

                    this.restartsToGo--;
                }

                for (int b = 0; b < blocks; b++)
                {
                    int ci = blockComponent[b];
                    var c = scanComponents[ci];
                    int origin = McuOrigin(scan, c, column, row - arrayRow) + blockOffset[b];
                    ref short block = ref c.Coefficients[origin];
                    EncodeBlock(
                        ref block,
                        this.lastDc[ci],
                        ref MemoryMarshal.GetArrayDataReference(
                            this.dcPacked[c.DcTable]),
                        ref MemoryMarshal.GetArrayDataReference(
                            this.acPacked[c.AcTable]),
                        ref buffer,
                        ref free,
                        output,
                        ref position);
                    this.lastDc[ci] = block;
                }
            }
        }

        this.bitBuffer = buffer;
        this.freeBits = free;
        this.outputPosition = position;
    }

    // encode_one_block: the DC difference, then the AC coefficients' runs and values, in zigzag order;
    // the bit buffer in locals while it works (the output has room: the caller reserves it)
    private static void EncodeBlock(
        ref short block,
        int lastDc,
        ref ulong dc,
        ref ulong ac,
        ref ulong bufferRef,
        ref int freeRef,
        byte[] output,
        ref int positionRef)
    {
        ulong buffer = bufferRef;
        int free = freeRef;
        int position = positionRef;
        int temp = block - lastDc;
        int sign = temp >> 31;
        int nbits = 32 - BitOperations.LeadingZeroCount((uint)((temp ^ sign) - sign));
        PutPacked(Unsafe.Add(ref dc, nbits), nbits, temp + sign, ref buffer, ref free, output, ref position);

        // The nonzero AC coefficients, from a bit mask of them (as jchuff-neon.c finds them)
        ulong nonZero = NonZeroMask(ref block) & ~1UL;
        int last = 0;
        while (nonZero != 0)
        {
            int k = BitOperations.TrailingZeroCount(nonZero);
            nonZero &= nonZero - 1;
            int run = k - last - 1;
            last = k;
            while (run > 15)
            {
                PutPacked(Unsafe.Add(ref ac, 0xF0), 0, 0, ref buffer, ref free, output, ref position);
                run -= 16;
            }

            temp = Unsafe.Add(ref block, k);
            sign = temp >> 31;
            nbits = 32 - BitOperations.LeadingZeroCount((uint)((temp ^ sign) - sign));
            PutPacked(Unsafe.Add(ref ac, (run << 4) + nbits), nbits, temp + sign, ref buffer, ref free, output, ref position);
        }

        if (last != 63)
        {
            PutPacked(ac, 0, 0, ref buffer, ref free, output, ref position);
        }

        bufferRef = buffer;
        freeRef = free;
        positionRef = position;
    }

    // A block's nonzero coefficients as bits, coefficient k at bit k: on Arm64 by jchuff-neon.c's
    // bit-mask pairwise adds, elsewhere by the lanes' sign bits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NonZeroMask(
        ref short b)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            var v0 = Vector128.LoadUnsafe(ref b);
            var v1 = Vector128.LoadUnsafe(ref b, 8);
            var v2 = Vector128.LoadUnsafe(ref b, 16);
            var v3 = Vector128.LoadUnsafe(ref b, 24);
            var v4 = Vector128.LoadUnsafe(ref b, 32);
            var v5 = Vector128.LoadUnsafe(ref b, 40);
            var v6 = Vector128.LoadUnsafe(ref b, 48);
            var v7 = Vector128.LoadUnsafe(ref b, 56);
            var bit = Vector128.Create(0x8040201008040201UL).AsByte();
            var m01 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(AdvSimd.CompareTest(v0, v0).AsByte(), AdvSimd.CompareTest(v1, v1).AsByte()), bit);
            var m23 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(AdvSimd.CompareTest(v2, v2).AsByte(), AdvSimd.CompareTest(v3, v3).AsByte()), bit);
            var m45 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(AdvSimd.CompareTest(v4, v4).AsByte(), AdvSimd.CompareTest(v5, v5).AsByte()), bit);
            var m67 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(AdvSimd.CompareTest(v6, v6).AsByte(), AdvSimd.CompareTest(v7, v7).AsByte()), bit);
            var m = AdvSimd.Arm64.AddPairwise(AdvSimd.Arm64.AddPairwise(m01, m23), AdvSimd.Arm64.AddPairwise(m45, m67));
            return AdvSimd.Arm64.AddPairwise(m, m).AsUInt64().ToScalar();
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var zero = Vector128<short>.Zero;
            ulong zeros = Vector128.Narrow(Vector128.Equals(Vector128.LoadUnsafe(ref b), zero), Vector128.Equals(Vector128.LoadUnsafe(ref b, 8), zero)).ExtractMostSignificantBits();
            zeros |= (ulong)Vector128.Narrow(Vector128.Equals(Vector128.LoadUnsafe(ref b, 16), zero), Vector128.Equals(Vector128.LoadUnsafe(ref b, 24), zero)).ExtractMostSignificantBits() << 16;
            zeros |= (ulong)Vector128.Narrow(Vector128.Equals(Vector128.LoadUnsafe(ref b, 32), zero), Vector128.Equals(Vector128.LoadUnsafe(ref b, 40), zero)).ExtractMostSignificantBits() << 32;
            zeros |= (ulong)Vector128.Narrow(Vector128.Equals(Vector128.LoadUnsafe(ref b, 48), zero), Vector128.Equals(Vector128.LoadUnsafe(ref b, 56), zero)).ExtractMostSignificantBits() << 48;
            return ~zeros;
        }

        ulong mask = 0;
        for (int k = 0; k < 64; k++)
        {
            mask |= ((Unsafe.Add(ref b, k) != 0) ? 1UL : 0UL) << k;
        }

        return mask;
    }

    // PUT_CODE: a symbol's packed code (PackCodes), then nbits of the value
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PutPacked(
        ulong code,
        int nbits,
        int value,
        ref ulong buffer,
        ref int free,
        byte[] output,
        ref int position)
    {
        PutBits((uint)code | ((uint)value & ((1u << nbits) - 1)), (int)(code >> 32), ref buffer, ref free, output, ref position);
    }

    // PUT_BITS: size bits (at most 32) into the buffer, its 8 bytes written when it fills
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PutBits(
        uint bits,
        int size,
        ref ulong buffer,
        ref int free,
        byte[] output,
        ref int position)
    {
        free -= size;
        if (free < 0)
        {
            buffer = (buffer << (size + free)) | (bits >> -free);
            WriteBuffer(buffer, output, ref position);
            free += 64;
            buffer = bits;
        }
        else
        {
            buffer = (buffer << size) | bits;
        }
    }

    // FLUSH: the buffer's 8 bytes, a 0 stuffed after each 0xFF
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBuffer(
        ulong buffer,
        byte[] output,
        ref int position)
    {
        if ((buffer & 0x8080808080808080 & ~(buffer + 0x0101010101010101)) != 0)
        {
            // Rare: the position passed by value, so the caller's stays in a register
            position = WriteStuffed(buffer, output, position);
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(output), position), BinaryPrimitives.ReverseEndianness(buffer));
            position += 8;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WriteStuffed(
        ulong buffer,
        byte[] output,
        int position)
    {
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            byte b = (byte)(buffer >> shift);
            output[position++] = b;
            if (b == 0xFF)
            {
                output[position++] = 0;
            }
        }

        return position;
    }

    // flush_bits: the buffer's whole bytes, then its last bits padded with ones, and the buffer empty
    private static void FlushBits(
        ref ulong buffer,
        ref int free,
        byte[] output,
        ref int position)
    {
        int bits = 64 - free;
        while (bits >= 8)
        {
            bits -= 8;
            byte b = (byte)(buffer >> bits);
            output[position++] = b;
            if (b == 0xFF)
            {
                output[position++] = 0;
            }
        }

        if (bits > 0)
        {
            byte b = (byte)((buffer << (8 - bits)) | (0xFFu >> bits));
            output[position++] = b;
            if (b == 0xFF)
            {
                output[position++] = 0;
            }
        }

        buffer = 0;
        free = 64;
    }
}
