// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;

namespace DotGame.Imaging.Png;

/// <summary>
/// What <see cref="PngEncoder"/> and <see cref="PngDecoder"/> share: the file signature and
/// the chunks' CRC.
/// </summary>
internal static class PngChunks
{
    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    // Eight tables of 256 for slicing by 8: table k is the CRC of a byte followed by k zero bytes
    private static readonly uint[] CrcTables = CreateCrcTables();

    /// <summary>
    /// The CRC of a chunk, which covers its type and its data.
    /// </summary>
    public static uint Crc(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        return UpdateCrc(UpdateCrc(0xffffffff, type), data) ^ 0xffffffff;
    }

    // The CRC-32 of zlib and PNG (reflected, polynomial 0xedb88320): Arm64's CRC32 instructions
    // where the runtime has them (CoreCLR and NativeAOT on Arm64), otherwise 8 bytes at a time
    // from the tables (Mono and WebAssembly, x64)
    private static uint UpdateCrc(
        uint crc,
        ReadOnlySpan<byte> data)
    {
        ref byte bytes = ref MemoryMarshal.GetReference(data);
        int length = data.Length;
        int i = 0;
        if (Crc32.Arm64.IsSupported)
        {
            for (; i + 8 <= length; i += 8)
            {
                crc = Crc32.Arm64.ComputeCrc32(crc, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, i)));
            }

            for (; i < length; i++)
            {
                crc = Crc32.ComputeCrc32(crc, Unsafe.Add(ref bytes, i));
            }

            return crc;
        }

        ref uint tables = ref MemoryMarshal.GetArrayDataReference(CrcTables);
        for (; i + 8 <= length; i += 8)
        {
            // Little-endian, as every platform this library supports is
            uint low = crc ^ Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, i));
            uint high = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bytes, i + 4));
            crc = Unsafe.Add(ref tables, (7 * 256) + (int)(low & 0xff))
                ^ Unsafe.Add(ref tables, (6 * 256) + (int)((low >> 8) & 0xff))
                ^ Unsafe.Add(ref tables, (5 * 256) + (int)((low >> 16) & 0xff))
                ^ Unsafe.Add(ref tables, (4 * 256) + (int)(low >> 24))
                ^ Unsafe.Add(ref tables, (3 * 256) + (int)(high & 0xff))
                ^ Unsafe.Add(ref tables, (2 * 256) + (int)((high >> 8) & 0xff))
                ^ Unsafe.Add(ref tables, 256 + (int)((high >> 16) & 0xff))
                ^ Unsafe.Add(ref tables, (int)(high >> 24));
        }

        for (; i < length; i++)
        {
            crc = Unsafe.Add(ref tables, (int)((crc ^ Unsafe.Add(ref bytes, i)) & 0xff)) ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTables()
    {
        var tables = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = ((c & 1) != 0) ? (0xedb88320 ^ (c >> 1)) : (c >> 1);
            }

            tables[n] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int n = 0; n < 256; n++)
            {
                uint previous = tables[((k - 1) * 256) + n];
                tables[(k * 256) + n] = tables[(int)(previous & 0xff)] ^ (previous >> 8);
            }
        }

        return tables;
    }
}
