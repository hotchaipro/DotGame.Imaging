// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace DotGame.Imaging.WebP;

/// <summary>
/// Reads and writes WebP images: lossy (VP8) with or without alpha, and lossless (VP8L). A C# port of
/// libwebp 1.6.0: its decoder's pixels, bit for bit, with libwebp's defaults (fancy upsampling), and
/// its encoder's files, byte for byte, with cwebp's settings. Animated images aren't read or written.
/// </summary>
/// <remarks>
/// Decoding is two steps, so the caller decides where the pixels go: <see cref="TryGetDecodedSize"/>
/// reads the size from the image's headers, and <see cref="Decode"/> fills a buffer of that size. To
/// read PNG, JPEG and WebP alike, use <see cref="ImageDecoder"/>. libwebp's BSD license asks that its
/// notice go with the software in binary form: the package's THIRD-PARTY-NOTICES.md has it.
/// </remarks>
public static class WebPCodec
{
    private static readonly WebPEncoderOptions Defaults = new();

    /// <summary>
    /// Reads a WebP image's size from its headers, and the size of the buffer <see cref="Decode"/>
    /// fills. An animated image still has a size (its canvas's).
    /// </summary>
    /// <param name="webp">The image's bytes: the whole file, or at least as far as its image's header.</param>
    /// <param name="layout">The layout the pixels will be decoded to.</param>
    /// <param name="width">The image's width in pixels, or 0.</param>
    /// <param name="height">The image's height in pixels, or 0.</param>
    /// <param name="byteCount">The bytes the decoded pixels take, or 0.</param>
    /// <returns>
    /// Whether the bytes are a WebP image with headers that read: false if they aren't a WebP image,
    /// if its headers are damaged or cut short, or if its pixels wouldn't fit in a buffer (2 GB).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static bool TryGetDecodedSize(
        ReadOnlySpan<byte> webp,
        PixelLayout layout,
        out int width,
        out int height,
        out int byteCount)
    {
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        if (WebPDecoder.TryReadSize(webp, out int w, out int h) && PixelLayouts.TryGetByteCount(w, h, bytesPerPixel, out byteCount))
        {
            width = w;
            height = h;
            return true;
        }

        width = 0;
        height = 0;
        byteCount = 0;
        return false;
    }

    /// <summary>
    /// Decodes a WebP image's pixels into <paramref name="pixels"/>.
    /// </summary>
    /// <param name="webp">The image's bytes.</param>
    /// <param name="pixels">
    /// Where the pixels go: exactly the byte count <see cref="TryGetDecodedSize"/> gives. Every byte is
    /// written.
    /// </param>
    /// <param name="layout">The layout the pixels are written in.</param>
    /// <param name="premultiplyAlpha">
    /// Whether the decoder multiplies each pixel's red, green and blue by its alpha, as GPU textures
    /// usually take them. A WebP file stores straight alpha, so false gives the colors as stored.
    /// </param>
    /// <exception cref="InvalidDataException">The bytes aren't a WebP image, or it's damaged.</exception>
    /// <exception cref="NotSupportedException">The image is animated, or larger than 8192 x 8192 pixels.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't the image's byte count.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static void Decode(
        ReadOnlySpan<byte> webp,
        Span<byte> pixels,
        PixelLayout layout,
        bool premultiplyAlpha)
    {
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        WebPDecoder.Decode(webp, pixels, bytesPerPixel, premultiplyAlpha);
    }

    /// <summary>
    /// Encodes pixels as a WebP image, lossy or lossless as <paramref name="options"/> say, and writes
    /// the file to <paramref name="writer"/>: with the same settings, byte for byte what cwebp 1.6.0
    /// writes.
    /// </summary>
    /// <param name="writer">Where the file's bytes go.</param>
    /// <param name="width">The image's width in pixels.</param>
    /// <param name="height">The image's height in pixels.</param>
    /// <param name="pixels">The pixels: width x height of them, in <paramref name="layout"/>.</param>
    /// <param name="layout">How <paramref name="pixels"/> are laid out.</param>
    /// <param name="unpremultiplyAlpha">
    /// Whether the encoder divides each pixel's red, green and blue by its alpha before encoding it,
    /// since a WebP file stores straight alpha: true for premultiplied pixels.
    /// </param>
    /// <param name="options">How to encode the image; null for cwebp's defaults (lossy, quality 75).</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width or height isn't 1 to 16383, the image is larger than 8192 x 8192 pixels, an option is
    /// out of its range, or <paramref name="layout"/> isn't a layout.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't width x height pixels.</exception>
    public static void Encode(
        IBufferWriter<byte> writer,
        int width,
        int height,
        ReadOnlySpan<byte> pixels,
        PixelLayout layout,
        bool unpremultiplyAlpha,
        WebPEncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        options ??= Defaults;
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        if ((width < 1) || (width > 16383) || (height < 1) || (height > 16383) || (((long)width * height) > (8192L * 8192L)))
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"A WebP image is 1 to 16383 pixels on each side, and at most 8192 x 8192 pixels; this is {width} x {height}.");
        }

        if (pixels.Length != (width * height * bytesPerPixel))
        {
            throw new ArgumentException($"A {width} x {height} image is {width * height * bytesPerPixel} bytes in this layout, but {pixels.Length} were given.", nameof(pixels));
        }

        CheckRange(options.Quality, 0, 100, nameof(options), nameof(options.Quality));
        CheckRange(options.Method, 0, 6, nameof(options), nameof(options.Method));
        CheckRange(options.AlphaQuality, 0, 100, nameof(options), nameof(options.AlphaQuality));
        CheckRange(options.NearLossless, 0, 100, nameof(options), nameof(options.NearLossless));

        byte[]? straight = null;
        if (unpremultiplyAlpha)
        {
            straight = pixels.ToArray();
            Premultiply.UndoPixels(straight);
            pixels = straight;
        }

        if (options.Lossless)
        {
            Vp8LEncoder.Encode(writer, width, height, pixels, new Vp8LEncoderOptions
            {
                Quality = options.Quality,
                Method = options.Method,
                Exact = options.Exact,
                NearLossless = options.NearLossless,
                UseThreads = options.UseThreads,
            });
        }
        else
        {
            Vp8Encoder.Encode(writer, width, height, pixels, new Vp8EncoderOptions
            {
                Quality = options.Quality,
                Method = options.Method,
                Exact = options.Exact,
                AlphaQuality = options.AlphaQuality,
                UseSharpYuv = options.UseSharpYuv,
                UseThreads = options.UseThreads,
            });
        }
    }

    private static void CheckRange(
        float value,
        float min,
        float max,
        string paramName,
        string option)
    {
        if (!(value >= min) || !(value <= max))
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"The options' {option} is {min} to {max}.");
        }
    }
}
