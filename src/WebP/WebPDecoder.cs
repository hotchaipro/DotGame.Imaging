// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/webp_dec.c), modified by Hot Chai Games LLC: written in C#, files
// read whole. libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers.Binary;
using System.IO;
using DotGame.Imaging.Png;

namespace DotGame.Imaging.WebP;

/// <summary>
/// The WebP container (RIFF): a lossy image (a VP8 chunk), a lossless one (VP8L), or an extended one
/// (VP8X) with a lossy image and its alpha (ALPH) or a lossless image, and chunks that don't change
/// the pixels (ICCP, EXIF, XMP). Animated images aren't read.
/// </summary>
internal static class WebPDecoder
{
    // The most pixels an image may have: 8192 x 8192, which is 256 MB of pixels
    private const long MaxPixels = 8192L * 8192L;

    private const uint AnimationFlag = 0x02;

    /// <summary>Whether the bytes start as a WebP file does ("RIFF", its size, "WEBP").</summary>
    public static bool HasSignature(
        ReadOnlySpan<byte> data)
    {
        return (data.Length >= 12) && data.Slice(0, 4).SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);
    }

    /// <summary>
    /// The image's size, from its VP8X, VP8 or VP8L header: false if the bytes aren't a WebP file or
    /// the headers are damaged or cut short. An animated image has a size, from its VP8X header.
    /// </summary>
    public static bool TryReadSize(
        ReadOnlySpan<byte> data,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        try
        {
            var file = Parse(data, sizeOnly: true);
            width = file.Width;
            height = file.Height;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes a WebP file to 8-bit RGBA into <paramref name="pixels"/> (the image's byte count in a
    /// layout of <paramref name="bytesPerPixel"/>, 4: RGBA): libwebp's pixels with its defaults (fancy
    /// upsampling), straight alpha, or premultiplied as <see cref="PngCodec"/> does.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes aren't a WebP file, or it's damaged.</exception>
    /// <exception cref="NotSupportedException">The image is animated, or larger than 8192 x 8192 pixels.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't the image's byte count.</exception>
    public static void Decode(
        ReadOnlySpan<byte> data,
        Span<byte> pixels,
        int bytesPerPixel,
        bool premultiply)
    {
        if (!HasSignature(data))
        {
            throw new InvalidDataException("The bytes aren't a WebP image.");
        }

        var file = Parse(data, sizeOnly: false);
        if (file.Animated)
        {
            throw new NotSupportedException("Animated WebP images aren't supported.");
        }

        if ((long)file.Width * file.Height > MaxPixels)
        {
            throw new NotSupportedException($"The WebP image ({file.Width} x {file.Height}) is larger than 8192 x 8192 pixels.");
        }

        PixelLayouts.CheckBuffer(file.Width, file.Height, bytesPerPixel, pixels.Length, nameof(pixels));
        var image = data.Slice(file.ImageOffset, file.ImageLength);
        bool hasAlpha;
        if (file.Lossless)
        {
            // Its alpha is decoded as it's stored, whatever its header says of it
            Vp8LDecoder.Decode(image, file.Width, file.Height, pixels);
            hasAlpha = true;
        }
        else
        {
            Vp8Decoder.Decode(image, file.Width, file.Height, pixels);
            hasAlpha = file.AlphaLength > 0;
            if (hasAlpha)
            {
                AlphaDecoder.Decode(data.Slice(file.AlphaOffset, file.AlphaLength), file.Width, file.Height, pixels);
            }
        }

        if (premultiply && hasAlpha)
        {
            Premultiply.Pixels(pixels);
        }
    }

    private ref struct WebPFile
    {
        public int Width;
        public int Height;
        public bool Animated;
        public bool Lossless;
        public int ImageOffset;
        public int ImageLength;
        public int AlphaOffset;
        public int AlphaLength;
    }

    // The chunks, as libwebp's ParseHeadersInternal reads them, of a file that's whole
    private static WebPFile Parse(
        ReadOnlySpan<byte> data,
        bool sizeOnly)
    {
        var file = default(WebPFile);
        if (!HasSignature(data))
        {
            throw new InvalidDataException("The bytes aren't a WebP image.");
        }

        // RIFF: its size covers "WEBP" and the chunks after it, which must hold at least one header
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
        if ((riffSize < 12) || (riffSize > uint.MaxValue - 10))
        {
            throw new InvalidDataException("The WebP image's RIFF header is malformed.");
        }

        // Bytes after the RIFF chunk are ignored; a file cut short is damaged
        if (!sizeOnly && (riffSize > (uint)(data.Length - 8)))
        {
            throw new InvalidDataException("The WebP image is cut short.");
        }

        int end = (int)Math.Min((long)riffSize + 8, data.Length);
        int position = 12;
        bool extended = false;
        if (Tag(data, position, end, "VP8X"u8))
        {
            uint size = ChunkSize(data, position, end);
            if (size != 10)
            {
                throw new InvalidDataException("The WebP image's VP8X chunk is malformed.");
            }

            Need(position + 18, end);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 8));
            file.Width = 1 + (data[position + 12] | (data[position + 13] << 8) | (data[position + 14] << 16));
            file.Height = 1 + (data[position + 15] | (data[position + 16] << 8) | (data[position + 17] << 16));
            if ((long)file.Width * file.Height >= (1L << 32))
            {
                throw new InvalidDataException("The WebP image's VP8X chunk is malformed.");
            }

            file.Animated = (flags & AnimationFlag) != 0;
            extended = true;
            position += 18;
            if (file.Animated)
            {
                // Its frames are ANMF chunks: the canvas is its size
                return file;
            }

            // Chunks before the image: ALPH is the lossy image's alpha, the rest don't change pixels
            while (true)
            {
                Need(position + 8, end);
                uint size2 = ChunkSize(data, position, end);
                if (Tag(data, position, end, "VP8 "u8) || Tag(data, position, end, "VP8L"u8))
                {
                    break;
                }

                long next = position + 8 + (((long)size2 + 1) & ~1L);
                if (next > end)
                {
                    throw new InvalidDataException("The WebP image is cut short.");
                }

                if (Tag(data, position, end, "ALPH"u8))
                {
                    file.AlphaOffset = position + 8;
                    file.AlphaLength = (int)size2;
                }

                position = (int)next;
            }
        }

        // The image: VP8 (lossy) or VP8L (lossless)
        Need(position + 8, end);
        bool lossless = Tag(data, position, end, "VP8L"u8);
        if (!lossless && !Tag(data, position, end, "VP8 "u8))
        {
            throw new InvalidDataException("The WebP image has no VP8 or VP8L chunk.");
        }

        uint imageSize = ChunkSize(data, position, end);
        if ((long)position + 8 + imageSize > end)
        {
            if (!sizeOnly)
            {
                throw new InvalidDataException("The WebP image is cut short.");
            }

            imageSize = (uint)(end - position - 8);
        }

        file.Lossless = lossless;
        file.ImageOffset = position + 8;
        file.ImageLength = (int)imageSize;
        var image = data.Slice(file.ImageOffset, file.ImageLength);
        int width, height;
        bool read = lossless ? Vp8LDecoder.TryReadSize(image, out width, out height) : Vp8Decoder.TryReadSize(image, out width, out height);
        if (!read)
        {
            throw new InvalidDataException(lossless ? "The WebP image's VP8L header is malformed." : "The WebP image's VP8 frame header is malformed.");
        }

        if (extended && ((width != file.Width) || (height != file.Height)))
        {
            throw new InvalidDataException("The WebP image's VP8X size isn't its image's.");
        }

        if (lossless)
        {
            // A lossless image holds its own alpha
            file.AlphaLength = 0;
        }

        file.Width = width;
        file.Height = height;
        return file;
    }

    private static bool Tag(
        ReadOnlySpan<byte> data,
        int position,
        int end,
        ReadOnlySpan<byte> tag)
    {
        return (position + 4 <= end) && data.Slice(position, 4).SequenceEqual(tag);
    }

    private static uint ChunkSize(
        ReadOnlySpan<byte> data,
        int position,
        int end)
    {
        Need(position + 8, end);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4));
        if (size > uint.MaxValue - 10)
        {
            throw new InvalidDataException("A chunk of the WebP image is malformed.");
        }

        return size;
    }

    private static void Need(
        long position,
        int end)
    {
        if (position > end)
        {
            throw new InvalidDataException("The WebP image is cut short.");
        }
    }
}
