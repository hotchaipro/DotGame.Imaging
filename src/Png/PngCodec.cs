// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace DotGame.Imaging.Png;

/// <summary>
/// Reads and writes PNG images: every color type and bit depth, palettes, transparency and
/// interlacing when reading, 8 bits a sample when writing. The same on every platform.
/// </summary>
/// <remarks>
/// <para>
/// Written from the PNG specification; the deflate compression is .NET's zlib
/// (<c>System.IO.Compression</c>).
/// </para>
/// <para>
/// Decoding is two steps, so the caller decides where the pixels go (a new array, a pooled buffer, an
/// array of the app's own color type as bytes): <see cref="TryGetDecodedSize"/> reads the size
/// from the image's header, and <see cref="Decode"/> fills a buffer of that size. To read PNG, JPEG
/// and WebP alike, use <see cref="ImageDecoder"/>.
/// </para>
/// </remarks>
public static class PngCodec
{
    /// <summary>
    /// Reads a PNG image's size from its header, and the size of the buffer <see cref="Decode"/> fills.
    /// </summary>
    /// <param name="png">The image's bytes: the whole file, or at least its first 33 bytes.</param>
    /// <param name="layout">The layout the pixels will be decoded to.</param>
    /// <param name="width">The image's width in pixels, or 0.</param>
    /// <param name="height">The image's height in pixels, or 0.</param>
    /// <param name="byteCount">The bytes the decoded pixels take, or 0.</param>
    /// <returns>
    /// Whether the bytes are a PNG image with a header that reads: false if they aren't a PNG image,
    /// if its header is damaged or cut short, or if its pixels wouldn't fit in a buffer (2 GB).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static bool TryGetDecodedSize(
        ReadOnlySpan<byte> png,
        PixelLayout layout,
        out int width,
        out int height,
        out int byteCount)
    {
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        if (PngDecoder.TryReadSize(png, out int w, out int h) && PixelLayouts.TryGetByteCount(w, h, bytesPerPixel, out byteCount))
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
    /// Decodes a PNG image's pixels into <paramref name="pixels"/>.
    /// </summary>
    /// <param name="png">The image's bytes.</param>
    /// <param name="pixels">
    /// Where the pixels go: exactly the byte count <see cref="TryGetDecodedSize"/> gives. Every byte is
    /// written.
    /// </param>
    /// <param name="layout">The layout the pixels are written in.</param>
    /// <param name="premultiplyAlpha">
    /// Whether the decoder multiplies each pixel's red, green and blue by its alpha, as GPU textures
    /// usually take them. A PNG file stores straight alpha, so false gives the colors as stored.
    /// </param>
    /// <exception cref="InvalidDataException">The bytes aren't a PNG image, or it's damaged.</exception>
    /// <exception cref="NotSupportedException">
    /// The image has more than 8192 x 8192 pixels in all (67,108,864, whatever its shape), or has a
    /// critical chunk this decoder doesn't know.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't the image's byte count.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static void Decode(
        ReadOnlySpan<byte> png,
        Span<byte> pixels,
        PixelLayout layout,
        bool premultiplyAlpha)
    {
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        PngDecoder.Decode(png, pixels, bytesPerPixel, premultiplyAlpha);
    }

    /// <summary>
    /// Encodes pixels as a PNG image, 8 bits a sample, and writes the file to <paramref name="writer"/>.
    /// </summary>
    /// <param name="writer">Where the file's bytes go.</param>
    /// <param name="width">The image's width in pixels.</param>
    /// <param name="height">The image's height in pixels.</param>
    /// <param name="pixels">The pixels: width x height of them, in <paramref name="layout"/>.</param>
    /// <param name="layout">How <paramref name="pixels"/> are laid out.</param>
    /// <param name="unpremultiplyAlpha">
    /// Whether the encoder divides each pixel's red, green and blue by its alpha before writing it,
    /// since a PNG file stores straight alpha: true for premultiplied pixels, such as those read back
    /// from a render texture.
    /// </param>
    /// <param name="compressionLevel">
    /// How hard to work to make the file small: <see cref="PngCompressionLevel.Optimal"/> by default.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width or height isn't positive, the image has more than 8192 x 8192 pixels in all
    /// (67,108,864, whatever its shape), or
    /// <paramref name="layout"/> or <paramref name="compressionLevel"/> isn't one of its values.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't width x height pixels.</exception>
    public static void Encode(
        IBufferWriter<byte> writer,
        int width,
        int height,
        ReadOnlySpan<byte> pixels,
        PixelLayout layout,
        bool unpremultiplyAlpha,
        PngCompressionLevel compressionLevel = PngCompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > PngDecoder.MaxPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"The image ({width} x {height}) has more than 8192 x 8192 pixels in all.");
        }

        if (pixels.Length != width * height * bytesPerPixel)
        {
            throw new ArgumentException($"A {width} x {height} image is {width * height * bytesPerPixel} bytes in this layout, but {pixels.Length} were given.", nameof(pixels));
        }

        if ((uint)compressionLevel > (uint)PngCompressionLevel.SmallestSize)
        {
            throw new ArgumentOutOfRangeException(nameof(compressionLevel), compressionLevel, "That isn't a compression level.");
        }

        PngEncoder.Encode(writer, width, height, pixels, unpremultiplyAlpha, compressionLevel);
    }
}
