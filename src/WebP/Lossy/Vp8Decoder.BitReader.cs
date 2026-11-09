// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/utils/bit_reader_utils.c and bit_reader_inl_utils.h, the VP8
// boolean decoder), modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Decoder
{
    // The boolean decoder (bit_reader_utils.c with BITS = 56): the value's bits not yet consumed,
    // the range minus 1, in [127, 254], and the bits left in the value
    private struct BitReader
    {
        public ulong Value;
        public uint Range;
        public int Bits;
        public int Position;
        public int End;
        public int Max;
        public bool Eof;

        public static BitReader Create(
            byte[] data,
            int start,
            int size)
        {
            var reader = new BitReader
            {
                Range = 255 - 1,
                Value = 0,
                Bits = -8,
                Eof = false,
                Position = start,
                End = start + size,
                Max = (size >= 8) ? start + size - 8 + 1 : start,
            };
            reader.LoadNewBytes(data);
            return reader;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void LoadNewBytes(
            byte[] data)
        {
            if (this.Position < this.Max)
            {
                // Seven bytes, big-endian
                ulong bits = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(this.Position)) >> 8;
                this.Position += 7;
                this.Value = bits | (this.Value << 56);
                this.Bits += 56;
            }
            else
            {
                this.LoadFinalBytes(data);
            }
        }

        private void LoadFinalBytes(
            byte[] data)
        {
            if (this.Position < this.End)
            {
                this.Bits += 8;
                this.Value = data[this.Position++] | (this.Value << 8);
            }
            else if (!this.Eof)
            {
                this.Value <<= 8;
                this.Bits += 8;
                this.Eof = true;
            }
            else
            {
                this.Bits = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetBit(
            byte[] data,
            int probability)
        {
            uint range = this.Range;
            if (this.Bits < 0)
            {
                this.LoadNewBytes(data);
            }

            int position = this.Bits;
            uint split = (range * (uint)probability) >> 8;
            uint value = (uint)(this.Value >> position);
            int bit;
            if (value > split)
            {
                range -= split;
                this.Value -= (ulong)(split + 1) << position;
                bit = 1;
            }
            else
            {
                range = split + 1;
                bit = 0;
            }

            int shift = 7 ^ BitOperations.Log2(range);
            range <<= shift;
            this.Bits -= shift;
            this.Range = range - 1;
            return bit;
        }

        // A value's sign, at probability one half
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetSigned(
            byte[] data,
            int v)
        {
            if (this.Bits < 0)
            {
                this.LoadNewBytes(data);
            }

            int position = this.Bits;
            uint split = this.Range >> 1;
            uint value = (uint)(this.Value >> position);
            int mask = (int)(split - value) >> 31;
            this.Bits -= 1;
            this.Range += (uint)mask;
            this.Range |= 1;
            this.Value -= (ulong)((split + 1) & (uint)mask) << position;
            return (v ^ mask) - mask;
        }

        public uint GetValue(
            byte[] data,
            int bits)
        {
            uint v = 0;
            while (bits-- > 0)
            {
                v |= (uint)this.GetBit(data, 0x80) << bits;
            }

            return v;
        }

        public int GetSignedValue(
            byte[] data,
            int bits)
        {
            int value = (int)this.GetValue(data, bits);
            return (this.GetBit(data, 0x80) != 0) ? -value : value;
        }

        public bool Get(
            byte[] data)
        {
            return this.GetValue(data, 1) != 0;
        }
    }
}
