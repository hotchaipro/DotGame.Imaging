// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/utils/bit_writer_utils.c's VP8BitWriter and src/enc/token_enc.c),
// modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    // kNorm and kNewRange: the renormalization shift and new range for each range below 127, and 0 and
    // the range itself from 127 up (so renormalizing needs no test)
    private static ReadOnlySpan<byte> Norm =>
    [
        7, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static ReadOnlySpan<byte> NewRange =>
    [
        127, 127, 191, 127, 159, 191, 223, 127, 143, 159, 175, 191, 207, 223, 239, 127,
        135, 143, 151, 159, 167, 175, 183, 191, 199, 207, 215, 223, 231, 239, 247, 127,
        131, 135, 139, 143, 147, 151, 155, 159, 163, 167, 171, 175, 179, 183, 187, 191,
        195, 199, 203, 207, 211, 215, 219, 223, 227, 231, 235, 239, 243, 247, 251, 127,
        129, 131, 133, 135, 137, 139, 141, 143, 145, 147, 149, 151, 153, 155, 157, 159,
        161, 163, 165, 167, 169, 171, 173, 175, 177, 179, 181, 183, 185, 187, 189, 191,
        193, 195, 197, 199, 201, 203, 205, 207, 209, 211, 213, 215, 217, 219, 221, 223,
        225, 227, 229, 231, 233, 235, 237, 239, 241, 243, 245, 247, 249, 251, 253, 127,
        128, 129, 130, 131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143,
        144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159,
        160, 161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 173, 174, 175,
        176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191,
        192, 193, 194, 195, 196, 197, 198, 199, 200, 201, 202, 203, 204, 205, 206, 207,
        208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223,
        224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239,
        240, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255,
    ];

    /// <summary>
    /// The boolean encoder (VP8BitWriter). The hot loops keep range, value and the bit count in
    /// locals, passed by ref to <see cref="Put"/>, and store them back when done.
    /// </summary>
    private sealed class BitWriter
    {
        public int Range = 255 - 1;
        public int Value;
        public int NbBits = -8;
        private int run;
        private byte[] buffer;
        private int position;

        public BitWriter(
            int expectedSize)
        {
            this.buffer = new byte[Math.Max(expectedSize, 1024)];
        }

        public int Size => this.position;

        public ReadOnlySpan<byte> Bytes => this.buffer.AsSpan(0, this.position);

        // VP8BitWriterPos
        public long Pos => ((long)(this.position + this.run) * 8) + 8 + this.NbBits;

        private void Reserve(
            int extra)
        {
            int needed = this.position + extra;
            if (needed > this.buffer.Length)
            {
                Array.Resize(ref this.buffer, Math.Max(needed, 2 * this.buffer.Length));
            }
        }

        // Flush: the state in the fields
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Flush()
        {
            int s = 8 + this.NbBits;
            int bits = this.Value >> s;
            this.Value -= bits << s;
            this.NbBits -= 8;
            if ((bits & 0xff) != 0xff)
            {
                int pos = this.position;
                this.Reserve(this.run + 1);
                if ((bits & 0x100) != 0)
                {
                    if (pos > 0)
                    {
                        this.buffer[pos - 1]++;
                    }
                }

                if (this.run > 0)
                {
                    byte v = ((bits & 0x100) != 0) ? (byte)0x00 : (byte)0xff;
                    for (; this.run > 0; --this.run)
                    {
                        this.buffer[pos++] = v;
                    }
                }

                this.buffer[pos++] = (byte)(bits & 0xff);
                this.position = pos;
            }
            else
            {
                this.run++;
            }
        }

        // VP8PutBit on the state in locals
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Put(
            BitWriter bw,
            ref int range,
            ref int value,
            ref int nbBits,
            bool bit,
            int probability)
        {
            // Branchless: the bits are data
            int split = (range * probability) >> 8;
            int mask = bit ? -1 : 0;
            value += (split + 1) & mask;
            range = split ^ ((split ^ (range - split - 1)) & mask);
            int shift = Unsafe.Add(ref MemoryMarshal.GetReference(Norm), range);
            range = Unsafe.Add(ref MemoryMarshal.GetReference(NewRange), range);
            value <<= shift;
            nbBits += shift;
            if (nbBits > 0)
            {
                bw.Value = value;
                bw.NbBits = nbBits;
                bw.Flush();
                value = bw.Value;
                nbBits = bw.NbBits;
            }

            return bit;
        }

        public bool PutBit(
            bool bit,
            int probability)
        {
            int range = this.Range, value = this.Value, nbBits = this.NbBits;
            Put(this, ref range, ref value, ref nbBits, bit, probability);
            this.Range = range;
            this.Value = value;
            this.NbBits = nbBits;
            return bit;
        }

        public bool PutBitUniform(
            bool bit)
        {
            int split = this.Range >> 1;
            if (bit)
            {
                this.Value += split + 1;
                this.Range -= split + 1;
            }
            else
            {
                this.Range = split;
            }

            if (this.Range < 127)
            {
                this.Range = NewRange[this.Range];
                this.Value <<= 1;
                this.NbBits += 1;
                if (this.NbBits > 0)
                {
                    this.Flush();
                }
            }

            return bit;
        }

        public void PutBits(
            uint value,
            int nbBits)
        {
            for (uint mask = 1u << (nbBits - 1); mask != 0; mask >>= 1)
            {
                this.PutBitUniform((value & mask) != 0);
            }
        }

        public void PutSignedBits(
            int value,
            int nbBits)
        {
            if (!this.PutBitUniform(value != 0))
            {
                return;
            }

            if (value < 0)
            {
                this.PutBits((uint)((-value << 1) | 1), nbBits + 1);
            }
            else
            {
                this.PutBits((uint)(value << 1), nbBits + 1);
            }
        }

        // VP8BitWriterFinish
        public void Finish()
        {
            this.PutBits(0, 9 - this.NbBits);
            this.NbBits = 0;
            this.Flush();
        }
    }

    /// <summary>
    /// The token buffer (token_enc.c), in the order the tokens were added: each a bit (bit 15) and a
    /// probability's index in the coefficient probabilities, or a constant probability (bit 14 set).
    /// </summary>
    private sealed class TokenBuffer
    {
        public const ushort FixedProbaBit = 1 << 14;

        public ushort[] Tokens = [];
        public int Count;

        public void Clear()
        {
            this.Count = 0;
        }

        // Room for at least count more tokens
        public void Reserve(
            int count)
        {
            if (this.Count + count > this.Tokens.Length)
            {
                Array.Resize(ref this.Tokens, Math.Max(2 * this.Tokens.Length, this.Count + count));
            }
        }

        // VP8EmitTokens
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Emit(
            BitWriter bw,
            byte[] probas)
        {
            int range = bw.Range, value = bw.Value, nbBits = bw.NbBits;
            ref ushort tokens = ref MemoryMarshal.GetArrayDataReference(this.Tokens);
            ref byte p = ref MemoryMarshal.GetArrayDataReference(probas);
            for (int i = 0; i < this.Count; i++)
            {
                int token = Unsafe.Add(ref tokens, i);
                bool bit = (token & (1 << 15)) != 0;
                int proba = ((token & FixedProbaBit) != 0) ? token & 0xff : Unsafe.Add(ref p, token & 0x3fff);
                BitWriter.Put(bw, ref range, ref value, ref nbBits, bit, proba);
            }

            bw.Range = range;
            bw.Value = value;
            bw.NbBits = nbBits;
        }

        // VP8EstimateTokenSize
        public long EstimateSize(
            byte[] probas)
        {
            long size = 0;
            for (int i = 0; i < this.Count; i++)
            {
                int token = this.Tokens[i];
                bool bit = (token & (1 << 15)) != 0;
                if ((token & FixedProbaBit) != 0)
                {
                    size += BitCost(bit, (byte)(token & 0xff));
                }
                else
                {
                    size += BitCost(bit, probas[token & 0x3fff]);
                }
            }

            return size;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitCost(
        bool bit,
        byte probability)
    {
        return Unsafe.Add(ref MemoryMarshal.GetReference(EntropyCost), bit ? 255 - probability : probability);
    }
}
