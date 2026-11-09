// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/utils/bit_reader_utils.c and .h, the VP8L bit reader), modified by
// Hot Chai Games LLC: written in C#, the bytes shifted in at once where eight are left. libwebp carries
// this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

/// <summary>
/// VP8L's bit reader: bits from the low end of 64 prefetched little-endian bits, as libwebp reads
/// them (its end-of-stream rule included, which says whether an image decodes). Its hot methods are
/// inlined and its slow paths take and return it by value, so a decoder's loop keeps it in registers.
/// </summary>
internal struct Vp8LBitReader
{
    public const int MaxBitsRead = 24;

    public ulong Value;
    public int BitPos;
    public int Pos;
    public int Length;
    public bool Eos;

    public static Vp8LBitReader Create(
        byte[] data,
        int length)
    {
        var reader = default(Vp8LBitReader);
        reader.Length = length;
        int prefetch = (length > 8) ? 8 : length;
        ulong value = 0;
        for (int i = 0; i < prefetch; i++)
        {
            value |= (ulong)data[i] << (8 * i);
        }

        reader.Value = value;
        reader.Pos = prefetch;
        return reader;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint Prefetch()
    {
        return (uint)(this.Value >> (this.BitPos & 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsEndOfStream()
    {
        return this.Eos || ((this.Pos == this.Length) && (this.BitPos > 64));
    }

    /// <summary>At least 32 bits ready (libwebp's VP8LFillBitWindow).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill(
        byte[] data)
    {
        if (this.BitPos >= 32)
        {
            if (this.Pos + 8 < this.Length)
            {
                this.Value >>= 32;
                this.BitPos -= 32;
                this.Value |= (ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), this.Pos)) << 32;
                this.Pos += 4;
            }
            else
            {
                this.Take(ShiftBytesSlowly(this, data));
            }
        }
    }

    /// <summary>
    /// libwebp's ShiftBytes: whole bytes consumed are shifted out and as many new ones shifted in.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ShiftBytes(
        byte[] data)
    {
        int bytes = this.BitPos >> 3;
        if ((bytes != 0) && (bytes < 8) && (this.Pos + 8 <= this.Length))
        {
            // The next eight bytes are there, so the shift is one: the same as a byte at a time
            ulong next = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), this.Pos));
            this.Value = (this.Value >> (8 * bytes)) | (next << (64 - (8 * bytes)));
            this.Pos += bytes;
            this.BitPos -= 8 * bytes;
            return;
        }

        if (bytes != 0)
        {
            this.Take(ShiftBytesSlowly(this, data));
        }
    }

    // A slow path's result copied in a field at a time, so the JIT doesn't make the caller's reader the
    // call's return buffer (which would keep it on the stack in a decoder's loop)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Take(
        Vp8LBitReader reader)
    {
        this.Value = reader.Value;
        this.BitPos = reader.BitPos;
        this.Pos = reader.Pos;
        this.Eos = reader.Eos;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vp8LBitReader ShiftBytesSlowly(
        Vp8LBitReader reader,
        byte[] data)
    {
        while ((reader.BitPos >= 8) && (reader.Pos < reader.Length))
        {
            reader.Value >>= 8;
            reader.Value |= (ulong)data[reader.Pos] << 56;
            reader.Pos++;
            reader.BitPos -= 8;
        }

        if (reader.IsEndOfStream())
        {
            reader.Eos = true;
            reader.BitPos = 0;
        }

        return reader;
    }

    public void SetEndOfStream()
    {
        this.Eos = true;
        this.BitPos = 0;
    }

    /// <summary>Reads up to 24 bits (libwebp's VP8LReadBits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(
        byte[] data,
        int count)
    {
        if (!this.Eos && (count <= MaxBitsRead))
        {
            uint value = this.Prefetch() & ((1u << count) - 1);
            this.BitPos += count;
            this.ShiftBytes(data);
            return value;
        }

        this.Eos = true;
        this.BitPos = 0;
        return 0;
    }
}
