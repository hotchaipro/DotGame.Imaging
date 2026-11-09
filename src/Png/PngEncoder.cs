// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.Png;

/// <summary>
/// The PNG encoder (<see cref="PngCodec.Encode"/>): pixels as little-endian RGBA words, red in the low
/// byte, to a PNG file of 8-bit samples.
/// </summary>
internal static class PngEncoder
{
    private const byte Grayscale = 0;
    private const byte Rgb = 2;
    private const byte GrayscaleAlpha = 4;
    private const byte Rgba = 6;

    private const byte NoFilter = 0;
    private const byte PaethFilter = 4;

    // The most compressed bytes in an IDAT chunk: the image's data is written in chunks of this
    // size as it's compressed, so it's never held whole
    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Writes the pixels (width x height of them, checked by the caller) as a PNG file.
    /// </summary>
    public static void Encode(
        IBufferWriter<byte> writer,
        int width,
        int height,
        ReadOnlySpan<byte> rgba,
        bool unpremultiply,
        PngCompressionLevel level)
    {
        var source = MemoryMarshal.Cast<byte, uint>(rgba);
        bool small = (level == PngCompressionLevel.Optimal) || (level == PngCompressionLevel.SmallestSize);
        byte colorType = small ? ChooseColorType(source) : Rgba;
        int channels = colorType switch
        {
            Grayscale => 1,
            GrayscaleAlpha => 2,
            Rgb => 3,
            _ => 4,
        };

        var signature = writer.GetSpan(PngChunks.Signature.Length);
        PngChunks.Signature.CopyTo(signature);
        writer.Advance(PngChunks.Signature.Length);

        // Header: width, height, 8 bits per sample, the color type, deflate, adaptive filtering
        // (a filter per row), no interlacing
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4), height);
        header[8] = 8;
        header[9] = colorType;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(writer, "IHDR"u8, header);

        var compression = level switch
        {
            PngCompressionLevel.Fastest => CompressionLevel.Fastest,
            PngCompressionLevel.NoCompression => CompressionLevel.NoCompression,
            PngCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        int stride = width * channels;
        using (var idat = new IdatStream(writer))
        {
            using var zlib = new ZLibStream(idat, compression, leaveOpen: true);
            if ((colorType == Rgba) && !unpremultiply && !small)
            {
                // Unfiltered rows of the pixels as they are: nothing to convert, so no copy of them
                Span<byte> filter = [NoFilter];
                for (int y = 0; y < height; y++)
                {
                    zlib.Write(filter);
                    zlib.Write(rgba.Slice(y * stride, stride));
                }
            }
            else
            {
                // The samples in that color type, straight alpha, with a row of zeros (the row above
                // the first) before them and room for a vector after them
                var samples = new byte[(stride * (height + 1)) + 16];
                WriteSamples(source, unpremultiply, colorType, samples.AsSpan(stride, stride * height));
                byte filter = small ? ChooseFilter(samples, stride, height, channels) : NoFilter;
                var row = new byte[stride + 1 + 16];
                for (int y = 0; y < height; y++)
                {
                    FilterRow(filter, samples, stride, y, channels, row);
                    zlib.Write(row, 0, stride + 1);
                }
            }
        }

        WriteChunk(writer, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    // The smallest color type that holds every pixel: alpha can go if every pixel is opaque,
    // and color if every pixel's red, green and blue are the same (as they are after dividing
    // by alpha if they are before)
    private static byte ChooseColorType(
        ReadOnlySpan<uint> pixels)
    {
        uint allAlpha = 0xff000000;
        uint colorDifferences = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint pixel = pixels[i];
            allAlpha &= pixel;
            colorDifferences |= (pixel ^ (pixel >> 8)) & 0xffff;
        }

        bool opaque = (allAlpha == 0xff000000);
        if (colorDifferences == 0)
        {
            return opaque ? Grayscale : GrayscaleAlpha;
        }

        return opaque ? Rgb : Rgba;
    }

    private static void WriteSamples(
        ReadOnlySpan<uint> pixels,
        bool unpremultiply,
        byte colorType,
        Span<byte> samples)
    {
        ref byte target = ref MemoryMarshal.GetReference(samples);
        switch (colorType)
        {
            case Rgba:
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint pixel = pixels[i];
                    if (unpremultiply)
                    {
                        pixel = Unpremultiply(pixel);
                    }

                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i * 4), pixel);
                }

                break;

            case Rgb:
                // Opaque, so there's nothing to divide
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint pixel = pixels[i];
                    ref byte p = ref Unsafe.Add(ref target, i * 3);
                    p = (byte)pixel;
                    Unsafe.Add(ref p, 1) = (byte)(pixel >> 8);
                    Unsafe.Add(ref p, 2) = (byte)(pixel >> 16);
                }

                break;

            case GrayscaleAlpha:
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint pixel = pixels[i];
                    if (unpremultiply)
                    {
                        pixel = Unpremultiply(pixel);
                    }

                    ref byte p = ref Unsafe.Add(ref target, i * 2);
                    p = (byte)pixel;
                    Unsafe.Add(ref p, 1) = (byte)(pixel >> 24);
                }

                break;

            default:
                for (int i = 0; i < pixels.Length; i++)
                {
                    Unsafe.Add(ref target, i) = (byte)pixels[i];
                }

                break;
        }
    }

    // Divides a premultiplied color by its alpha, rounding to the nearest
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Unpremultiply(
        uint pixel)
    {
        uint a = pixel >> 24;
        if ((a == 0) || (a == 255))
        {
            return pixel;
        }

        uint half = a / 2;
        uint r = Math.Min(255, (((pixel & 0xff) * 255) + half) / a);
        uint g = Math.Min(255, ((((pixel >> 8) & 0xff) * 255) + half) / a);
        uint b = Math.Min(255, ((((pixel >> 16) & 0xff) * 255) + half) / a);
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    // Unfiltered rows compress best for flat-colored art (repeats that filtering would break up),
    // Paeth for photographs, gradients and screenshots: every eighth group of eight rows, both
    // ways, compressed quickly, says which
    private static byte ChooseFilter(
        byte[] samples,
        int stride,
        int height,
        int channels)
    {
        long unfiltered = 0;
        long paeth = 0;
        var row = new byte[stride + 1 + 16];
        for (int pass = 0; pass < 2; pass++)
        {
            byte filter = (pass == 0) ? NoFilter : PaethFilter;
            using var sample = new MemoryStream();
            using (var zlib = new ZLibStream(sample, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    if (((y >> 3) & 7) == 0)
                    {
                        FilterRow(filter, samples, stride, y, channels, row);
                        zlib.Write(row, 0, stride + 1);
                    }
                }
            }

            if (pass == 0)
            {
                unfiltered = sample.Length;
            }
            else
            {
                paeth = sample.Length;
            }
        }

        return (paeth < unfiltered) ? PaethFilter : NoFilter;
    }

    // A row (0 is the top) after its filter type, in row; samples has a row of zeros first
    private static void FilterRow(
        byte filter,
        byte[] samples,
        int stride,
        int y,
        int channels,
        byte[] row)
    {
        row[0] = filter;
        var current = samples.AsSpan(stride * (y + 1), stride);
        if (filter == NoFilter)
        {
            current.CopyTo(row.AsSpan(1));
            return;
        }

        ref byte output = ref row[1];
        ref byte pixel = ref MemoryMarshal.GetReference(current);
        ref byte above = ref samples[stride * y];

        // The first pixel has nothing to its left or above-left: Paeth predicts the byte above
        for (int i = 0; i < channels; i++)
        {
            Unsafe.Add(ref output, i) = (byte)(Unsafe.Add(ref pixel, i) - Unsafe.Add(ref above, i));
        }

        // The rest, 8 bytes at a time as 16-bit values: each prediction is from the unfiltered
        // samples, so the bytes don't wait on each other. The last vector may run past the row
        // into the next one or the room after the samples, and its extra bytes are ignored.
        for (int i = channels; i < stride; i += 8)
        {
            var left = Widen(ref Unsafe.Add(ref pixel, i - channels));
            var up = Widen(ref Unsafe.Add(ref above, i));
            var upLeft = Widen(ref Unsafe.Add(ref above, i - channels));
            var value = Widen(ref Unsafe.Add(ref pixel, i));

            var upMinusUpLeft = up - upLeft;
            var leftMinusUpLeft = left - upLeft;
            var toLeft = Vector128.Abs(upMinusUpLeft);
            var toUp = Vector128.Abs(leftMinusUpLeft);
            var toUpLeft = Vector128.Abs(upMinusUpLeft + leftMinusUpLeft);
            var nearer = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(toUp, toUpLeft), up, upLeft);
            var leftNearest = Vector128.LessThanOrEqual(toLeft, toUp) & Vector128.LessThanOrEqual(toLeft, toUpLeft);
            var difference = value - Vector128.ConditionalSelect(leftNearest, left, nearer);
            ulong bytes = Vector128.Narrow(difference.AsUInt16(), difference.AsUInt16()).AsUInt64().ToScalar();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, i), bytes);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen(
        ref byte bytes)
    {
        return Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref bytes)).AsByte()).AsInt16();
    }

    // A chunk straight into the writer's memory: its data's length, its type, its data, and the CRC
    // of its type and data
    private static void WriteChunk(
        IBufferWriter<byte> writer,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        var chunk = writer.GetSpan(12 + data.Length);
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        type.CopyTo(chunk.Slice(4));
        data.CopyTo(chunk.Slice(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.Slice(8 + data.Length), PngChunks.Crc(type, data));
        writer.Advance(12 + data.Length);
    }

    // The compressed rows, written to the writer as IDAT chunks of up to ChunkSize bytes as they
    // come (a PNG's image data may be split across chunks anywhere)
    private sealed class IdatStream(IBufferWriter<byte> writer) : Stream
    {
        private readonly byte[] buffer = new byte[ChunkSize];
        private int count;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(
            ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int take = Math.Min(data.Length, ChunkSize - this.count);
                data.Slice(0, take).CopyTo(this.buffer.AsSpan(this.count));
                this.count += take;
                data = data.Slice(take);
                if (this.count == ChunkSize)
                {
                    this.Flush();
                }
            }
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            this.Write(buffer.AsSpan(offset, count));
        }

        public override void WriteByte(
            byte value)
        {
            this.Write([value]);
        }

        public override void Flush()
        {
            if (this.count > 0)
            {
                WriteChunk(writer, "IDAT"u8, this.buffer.AsSpan(0, this.count));
                this.count = 0;
            }
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) => throw new NotSupportedException();

        public override long Seek(
            long offset,
            SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(
            long value) => throw new NotSupportedException();

        protected override void Dispose(
            bool disposing)
        {
            if (disposing)
            {
                this.Flush();
            }

            base.Dispose(disposing);
        }
    }
}
