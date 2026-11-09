// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;

namespace DotGame.Imaging.WebP;

/// <summary>
/// Reads WebP images: lossy (VP8) with or without alpha, and lossless (VP8L). A C# port of libwebp
/// 1.6.0's decoder, whose pixels it gives, bit for bit, with libwebp's defaults (fancy upsampling).
/// Animated images aren't read.
/// </summary>
/// <remarks>
/// Decoding is two steps, so the caller decides where the pixels go: <see cref="TryGetDecodedSize"/>
/// reads the size from the image's headers, and <see cref="Decode"/> fills a buffer of that size. To
/// read PNG, JPEG and WebP alike, use <see cref="ImageDecoder"/>. libwebp's BSD license asks that its
/// notice go with the software in binary form: the package's THIRD-PARTY-NOTICES.md has it.
/// </remarks>
public static class WebPCodec
{
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
}
