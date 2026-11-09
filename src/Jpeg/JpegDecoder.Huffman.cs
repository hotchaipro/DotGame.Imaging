// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Part of the JPEG decoder (JpegDecoder.cs). The decoder's port notes and notices:
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, a progressive or
// multiscan image decoded whole before it's output, and the Huffman decoding, vector forms and buffer
// handling new.
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
using System.IO;

namespace DotGame.Imaging.Jpeg;

internal static partial class JpegDecoder
{
    private sealed class HuffmanTable
    {
        public const int LookupBits = 9;

        // Codes up to LookupBits long: (length << 8) | symbol, 0 for longer codes
        public readonly ushort[] Lookup = new ushort[1 << LookupBits];
        public readonly int[] MaxCode = new int[18];      // the largest code of each length, -1 none
        public readonly int[] ValueOffset = new int[17];
        public readonly byte[] Symbols = new byte[256];

        public HuffmanTable(
            ReadOnlySpan<byte> counts,
            ReadOnlySpan<byte> symbols)
        {
            symbols.CopyTo(this.Symbols);
            int code = 0;
            int k = 0;
            for (int length = 1; length <= 16; length++)
            {
                int count = counts[length - 1];
                this.ValueOffset[length] = k - code;

                // The codes of each length must fit in its bits, and none may be all ones (libjpeg's
                // check, made before the codes fill the lookup, which they'd overrun)
                if (code + count >= (1 << length))
                {
                    throw new InvalidDataException("A JPEG Huffman table is malformed.");
                }

                if (count > 0)
                {
                    for (int i = 0; i < count; i++, k++, code++)
                    {
                        if (length <= LookupBits)
                        {
                            int shift = LookupBits - length;
                            int first = code << shift;
                            for (int j = 0; j < (1 << shift); j++)
                            {
                                this.Lookup[first + j] = (ushort)((length << 8) | symbols[k]);
                            }
                        }
                    }

                    this.MaxCode[length] = code - 1;
                }
                else
                {
                    this.MaxCode[length] = -1;
                }

                code <<= 1;
            }

            this.MaxCode[17] = int.MaxValue;

            // AC codes whose value fits in the lookup's bits too: (value << 16) | (run << 8) | bits,
            // and the end of block: EndOfBlock | bits
            for (int i = 0; i < this.Lookup.Length; i++)
            {
                int entry = this.Lookup[i];
                int length = entry >> 8;
                int run = (entry >> 4) & 15;
                int size = entry & 15;
                if ((entry != 0) && ((entry & 0xFF) == 0))
                {
                    this.FastAc[i] = EndOfBlock | length;
                }
                else if ((entry != 0) && (size != 0) && (length + size <= LookupBits))
                {
                    int value = (i >> (LookupBits - length - size)) & ((1 << size) - 1);
                    if (value < (1 << (size - 1)))
                    {
                        value -= (1 << size) - 1;
                    }

                    this.FastAc[i] = (value << 16) | (run << 8) | (length + size);
                }
            }
        }

        public const int EndOfBlock = 0x1000;

        public readonly int[] FastAc = new int[1 << LookupBits];
    }

    // The entropy-coded data's bits, most significant first: 0xFF 0x00 is a 0xFF byte, and at a
    // marker the bits are zeros from then on (as libjpeg reads a damaged scan)
    private sealed class BitReader(byte[] data)
    {
        public int Position;
        private ulong bits;
        private int count;
        public bool AtMarker;

        public void Reset(
            int position)
        {
            this.Position = position;
            this.bits = 0;
            this.count = 0;
            this.AtMarker = false;
        }

        public void Fill()
        {
            while (this.count <= 56)
            {
                ulong b = 0;
                if (!this.AtMarker && (this.Position < data.Length))
                {
                    b = data[this.Position];
                    if (b == 0xFF)
                    {
                        int next = (this.Position + 1 < data.Length) ? data[this.Position + 1] : 0xD9;
                        if (next == 0)
                        {
                            this.Position += 2;
                        }
                        else
                        {
                            this.AtMarker = true;
                            b = 0;
                        }
                    }
                    else
                    {
                        this.Position++;
                    }
                }

                this.bits |= b << (56 - this.count);
                this.count += 8;
            }
        }

        public int Peek(
            int n)
        {
            if (this.count < n)
            {
                this.Fill();
            }

            return (int)(this.bits >> (64 - n));
        }

        public void Skip(
            int n)
        {
            this.bits <<= n;
            this.count -= n;
        }

        public int Bits(
            int n)
        {
            if (n == 0)
            {
                return 0;
            }

            int value = this.Peek(n);
            this.Skip(n);
            return value;
        }

        public int Bit() => this.Bits(1);

        // A value of n bits, as T.81's EXTEND: the upper half positive, the lower negative
        public int Receive(
            int n)
        {
            if (n == 0)
            {
                return 0;
            }

            int value = this.Bits(n);
            return (value < (1 << (n - 1))) ? value - (1 << n) + 1 : value;
        }

        public int Decode(
            HuffmanTable table)
        {
            int look = this.Peek(16);
            int entry = table.Lookup[look >> (16 - HuffmanTable.LookupBits)];
            if (entry != 0)
            {
                this.Skip(entry >> 8);
                return entry & 0xFF;
            }

            int length = HuffmanTable.LookupBits + 1;
            int code = look >> (16 - length);
            while (code > table.MaxCode[length])
            {
                length++;
                if (length > 16)
                {
                    // A code no table has: damaged data, read as 0 (libjpeg warns and goes on)
                    this.Skip(16);
                    return 0;
                }

                code = look >> (16 - length);
            }

            this.Skip(length);
            return table.Symbols[(code + table.ValueOffset[length]) & 0xFF];
        }
    }
}
