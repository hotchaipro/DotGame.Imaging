// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DotGame.Imaging.Png;

/// <summary>
/// The PNG decoder (<see cref="PngCodec.TryGetDecodedSize"/> and <see cref="PngCodec.Decode"/>): pixels as
/// little-endian RGBA words, red in the low byte.
/// </summary>
internal static partial class PngDecoder
{
    // The most pixels an image may have: 8192 x 8192, which is 256 MB of pixels
    public const long MaxPixels = 8192L * 8192L;

    private const byte Grayscale = 0;
    private const byte Rgb = 2;
    private const byte Indexed = 3;
    private const byte GrayscaleAlpha = 4;
    private const byte Rgba = 6;

    // Adam7 interlacing: each pass's first column and row, and the steps between them
    private static readonly int[] PassXStart = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] PassYStart = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] PassXStep = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] PassYStep = [8, 8, 8, 4, 4, 2, 2];

    /// <summary>Whether the bytes start with a PNG file's signature.</summary>
    public static bool HasSignature(
        ReadOnlySpan<byte> data)
    {
        return (data.Length >= PngChunks.Signature.Length) && data.Slice(0, PngChunks.Signature.Length).SequenceEqual(PngChunks.Signature);
    }

    /// <summary>
    /// The image's size from its header, which a PNG file has first: false if the bytes aren't a PNG
    /// image or the header is damaged.
    /// </summary>
    public static bool TryReadSize(
        ReadOnlySpan<byte> png,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        int offset = PngChunks.Signature.Length;
        if (!HasSignature(png) || (png.Length < offset + 12 + 13))
        {
            return false;
        }

        var type = png.Slice(offset + 4, 4);
        var data = png.Slice(offset + 8, 13);
        if ((BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset)) != 13) || !type.SequenceEqual("IHDR"u8)
            || (BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + 13)) != PngChunks.Crc(type, data)))
        {
            return false;
        }

        try
        {
            var image = new ImageInfo();
            ReadHeader(data, ref image, checkSize: false);
            width = image.Width;
            height = image.Height;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes a PNG image into <paramref name="pixels"/>, which is the image's byte count in a layout
    /// of <paramref name="bytesPerPixel"/> (4: RGBA), the top row first: straight alpha as the file
    /// stores it, or premultiplied.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes aren't a PNG image, or the image is damaged.</exception>
    /// <exception cref="NotSupportedException">
    /// The image has more than 8192 x 8192 pixels in all (67,108,864, whatever its shape), or has a
    /// critical chunk this decoder doesn't know.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't the image's byte count.</exception>
    public static void Decode(
        ReadOnlySpan<byte> png,
        Span<byte> pixels,
        int bytesPerPixel,
        bool premultiply)
    {
        if (!HasSignature(png))
        {
            throw new InvalidDataException("The bytes aren't a PNG image.");
        }

        var image = new ImageInfo();
        bool hasHeader = false;
        bool ended = false;
        using var compressed = new MemoryStream(png.Length);

        int offset = PngChunks.Signature.Length;
        while (!ended)
        {
            // Each chunk is its data's length, its type, its data, and a CRC of its type and data
            if (png.Length - offset < 12)
            {
                throw new InvalidDataException("The PNG image is cut short.");
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset));
            if ((length < 0) || (length > png.Length - offset - 12))
            {
                throw new InvalidDataException("The PNG image is cut short.");
            }

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset + 8 + length));
            if (crc != PngChunks.Crc(type, data))
            {
                throw new InvalidDataException("A chunk of the PNG image is damaged (its CRC doesn't match).");
            }

            offset += 12 + length;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (hasHeader)
                {
                    throw new InvalidDataException("The PNG image has two headers.");
                }

                ReadHeader(data, ref image, checkSize: true);
                PixelLayouts.CheckBuffer(image.Width, image.Height, bytesPerPixel, pixels.Length, nameof(pixels));
                hasHeader = true;
            }
            else if (!hasHeader)
            {
                throw new InvalidDataException("The PNG image doesn't start with its header.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if ((data.Length == 0) || (data.Length % 3 != 0) || (data.Length > 256 * 3))
                {
                    throw new InvalidDataException("The PNG image's palette is malformed.");
                }

                image.Palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                ReadTransparency(data, ref image);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                ended = true;
            }
            else if ((type[0] & 0x20) == 0)
            {
                // A critical chunk (its type starts with a capital) that this decoder doesn't
                // know: the image can't be read without it
                throw new NotSupportedException($"The PNG image has a critical chunk this decoder doesn't know ({Encoding.ASCII.GetString(type)}).");
            }
        }

        if ((image.ColorType == Indexed) && (image.Palette is null))
        {
            throw new InvalidDataException("The PNG image has no palette.");
        }

        // Every pass's rows, each after its filter type byte, compressed together: inflated a row
        // at a time, so each row is unfiltered and converted while it's in the cache
        // RGBA words, written as they're read: every pixel is (each pass covers its own)
        var words = MemoryMarshal.Cast<byte, uint>(pixels);
        var rows = new RowReader(image, premultiply);
        // The zlib stream's header, then its deflate data: inflated without checking zlib's
        // Adler-32 at its end, since each chunk's CRC already covers those bytes (and the
        // runtime's zlib computes it without vectors, a sixth of the time)
        var header = compressed.GetBuffer();
        if ((compressed.Length < 2) || ((header[0] & 0x0f) != 8) || ((header[0] >> 4) > 7) || ((header[1] & 0x20) != 0) || ((((header[0] << 8) | header[1]) % 31) != 0))
        {
            throw new InvalidDataException("The PNG image's pixels aren't zlib data this decoder can read.");
        }

        compressed.Position = 2;
        using (var zlib = new DeflateStream(compressed, CompressionMode.Decompress))
        {
            if (image.Interlaced)
            {
                for (int pass = 0; pass < 7; pass++)
                {
                    rows.ReadPass(zlib, words, PassXStart[pass], PassYStart[pass], PassXStep[pass], PassYStep[pass]);
                }
            }
            else
            {
                rows.ReadPass(zlib, words, 0, 0, 1, 1);
            }
        }
    }

    private static void ReadHeader(
        ReadOnlySpan<byte> data,
        ref ImageInfo image,
        bool checkSize)
    {
        if (data.Length != 13)
        {
            throw new InvalidDataException("The PNG image's header is malformed.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(data);
        int height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4));
        byte bitDepth = data[8];
        byte colorType = data[9];

        if ((width <= 0) || (height <= 0))
        {
            throw new InvalidDataException("The PNG image has no pixels.");
        }

        if (checkSize && ((long)width * height > MaxPixels))
        {
            throw new NotSupportedException($"The PNG image ({width} x {height}) has more than 8192 x 8192 pixels in all.");
        }

        bool validDepth = colorType switch
        {
            Grayscale => (bitDepth == 1) || (bitDepth == 2) || (bitDepth == 4) || (bitDepth == 8) || (bitDepth == 16),
            Indexed => (bitDepth == 1) || (bitDepth == 2) || (bitDepth == 4) || (bitDepth == 8),
            Rgb or GrayscaleAlpha or Rgba => (bitDepth == 8) || (bitDepth == 16),
            _ => false,
        };

        if (!validDepth)
        {
            throw new InvalidDataException($"The PNG image's color type ({colorType}) and bit depth ({bitDepth}) aren't valid together.");
        }

        // Compression and filtering have one method each; interlacing is none or Adam7
        if ((data[10] != 0) || (data[11] != 0) || (data[12] > 1))
        {
            throw new InvalidDataException("The PNG image's header is malformed.");
        }

        image.Width = width;
        image.Height = height;
        image.BitDepth = bitDepth;
        image.ColorType = colorType;
        image.Channels = colorType switch
        {
            Rgb => 3,
            GrayscaleAlpha => 2,
            Rgba => 4,
            _ => 1,
        };
        image.Interlaced = (data[12] == 1);
    }

    private static void ReadTransparency(
        ReadOnlySpan<byte> data,
        ref ImageInfo image)
    {
        switch (image.ColorType)
        {
            case Indexed:
                // An alpha per palette entry, from the first; the rest are opaque
                image.PaletteAlpha = data.ToArray();
                break;

            case Grayscale when data.Length >= 2:
                image.TransparentGray = BinaryPrimitives.ReadUInt16BigEndian(data);
                break;

            case Rgb when data.Length >= 6:
                image.TransparentRed = BinaryPrimitives.ReadUInt16BigEndian(data);
                image.TransparentGreen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2));
                image.TransparentBlue = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4));
                image.HasTransparentColor = true;
                break;
        }
    }

    private static int PassSize(
        int size,
        int start,
        int step)
    {
        return (size > start) ? ((size - start + step - 1) / step) : 0;
    }

    private static int RowLength(
        in ImageInfo image,
        int width)
    {
        return checked((int)((((long)width * image.Channels * image.BitDepth) + 7) / 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(
        byte r,
        byte g,
        byte b,
        byte a)
    {
        return r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);
    }

    private struct ImageInfo
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public byte ColorType;
        public int Channels;
        public bool Interlaced;
        public byte[]? Palette;
        public byte[]? PaletteAlpha;
        public int TransparentGray;
        public int TransparentRed;
        public int TransparentGreen;
        public int TransparentBlue;
        public bool HasTransparentColor;

        public ImageInfo()
        {
            this.TransparentGray = -1;
        }
    }
}
