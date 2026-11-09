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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.Jpeg;

internal static partial class JpegDecoder
{
    private sealed partial class Decoding
    {
        // A progressive scan, its blocks' coefficients refined in place, the bits in locals
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void DecodeProgressiveScan(
            Component[] scan,
            int spectralStart,
            int spectralEnd,
            int successiveHigh,
            int successiveLow)
        {
            ulong bits = 0;
            int count = 0;
            int position = this.reader.Position;
            bool atMarker = false;
            int eob = 0;
            int restartsLeft = this.restartInterval;
            bool single = scan.Length == 1;
            int rows = single ? scan[0].HeightInBlocks : this.mcusPerColumn;
            int columns = single ? scan[0].WidthInBlocks : this.mcusPerLine;
            int kind = (spectralStart == 0) ? ((successiveHigh == 0) ? 0 : 1) : ((successiveHigh == 0) ? 2 : 3);
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
                            eob = 0;
                            restartsLeft = this.restartInterval;
                        }

                        restartsLeft--;
                    }

                    if (single)
                    {
                        var c = scan[0];
                        var block = c.Coefficients.AsSpan(((mcuRow * c.BlocksPerLine) + mcuColumn) * 64, 64);
                        this.ProgressiveBlock(kind, c, block, spectralStart, spectralEnd, successiveLow, ref bits, ref count, ref position, ref atMarker, ref eob);
                        continue;
                    }

