// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, sharing the
// sequential coder's bit buffer, with vector forms new.
//
// The progressive scans' coding (DC first and refinement, AC first and refinement, end-of-band runs,
// correction bits, restarts) and their statistics follow jcphuff.c, which carries this notice:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1995-1997, Thomas G. Lane.
//   libjpeg-turbo Modifications:
//   Copyright (C) 2011, 2015, 2018, 2021-2022, D. R. Commander.
//   Copyright (C) 2016, 2018, 2022, Matthieu Darbois.
//   Copyright (C) 2020, Arm Limited.
//   Copyright (C) 2021, Alex Richardson.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // The most correction bits an AC refinement scan holds back for an end-of-band run (MAX_CORR_BITS)
    private const int MaxCorrectionBits = 1000;

    private readonly byte[] correctionBits = new byte[MaxCorrectionBits];
    private bool gathering;
    private long[][] symbolCounts = [];
    private uint[][] symbolCodes = [];
    private int scanTable;
    private int endOfBandRun;
    private int bufferedCorrections;

    /// <summary>
    /// A progressive scan: its symbols counted and its tables made (but for a DC refinement, which has
    /// none), its header, then its data, as jcmaster.c's passes run it.
    /// </summary>
    private void EncodeProgressiveScan(
        Scan scan,
        bool first)
    {
        this.StartScan(scan);
        bool dcBand = scan.Ss == 0;
        this.symbolCounts = dcBand ? this.dcCounts : this.acCounts;
        this.symbolCodes = dcBand ? this.dcCodes : this.acCodes;
        this.scanTable = dcBand ? scan.Components[0].DcTable : scan.Components[0].AcTable;
        if (!dcBand || (scan.Ah == 0))
        {
            foreach (var c in scan.Components)
            {
                Array.Clear(this.symbolCounts[dcBand ? c.DcTable : c.AcTable]);
            }

            this.gathering = true;
            this.RunProgressiveScan(scan);

            // finish_pass_gather_phuff
            Span<bool> done = stackalloc bool[2];
            done.Clear();
            foreach (var c in scan.Components)
            {
                int table = dcBand ? c.DcTable : c.AcTable;
                if (!done[table])
                {
                    var optimal = OptimalTable(this.symbolCounts[table]);
                    if (dcBand)
                    {
                        this.dcTables[table] = optimal;
                    }
                    else
                    {
                        this.acTables[table] = optimal;
                    }

                    done[table] = true;
                }
            }
        }

        if (first)
        {
            this.WriteFrameHeader();
        }

        this.WriteScanHeader(scan);
        this.StartHuffman(scan);
        this.gathering = false;
        this.RunProgressiveScan(scan);
        this.Reserve(MaxCorrectionBits);
        this.EmitEndOfBandRun();
        this.FinishHuffman();
    }

    // start_pass_phuff and the MCUs of the scan, counted or coded
    private void RunProgressiveScan(
        Scan scan)
    {
        Array.Clear(this.lastDc);
        this.endOfBandRun = 0;
        this.bufferedCorrections = 0;
        this.restartsToGo = scan.RestartInterval;
        this.nextRestart = 0;
        bool dcBand = scan.Ss == 0;
        for (int row = 0; row < scan.McuRows; row++)
        {
            for (int column = 0; column < scan.McusPerRow; column++)
            {
                if (this.outputPosition > OutputChunk)
                {
                    this.FlushOutput();
                }

                if (scan.RestartInterval != 0)
                {
                    if (this.restartsToGo == 0)
                    {
                        this.EmitProgressiveRestart(dcBand);
                        this.restartsToGo = scan.RestartInterval;
                    }

                    this.restartsToGo--;
                }

                if (dcBand)
                {
                    for (int b = 0; b < scan.BlocksInMcu; b++)
                    {
                        int ci = scan.BlockComponent[b];
                        var c = scan.Components[ci];
                        int origin = McuOrigin(scan, c, column, row) + scan.BlockOffset[b];
                        if (scan.Ah == 0)
                        {
                            this.EncodeDcFirst(c.Coefficients[origin], ci, c.DcTable, scan.Al);
                        }
                        else
                        {
                            this.EmitBits((uint)(c.Coefficients[origin] >> scan.Al), 1);
                        }
                    }
                }
                else
                {
                    var c = scan.Components[0];
                    var block = c.Coefficients.AsSpan(McuOrigin(scan, c, column, row), 64);
                    if (scan.Ah == 0)
                    {
                        this.EncodeAcFirst(block, scan.Ss, scan.Se, scan.Al);
                    }
                    else
                    {
                        this.EncodeAcRefine(block, scan.Ss, scan.Se, scan.Al);
                    }
                }
            }
        }

        if (this.gathering)
        {
            this.EmitEndOfBandRun();
        }
    }

    // emit_restart for a progressive scan
    private void EmitProgressiveRestart(
        bool dcBand)
    {
        this.EmitEndOfBandRun();
        if (!this.gathering)
        {
            FlushBits(ref this.bitBuffer, ref this.freeBits, this.output, ref this.outputPosition);
            this.output[this.outputPosition++] = 0xFF;
            this.output[this.outputPosition++] = (byte)(0xD0 + this.nextRestart);
        }

        this.nextRestart = (this.nextRestart + 1) & 7;
        if (dcBand)
        {
            Array.Clear(this.lastDc);
        }
        else
        {
            this.endOfBandRun = 0;
            this.bufferedCorrections = 0;
        }
    }

    // encode_mcu_DC_first, a block: the point-transformed DC's difference
    private void EncodeDcFirst(
        int dc,
        int ci,
        int table,
        int al)
    {
        int value = dc >> al;
        int temp = value - this.lastDc[ci];
        this.lastDc[ci] = value;
        int sign = temp >> 31;
        int abs = (temp ^ sign) - sign;
        int nbits = 32 - BitOperations.LeadingZeroCount((uint)abs);
        this.EmitSymbol(table, nbits);
        if (nbits != 0)
        {
            this.EmitBits((uint)(abs ^ sign), nbits);
        }
    }

    // encode_mcu_AC_first: the band's point-transformed coefficients, runs of empty blocks counted
    private void EncodeAcFirst(
        ReadOnlySpan<short> block,
        int ss,
        int se,
        int al)
    {
        int length = se - ss + 1;
        ulong band = (1UL << length) - 1;
        Span<short> absValues = stackalloc short[64];
        Span<short> values = stackalloc short[64];
        PrepareBand(block, al, absValues, values, out ulong nonZero, out _, out _);
        nonZero = (nonZero >> ss) & band;
        values = values[ss..];

        if ((nonZero != 0) && (this.endOfBandRun > 0))
        {
            this.EmitEndOfBandRun();
        }

        int position = 0;
        while (nonZero != 0)
        {
            int run = BitOperations.TrailingZeroCount(nonZero);
            nonZero >>= run;
            position += run;
            while (run > 15)
            {
                this.EmitSymbol(this.scanTable, 0xF0);
                run -= 16;
            }

            int value = values[position];
            int abs = value ^ (value >> 31);
            int nbits = 32 - BitOperations.LeadingZeroCount((uint)abs);
            this.EmitSymbol(this.scanTable, (run << 4) + nbits);
            this.EmitBits((uint)value, nbits);
            position++;
            nonZero >>= 1;
        }

        if (position < length)
        {
            this.endOfBandRun++;
            if (this.endOfBandRun == 0x7FFF)
            {
                this.EmitEndOfBandRun();
            }
        }
    }

    // encode_mcu_AC_refine: the next bit of each coefficient already nonzero (held back as a
    // correction bit), each newly nonzero one coded with its run
    private void EncodeAcRefine(
        ReadOnlySpan<short> block,
        int ss,
        int se,
        int al)
    {
        int length = se - ss + 1;
        ulong band = (1UL << length) - 1;
        Span<short> absValues = stackalloc short[64];
        Span<short> values = stackalloc short[64];
        PrepareBand(block, al, absValues, values, out ulong nonZero, out ulong signs, out ulong ones);
        nonZero = (nonZero >> ss) & band;
        signs = (signs >> ss) & nonZero;
        ones = (ones >> ss) & band;
        int lastNew = (ones == 0) ? 0 : (63 - BitOperations.LeadingZeroCount(ones));
        absValues = absValues[ss..];

        int run = 0;
        int corrections = 0;
        int correctionStart = this.bufferedCorrections;
        int position = 0;
        while (nonZero != 0)
        {
            int skip = BitOperations.TrailingZeroCount(nonZero);
            nonZero >>= skip;
            signs >>= skip;
            run += skip;
            position += skip;
            while ((run > 15) && (position <= lastNew))
            {
                this.EmitEndOfBandRun();
                this.EmitSymbol(this.scanTable, 0xF0);
                run -= 16;
                this.EmitCorrectionBits(correctionStart, corrections);
                correctionStart = 0;
                corrections = 0;
            }

            int abs = absValues[position++];
            if (abs > 1)
            {
                this.correctionBits[correctionStart + corrections++] = (byte)(abs & 1);
                signs >>= 1;
                nonZero >>= 1;
                continue;
            }

            this.EmitEndOfBandRun();
            this.EmitSymbol(this.scanTable, (run << 4) + 1);
            this.EmitBits((uint)(signs & 1), 1);
            this.EmitCorrectionBits(correctionStart, corrections);
            correctionStart = 0;
            corrections = 0;
            run = 0;
            signs >>= 1;
            nonZero >>= 1;
        }

        run |= length - position;
        if ((run > 0) || (corrections > 0))
        {
            this.endOfBandRun++;
            this.bufferedCorrections += corrections;
            if ((this.endOfBandRun == 0x7FFF) || (this.bufferedCorrections > MaxCorrectionBits - 64 + 1))
            {
                this.EmitEndOfBandRun();
            }
        }
    }

    // The AC prepare functions (encode_mcu_AC_first_prepare, encode_mcu_AC_refine_prepare and their
    // Neon forms) for a whole zigzag-ordered block: each coefficient's point-transformed absolute
    // value, that value with the sign's bits (its ones' complement if negative), and bit masks of the
    // coefficients that are nonzero, positive, and 1 after the point transform
    private static void PrepareBand(
        ReadOnlySpan<short> block,
        int al,
        Span<short> absValues,
        Span<short> values,
        out ulong nonZero,
        out ulong positive,
        out ulong ones)
    {
        ref short b = ref MemoryMarshal.GetReference(block);
        ref short a = ref MemoryMarshal.GetReference(absValues);
        ref short d = ref MemoryMarshal.GetReference(values);
        if (Vector128.IsHardwareAccelerated)
        {
            PrepareRow(ref b, ref a, ref d, 0, al, out var z0, out var p0, out var e0);
            PrepareRow(ref b, ref a, ref d, 8, al, out var z1, out var p1, out var e1);
            PrepareRow(ref b, ref a, ref d, 16, al, out var z2, out var p2, out var e2);
            PrepareRow(ref b, ref a, ref d, 24, al, out var z3, out var p3, out var e3);
            PrepareRow(ref b, ref a, ref d, 32, al, out var z4, out var p4, out var e4);
            PrepareRow(ref b, ref a, ref d, 40, al, out var z5, out var p5, out var e5);
            PrepareRow(ref b, ref a, ref d, 48, al, out var z6, out var p6, out var e6);
            PrepareRow(ref b, ref a, ref d, 56, al, out var z7, out var p7, out var e7);
            nonZero = Bits(z0, z1, z2, z3, z4, z5, z6, z7);
            positive = Bits(p0, p1, p2, p3, p4, p5, p6, p7);
            ones = Bits(e0, e1, e2, e3, e4, e5, e6, e7);
            return;
        }

        nonZero = 0;
        positive = 0;
        ones = 0;
        for (int k = 0; k < 64; k++)
        {
            int temp = block[k];
            int sign = temp >> 31;
            int abs = ((temp ^ sign) - sign) >> al;
            absValues[k] = (short)abs;
            values[k] = (short)(abs ^ sign);
            nonZero |= ((abs != 0) ? 1UL : 0UL) << k;
            positive |= ((temp > 0) ? 1UL : 0UL) << k;
            ones |= ((abs == 1) ? 1UL : 0UL) << k;
        }
    }

    // A row of PrepareBand: the values stored, the masks' lanes all ones where they hold
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrepareRow(
        ref short block,
        ref short absValues,
        ref short values,
        int i,
        int al,
        out Vector128<short> nonZero,
        out Vector128<short> positive,
        out Vector128<short> one)
    {
        var v = Vector128.LoadUnsafe(ref block, (nuint)i);
        var abs = Vector128.ShiftRightLogical(Vector128.Abs(v).AsUInt16(), al).AsInt16();
        abs.StoreUnsafe(ref absValues, (nuint)i);
        (abs ^ Vector128.ShiftRightArithmetic(v, 15)).StoreUnsafe(ref values, (nuint)i);
        nonZero = ~Vector128.Equals(abs, Vector128<short>.Zero);
        positive = Vector128.GreaterThan(v, Vector128<short>.Zero);
        one = Vector128.Equals(abs, Vector128<short>.One);
    }

    // 8 vectors of all-ones or zero lanes as 64 bits, lane k of vector i at bit 8i + k: on Arm64 by
    // bit-mask pairwise adds (as jchuff-neon.c), elsewhere by the lanes' sign bits
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bits(
        Vector128<short> m0,
        Vector128<short> m1,
        Vector128<short> m2,
        Vector128<short> m3,
        Vector128<short> m4,
        Vector128<short> m5,
        Vector128<short> m6,
        Vector128<short> m7)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            var bit = Vector128.Create(0x8040201008040201UL).AsByte();
            var m01 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(m0.AsByte(), m1.AsByte()), bit);
            var m23 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(m2.AsByte(), m3.AsByte()), bit);
            var m45 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(m4.AsByte(), m5.AsByte()), bit);
            var m67 = AdvSimd.And(AdvSimd.Arm64.UnzipEven(m6.AsByte(), m7.AsByte()), bit);
            var sum = AdvSimd.Arm64.AddPairwise(AdvSimd.Arm64.AddPairwise(m01, m23), AdvSimd.Arm64.AddPairwise(m45, m67));
            return AdvSimd.Arm64.AddPairwise(sum, sum).AsUInt64().ToScalar();
        }

        ulong bits = Vector128.Narrow(m0, m1).ExtractMostSignificantBits();
        bits |= (ulong)Vector128.Narrow(m2, m3).ExtractMostSignificantBits() << 16;
        bits |= (ulong)Vector128.Narrow(m4, m5).ExtractMostSignificantBits() << 32;
        return bits | ((ulong)Vector128.Narrow(m6, m7).ExtractMostSignificantBits() << 48);
    }

    // emit_eobrun: the end-of-band run, then the correction bits held back for it
    private void EmitEndOfBandRun()
    {
        if (this.endOfBandRun > 0)
        {
            int temp = this.endOfBandRun;
            int nbits = 31 - BitOperations.LeadingZeroCount((uint)temp);
            this.EmitSymbol(this.scanTable, nbits << 4);
            if (nbits != 0)
            {
                this.EmitBits((uint)temp, nbits);
            }

            this.endOfBandRun = 0;
            this.EmitCorrectionBits(0, this.bufferedCorrections);
            this.bufferedCorrections = 0;
        }
    }

    // emit_buffered_bits
    private void EmitCorrectionBits(
        int start,
        int count)
    {
        if (this.gathering)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            this.EmitBits(this.correctionBits[start + i], 1);
        }
    }

    // emit_symbol: a symbol counted, or its code written
    private void EmitSymbol(
        int table,
        int symbol)
    {
        if (this.gathering)
        {
            this.symbolCounts[table][symbol]++;
        }
        else
        {
            uint code = this.symbolCodes[table][symbol];
            PutBits(code & 0xFFFF, (int)(code >> 16), ref this.bitBuffer, ref this.freeBits, this.output, ref this.outputPosition);
        }
    }

    // emit_bits: the low size bits of value, unless counting
    private void EmitBits(
        uint value,
        int size)
    {
        if (!this.gathering)
        {
            PutBits(value & ((1u << size) - 1), size, ref this.bitBuffer, ref this.freeBits, this.output, ref this.outputPosition);
        }
    }
}
