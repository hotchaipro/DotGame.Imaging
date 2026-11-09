// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DotGame.Imaging.Tests;

/// <summary>
/// PNG files written and read by the PNG specification's definitions, one byte at a time, as
/// references for the codec: every filter and chunk layout, and what the encoder chose.
/// </summary>
internal static class PngFiles
{
    // IHDR's color types
    public const byte Gray = 0;
    public const byte Rgb = 2;
    public const byte GrayAlpha = 4;
    public const byte Rgba = 6;

    // The row filters
    public const byte None = 0;
    public const byte Sub = 1;
    public const byte Up = 2;
    public const byte Average = 3;
    public const byte Paeth = 4;

    // Where IHDR's color type is: past the signature (8 bytes), the chunk's length and type (8), and the
    // width, height and bit depth (9)
    public const int ColorTypeOffset = 8 + 8 + 9;

    // A PNG of the given samples with each row filtered as given, by the specification's
    // definitions, one byte at a time
    public static byte[] Write(
        int width,
        int height,
        byte colorType,
        byte bitDepth,
        byte[] samples,
        byte[] filters,
        bool headerOnly = false,
        string? extraChunk = null)
    {
        // The signature as the specification gives it, not from the codec it's a reference for
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        WriteChunk(png, "IHDR", header);
        if (headerOnly)
        {
            return png.ToArray();
        }

        if (extraChunk is not null)
        {
            WriteChunk(png, extraChunk, [1, 2, 3]);
        }

        int channels = colorType switch { Gray => 1, GrayAlpha => 2, Rgb => 3, _ => 4 };
        int bytesPerPixel = channels * bitDepth / 8;
        int stride = width * bytesPerPixel;
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(filters[y]);
            for (int i = 0; i < stride; i++)
            {
                int a = (i >= bytesPerPixel) ? samples[(y * stride) + i - bytesPerPixel] : 0;
                int b = (y > 0) ? samples[((y - 1) * stride) + i] : 0;
                int c = ((i >= bytesPerPixel) && (y > 0)) ? samples[((y - 1) * stride) + i - bytesPerPixel] : 0;
                int prediction = filters[y] switch
                {
                    None => 0,
                    Sub => a,
                    Up => b,
                    Average => (a + b) / 2,
                    _ => PaethPredictor(a, b, c),
                };
                raw.WriteByte((byte)(samples[(y * stride) + i] - prediction));
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int PaethPredictor(
        int a,
        int b,
        int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if ((pa <= pb) && (pa <= pc))
        {
            return a;
        }

        return (pb <= pc) ? b : c;
    }

    private static void WriteChunk(
        MemoryStream png,
        string type,
        byte[] data)
    {
        var bytes = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, bytes);
        data.CopyTo(bytes, 4);
        var number = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(bytes));
        png.Write(number);
    }

    // The specification's CRC, a bit at a time
    public static uint Crc(
        ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int k = 0; k < 8; k++)
            {
                crc = ((crc & 1) != 0) ? (0xedb88320 ^ (crc >> 1)) : (crc >> 1);
            }
        }

        return crc ^ 0xffffffff;
    }

    public static int FindChunk(
        byte[] png,
        ReadOnlySpan<byte> type)
    {
        int offset = 8;
        while (!png.AsSpan(offset + 4, 4).SequenceEqual(type))
        {
            offset += 12 + BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
        }

        return offset;
    }

    public static int CountChunks(
        byte[] png,
        ReadOnlySpan<byte> type)
    {
        int count = 0;
        for (int offset = 8; offset < png.Length; offset += 12 + BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset)))
        {
            if (png.AsSpan(offset + 4, 4).SequenceEqual(type))
            {
                count++;
            }
        }

        return count;
    }

    // The filter type of the first row: the first byte the image's data inflates to
    public static byte FirstRowFilter(
        byte[] png)
    {
        int idat = FindChunk(png, "IDAT"u8);
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat));
        using var zlib = new ZLibStream(new MemoryStream(png, idat + 8, length), CompressionMode.Decompress);
        return (byte)zlib.ReadByte();
    }
}
