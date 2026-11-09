// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;

namespace DotGame.Imaging.Jpeg;

/// <summary>
/// Reads and writes JPEG images. It reads baseline, extended and progressive Huffman-coded JPEG, 8-bit
/// samples, grayscale, YCbCr and RGB, every sampling factor, restart intervals: a C# port of
/// libjpeg-turbo 3.2.1, whose pixels it gives, bit for bit, with libjpeg-turbo's defaults. It writes
/// baseline and progressive YCbCr (4:2:0, 4:2:2, 4:4:4) and grayscale JPEG: the same port, with cjpeg's
/// files.
/// </summary>
/// <remarks>
/// <para>
/// Decoding is two steps, so the caller decides where the pixels go: <see cref="TryGetDecodedSize"/>
/// reads the size from the image's header, and <see cref="Decode"/> fills a buffer of that size. To
/// read PNG, JPEG and WebP alike, use <see cref="ImageDecoder"/>.
/// </para>
/// <para>
/// libjpeg-turbo's IJG License asks that software built from it say so: an app that ships this
/// decoder says "this software is based in part on the work of the Independent JPEG Group" in its
/// documentation or credits. The package's THIRD-PARTY-NOTICES.md has the licenses. A trimmed app
/// that never calls it has none of it.
/// </para>
/// </remarks>
public static class JpegCodec
{
    /// <summary>
    /// Reads a JPEG image's size from its frame header, and the size of the buffer
    /// <see cref="Decode"/> fills. An image of a kind <see cref="Decode"/> doesn't read (arithmetic
    /// coded, say) still has a size.
    /// </summary>
    /// <param name="jpeg">The image's bytes: the whole file, or at least as far as its frame header.</param>
    /// <param name="layout">The layout the pixels will be decoded to.</param>
    /// <param name="width">The image's width in pixels, or 0.</param>
    /// <param name="height">The image's height in pixels, or 0.</param>
    /// <param name="byteCount">The bytes the decoded pixels take, or 0.</param>
    /// <returns>
    /// Whether the bytes are a JPEG image with a frame header that reads: false if they aren't a JPEG
    /// image, if it's damaged or cut short before its frame header, if its height comes later (in a
    /// DNL marker), or if its pixels wouldn't fit in a buffer (2 GB).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static bool TryGetDecodedSize(
        ReadOnlySpan<byte> jpeg,
        PixelLayout layout,
        out int width,
        out int height,
        out int byteCount)
    {
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        if (JpegDecoder.TryReadSize(jpeg, out int w, out int h) && PixelLayouts.TryGetByteCount(w, h, bytesPerPixel, out byteCount))
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
    /// Decodes a JPEG image's pixels into <paramref name="pixels"/>. A JPEG image is opaque: every
    /// pixel's alpha is 255, so its pixels are the same straight or premultiplied.
    /// </summary>
    /// <param name="jpeg">The image's bytes.</param>
    /// <param name="pixels">
    /// Where the pixels go: exactly the byte count <see cref="TryGetDecodedSize"/> gives. Every byte is
    /// written.
    /// </param>
    /// <param name="layout">The layout the pixels are written in.</param>
    /// <exception cref="InvalidDataException">The bytes aren't a JPEG image, or it's damaged.</exception>
    /// <exception cref="NotSupportedException">
    /// The image is of a kind this decoder doesn't read, which the message names: arithmetic coded,
    /// lossless or hierarchical, 12-bit samples, CMYK or YCCK, a height in a DNL marker, sampling
    /// factors libjpeg doesn't upsample, a progressive image whose scans leave its low coefficients
    /// unrefined (which libjpeg-turbo smooths), or larger than 8192 x 8192 pixels.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't the image's byte count.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static void Decode(
        ReadOnlySpan<byte> jpeg,
        Span<byte> pixels,
        PixelLayout layout)
    {
        int bytesPerPixel = PixelLayouts.BytesPerPixel(layout);
        JpegDecoder.Decode(jpeg, pixels, bytesPerPixel);
    }

    /// <summary>
    /// Encodes pixels as a JPEG image and writes the file to <paramref name="writer"/>: with the same
    /// settings, byte for byte what cjpeg 3.2.1 writes. JPEG has no alpha, so it's dropped, as cjpeg
    /// drops it.
    /// </summary>
    /// <param name="writer">Where the file's bytes go.</param>
    /// <param name="width">The image's width in pixels.</param>
    /// <param name="height">The image's height in pixels.</param>
    /// <param name="pixels">The pixels: width x height of them, in <paramref name="layout"/>.</param>
    /// <param name="layout">How <paramref name="pixels"/> are laid out.</param>
    /// <param name="unpremultiplyAlpha">
    /// Whether the encoder divides each pixel's red, green and blue by its alpha before encoding it:
    /// true for premultiplied pixels, whose colors are otherwise darker where they're translucent.
    /// </param>
    /// <param name="options">How to encode the image, or null for cjpeg's defaults (quality 75, 4:2:0).</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width or height isn't positive, the image is larger than JPEG allows or than 8192 x 8192
    /// pixels, an option is out of its range, or <paramref name="layout"/> isn't a layout.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't width x height pixels.</exception>
    public static void Encode(
        IBufferWriter<byte> writer,
        int width,
        int height,
        ReadOnlySpan<byte> pixels,
        PixelLayout layout,
        bool unpremultiplyAlpha,
        JpegEncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _ = PixelLayouts.BytesPerPixel(layout);

        if (unpremultiplyAlpha)
        {
            byte[] straight = pixels.ToArray();
            Premultiply.UndoPixels(straight);
            pixels = straight;
        }

        JpegEncoder.Encode(writer, width, height, pixels, options);
    }
}