                    foreach (var c in scan)
                    {
                        for (int v = 0; v < c.V; v++)
                        {
                            for (int h = 0; h < c.H; h++)
                            {
                                int index = ((((mcuRow * c.V) + v) * c.BlocksPerLine) + (mcuColumn * c.H)) + h;
                                this.ProgressiveBlock(kind, c, c.Coefficients.AsSpan(index * 64, 64), spectralStart, spectralEnd, successiveLow, ref bits, ref count, ref position, ref atMarker, ref eob);
                            }
                        }
                    }
                }
            }

            this.reader.Position = position;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProgressiveBlock(int kind, Component c, Span<short> block, int spectralStart, int spectralEnd, int successiveLow,
            ref ulong bits, ref int count, ref int position, ref bool atMarker, ref int eob)
        {
            ref short coefficients = ref MemoryMarshal.GetReference(block);
            ref int natural = ref MemoryMarshal.GetArrayDataReference(NaturalOrder);
            switch (kind)
            {
                case 0:
                    {
                        // DC first: the difference, shifted
                        if (count < 32)
                        {
                            Refill(ref bits, ref count, ref position, ref atMarker, this.data);
                        }

                        int t = DecodeSymbol(ref bits, ref count, this.dcTables[c.DcTable]!);
                        c.DcPredictor += Receive(ref bits, ref count, t);
                        coefficients = (short)(c.DcPredictor << successiveLow);
                        return;
                    }

                case 1:
                    // DC refinement: one bit
                    if (count < 1)
                    {
                        Refill(ref bits, ref count, ref position, ref atMarker, this.data);
                    }

                    if ((bits >> 63) != 0)
                    {
                        coefficients |= (short)(1 << successiveLow);
                    }

                    bits <<= 1;
                    count--;
                    return;

                case 2:
                    {
                        // AC first pass
                        if (eob > 0)
                        {
                            eob--;
                            return;
                        }

                        var table = this.acTables[c.AcTable]!;
                        for (int k = spectralStart; k <= spectralEnd; k++)
                        {
                            if (count < 32)
                            {
                                Refill(ref bits, ref count, ref position, ref atMarker, this.data);
                            }

                            int rs = DecodeSymbol(ref bits, ref count, table);
                            int run = rs >> 4;
                            int size = rs & 15;
                            if (size != 0)
                            {
                                k += run;
                                Unsafe.Add(ref coefficients, Unsafe.Add(ref natural, k)) = (short)(Receive(ref bits, ref count, size) * (1 << successiveLow));
                            }
                            else if (run == 15)
                            {
                                k += 15;
                            }
                            else
                            {
                                eob = 1 << run;
                                if (run != 0)
                                {
                                    eob += (int)(bits >> (64 - run));
                                    bits <<= run;
                                    count -= run;
                                }

                                eob--;
                                break;
                            }
                        }

                        return;
                    }

                default:
                    {
                        // AC refinement (libjpeg's decode_mcu_AC_refine)
                        var table = this.acTables[c.AcTable]!;
                        int p1 = 1 << successiveLow;
                        int m1 = -1 << successiveLow;
                        int index = spectralStart;
                        if (eob == 0)
                        {
                            for (; index <= spectralEnd; index++)
                            {
                                if (count < 32)
                                {
                                    Refill(ref bits, ref count, ref position, ref atMarker, this.data);
                                }

                                int rs = DecodeSymbol(ref bits, ref count, table);
                                int run = rs >> 4;
                                int size = rs & 15;
                                int value = 0;
                                if (size != 0)
                                {
                                    value = ((bits >> 63) != 0) ? p1 : m1;
                                    bits <<= 1;
                                    count--;
                                }
                                else if (run != 15)
                                {
                                    eob = 1 << run;
                                    if (run != 0)
                                    {
                                        eob += (int)(bits >> (64 - run));
                                        bits <<= run;
                                        count -= run;
                                    }

                                    break;
                                }

                                do
                                {
                                    ref short coefficient = ref Unsafe.Add(ref coefficients, Unsafe.Add(ref natural, index));
                                    if (coefficient != 0)
                                    {
                                        if (count < 1)
                                        {
                                            Refill(ref bits, ref count, ref position, ref atMarker, this.data);
                                        }

                                        if (((bits >> 63) != 0) && ((coefficient & p1) == 0))
                                        {
                                            coefficient = (short)(coefficient + ((coefficient >= 0) ? p1 : m1));
                                        }

                                        bits <<= 1;
                                        count--;
                                    }
                                    else
                                    {
                                        if (--run < 0)
                                        {
                                            break;
                                        }
                                    }

                                    index++;
                                }
                                while (index <= spectralEnd);

                                if (value != 0)
                                {
                                    Unsafe.Add(ref coefficients, Unsafe.Add(ref natural, index)) = (short)value;
                                }
                            }
                        }

                        if (eob > 0)
                        {
                            for (; index <= spectralEnd; index++)
                            {
                                ref short coefficient = ref Unsafe.Add(ref coefficients, Unsafe.Add(ref natural, index));
                                if (coefficient != 0)
                                {
                                    if (count < 1)
                                    {
                                        Refill(ref bits, ref count, ref position, ref atMarker, this.data);
                                    }

                                    if (((bits >> 63) != 0) && ((coefficient & p1) == 0))
                                    {
                                        coefficient = (short)(coefficient + ((coefficient >= 0) ? p1 : m1));
                                    }

                                    bits <<= 1;
                                    count--;
                                }
                            }

                            eob--;
                        }

                        return;
                    }
            }
        }

        // A restart marker: the bits after it start afresh, and the DC predictions and EOB run reset
        private void Restart(
            Component[] scan)
        {
            int position = this.reader.Position;
            while ((position + 1 < this.data.Length) && !((this.data[position] == 0xFF) && (this.data[position + 1] >= 0xD0) && (this.data[position + 1] <= 0xD7)))
            {
                if ((this.data[position] == 0xFF) && (this.data[position + 1] != 0) && (this.data[position + 1] != 0xFF))
                {
                    break; // another marker: no restart marker here (damaged), carry on without one
                }

                position++;
            }

            if ((position + 1 < this.data.Length) && (this.data[position] == 0xFF) && (this.data[position + 1] >= 0xD0) && (this.data[position + 1] <= 0xD7))
            {
                position += 2;
            }

            this.reader.Reset(position);
            this.eobRun = 0;
            foreach (var c in scan)
            {
                c.DcPredictor = 0;
            }
        }

        private void DecodeBlock(
            Component c,
            int block,
            int spectralStart,
            int spectralEnd,
            int successiveHigh,
            int successiveLow)
        {
            var coefficients = c.Coefficients.AsSpan(block * 64, 64);
            var r = this.reader;
            if (!this.progressive)
            {
                int t = r.Decode(this.dcTables[c.DcTable]!);
                c.DcPredictor += r.Receive(t);
                coefficients[0] = (short)c.DcPredictor;
                var ac = this.acTables[c.AcTable]!;
                for (int k = 1; k < 64; k++)
                {
                    int rs = r.Decode(ac);
                    int run = rs >> 4;
                    int size = rs & 15;
                    if (size != 0)
                    {
                        k += run;
                        coefficients[NaturalOrder[k]] = (short)r.Receive(size);
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

                return;
            }

            if (spectralStart == 0)
            {
                if (successiveHigh == 0)
                {
                    int t = r.Decode(this.dcTables[c.DcTable]!);
                    c.DcPredictor += r.Receive(t);
                    coefficients[0] = (short)(c.DcPredictor << successiveLow);
                }
                else if (r.Bit() != 0)
                {
                    coefficients[0] |= (short)(1 << successiveLow);
                }

                return;
            }

            var table = this.acTables[c.AcTable]!;
            if (successiveHigh == 0)
            {
                // AC first pass
                if (this.eobRun > 0)
                {
                    this.eobRun--;
                    return;
                }

                for (int k = spectralStart; k <= spectralEnd; k++)
                {
                    int rs = r.Decode(table);
                    int run = rs >> 4;
                    int size = rs & 15;
                    if (size != 0)
                    {
                        k += run;
                        coefficients[NaturalOrder[k]] = (short)(r.Receive(size) * (1 << successiveLow));
                    }
                    else if (run == 15)
                    {
                        k += 15;
                    }
                    else
                    {
                        this.eobRun = 1 << run;
                        if (run != 0)
                        {
                            this.eobRun += r.Bits(run);
                        }

                        this.eobRun--;
                        break;
                    }
                }

                return;
            }

            // AC refinement (libjpeg's decode_mcu_AC_refine)
            int p1 = 1 << successiveLow;
            int m1 = -1 << successiveLow;
            int index = spectralStart;
            if (this.eobRun == 0)
            {
                for (; index <= spectralEnd; index++)
                {
                    int rs = r.Decode(table);
                    int run = rs >> 4;
                    int size = rs & 15;
                    int value = 0;
                    if (size != 0)
                    {
                        value = (r.Bit() != 0) ? p1 : m1;
                    }
                    else if (run != 15)
                    {
                        this.eobRun = 1 << run;
                        if (run != 0)
                        {
                            this.eobRun += r.Bits(run);
                        }

                        break;
                    }

                    do
                    {
                        ref short coefficient = ref coefficients[NaturalOrder[index]];
                        if (coefficient != 0)
                        {
                            if ((r.Bit() != 0) && ((coefficient & p1) == 0))
                            {
                                coefficient = (short)(coefficient + ((coefficient >= 0) ? p1 : m1));
                            }
                        }
                        else
                        {
                            if (--run < 0)
                            {
                                break;
                            }
                        }

                        index++;
                    }
                    while (index <= spectralEnd);

                    if (value != 0)
                    {
                        coefficients[NaturalOrder[index]] = (short)value;
                    }
                }
            }

            if (this.eobRun > 0)
            {
                for (; index <= spectralEnd; index++)
                {
                    ref short coefficient = ref coefficients[NaturalOrder[index]];
                    if (coefficient != 0)
                    {
                        if ((r.Bit() != 0) && ((coefficient & p1) == 0))
                        {
                            coefficient = (short)(coefficient + ((coefficient >= 0) ? p1 : m1));
                        }
                    }
                }

                this.eobRun--;
            }
        }
    }
}
