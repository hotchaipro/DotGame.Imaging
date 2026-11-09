// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/vp8_dec.c's GetCoeffsFast and GetLargeValue, and
// src/utils/bit_reader_inl_utils.h), modified by Hot Chai Games LLC: written in C#. libwebp carries this
// notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

// The coefficient tokens, the decoder's hot loop: the boolean decoder's state is in locals here (the
// bit functions are inlined, and the rare refill at the end of the data takes and returns values), so
// it stays in registers
internal static partial class Vp8Decoder
{
    // The bits' probabilities of the large coefficients' categories 3 to 6 (kCat3456), each ending in
    // 0, at 0, 4, 9 and 15
    private static ReadOnlySpan<byte> CategoryProbas =>
    [
        173, 148, 140, 0,
        176, 155, 140, 135, 0,
        180, 157, 141, 134, 130, 0,
        254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129, 0,
    ];

    // VP8GetBit
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBit(
        ref BitReader br,
        byte[] data,
        ref byte start,
        ref ulong value,
        ref uint range,
        ref int bits,
        ref int position,
        int probability)
    {
        if (bits < 0)
        {
            if (position < br.Max)
            {
                // Seven bytes, big-endian
                ulong next = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, position))) >> 8;
                position += 7;
                value = next | (value << 56);
                bits += 56;
            }
            else
            {
                // LoadFinalBytes: a byte at a time, then zeros once, then nothing
                if (position < br.End)
                {
                    bits += 8;
                    value = Unsafe.Add(ref start, position++) | (value << 8);
                }
                else if (!br.Eof)
                {
                    value <<= 8;
                    bits += 8;
                    br.Eof = true;
                }
                else
                {
                    bits = 0;
                }
            }
        }

        // A branch, not masks: the bits' probabilities are skewed, so it's predicted well (measured:
        // masks are 10% slower)
        uint split = (range * (uint)probability) >> 8;
        uint v = (uint)(value >> bits);
        int bit;
        if (v > split)
        {
            range -= split;
            value -= (ulong)(split + 1) << bits;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }

        int shift = 7 ^ BitOperations.Log2(range);
        range <<= shift;
        bits -= shift;
        range -= 1;
        return bit;
    }

    // VP8GetSigned
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSigned(
        ref BitReader br,
        byte[] data,
        ref byte start,
        ref ulong value,
        ref uint range,
        ref int bits,
        ref int position,
        int v)
    {
        if (bits < 0)
        {
            if (position < br.Max)
            {
                ulong next = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, position))) >> 8;
                position += 7;
                value = next | (value << 56);
                bits += 56;
            }
            else
            {
                // LoadFinalBytes: a byte at a time, then zeros once, then nothing
                if (position < br.End)
                {
                    bits += 8;
                    value = Unsafe.Add(ref start, position++) | (value << 8);
                }
                else if (!br.Eof)
                {
                    value <<= 8;
                    bits += 8;
                    br.Eof = true;
                }
                else
                {
                    bits = 0;
                }
            }
        }

        uint split = range >> 1;
        uint x = (uint)(value >> bits);
        int mask = (int)(split - x) >> 31;
        bits -= 1;
        range += (uint)mask;
        range |= 1;
        value -= (ulong)((split + 1) & (uint)mask) << (bits + 1);
        return (v ^ mask) - mask;
    }

    private sealed partial class Decoding
    {
        // GetCoeffsFast: one block's coefficients from coefficient n on, dequantized; the index after
        // the last that isn't zero
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static int GetCoeffs(
            ref BitReader br,
            byte[] buf,
            byte[] probas,
            int type,
            int ctx,
            int dqDc,
            int dqAc,
            int n,
            ref short output)
        {
            ref byte start = ref MemoryMarshal.GetArrayDataReference(buf);
            ref byte prob = ref MemoryMarshal.GetArrayDataReference(probas);
            ref byte bands = ref MemoryMarshal.GetReference(Bands);
            ref byte zigzag = ref MemoryMarshal.GetReference(Zigzag);
            ref short dst = ref output;
            ulong value = br.Value;
            uint range = br.Range;
            int bits = br.Bits;
            int position = br.Position;
            int typeBase = type * NumBands * BandStride;
            int p = typeBase + (Unsafe.Add(ref bands, n) * BandStride) + (ctx * NumProbas);
            int result = 16;
            for (; n < 16; n++)
            {
                if (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p)) == 0)
                {
                    result = n;
                    break;
                }

                bool done = false;
                while (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 1)) == 0)
                {
                    p = typeBase + (Unsafe.Add(ref bands, ++n) * BandStride);
                    if (n == 16)
                    {
                        done = true;
                        break;
                    }
                }

                if (done)
                {
                    break;
                }

                int next = typeBase + (Unsafe.Add(ref bands, n + 1) * BandStride);
                int v;
                if (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 2)) == 0)
                {
                    v = 1;
                    p = next + NumProbas;
                }
                else
                {
                    // GetLargeValue, out of line with the reader's state in its fields: inlined, its
                    // dozen bit reads would leave this method more locals than the JIT keeps in
                    // registers
                    br.Value = value;
                    br.Range = range;
                    br.Bits = bits;
                    br.Position = position;
                    v = GetLargeValue(ref br, buf, ref prob, p);
                    value = br.Value;
                    range = br.Range;
                    bits = br.Bits;
                    position = br.Position;
                    p = next + (2 * NumProbas);
                }

                int signed = GetSigned(ref br, buf, ref start, ref value, ref range, ref bits, ref position, v);
                Unsafe.Add(ref dst, Unsafe.Add(ref zigzag, n)) = (short)(signed * ((n > 0) ? dqAc : dqDc));
            }

            br.Value = value;
            br.Range = range;
            br.Bits = bits;
            br.Position = position;
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int GetLargeValue(
            ref BitReader br,
            byte[] buf,
            ref byte prob,
            int p)
        {
            ref byte start = ref MemoryMarshal.GetArrayDataReference(buf);
            ref byte categories = ref MemoryMarshal.GetReference(CategoryProbas);
            ulong value = br.Value;
            uint range = br.Range;
            int bits = br.Bits;
            int position = br.Position;
            int v;
            if (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 3)) == 0)
            {
                if (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 4)) == 0)
                {
                    v = 2;
                }
                else
                {
                    v = 3 + GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 5));
                }
            }
            else if (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 6)) == 0)
            {
                if (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 7)) == 0)
                {
                    v = 5 + GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 159);
                }
                else
                {
                    v = 7 + (2 * GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 165));
                    v += GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 145);
                }
            }
            else
            {
                int bit1 = GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 8));
                int bit0 = GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, p + 9 + bit1));
                int cat = (2 * bit1) + bit0;

                // The category's probabilities, each list ending in 0, from one table (span
                // locals here would be stack the method clears on every call)
                int t = (int)((0x0f090400u >> (cat * 8)) & 0xff);
                v = 0;
                for (int probability = Unsafe.Add(ref categories, t); probability != 0; probability = Unsafe.Add(ref categories, ++t))
                {
                    v += v + GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, probability);
                }

                v += 3 + (8 << cat);
            }

            br.Value = value;
            br.Range = range;
            br.Bits = bits;
            br.Position = position;
            return v;
        }
    }
}
