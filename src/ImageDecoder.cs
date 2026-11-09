// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using DotGame.Imaging.Jpeg;
using DotGame.Imaging.Png;
using DotGame.Imaging.WebP;

namespace DotGame.Imaging;

/// <summary>
/// Reads an image in any format this library decodes, telling which from its bytes: PNG
/// (<see cref="PngCodec"/>), JPEG (<see cref="JpegCodec"/>) and WebP (<see cref="WebPCodec"/>) now, and any format added later, without a
/// change to the code that calls it.
/// </summary>
/// <remarks>
/// It refers to every decoder, so an app that calls it ships them all, and with them their licenses'
/// terms: the JPEG decoder's asks for a line in the app's documentation or credits (see
/// <see cref="JpegCodec"/>). An app that reads only PNG images, and wants nothing else in its download,
/// calls <see cref="PngCodec"/>.
/// </remarks>
public static class ImageDecoder
{
    /// <summary>
    /// Reads an image's size from its header, and the size of the buffer <see cref="Decode"/> fills.
    /// </summary>
    /// <param name="data">The image's bytes: the whole file, or at least its header.</param>
    /// <param name="layout">The layout the pixels will be decoded to.</param>
    /// <param name="width">The image's width in pixels, or 0.</param>
    /// <param name="height">The image's height in pixels, or 0.</param>
    /// <param name="byteCount">The bytes the decoded pixels take, or 0.</param>
    /// <returns>
    /// Whether the bytes are an image in a format this library reads, with a header that reads (see
    /// <see cref="PngCodec.TryGetDecodedSize"/> and <see cref="JpegCodec.TryGetDecodedSize"/>).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static bool TryGetDecodedSize(
        ReadOnlySpan<byte> data,
        PixelLayout layout,
        out int width,
        out int height,
        out int byteCount)
    {
        return PngCodec.TryGetDecodedSize(data, layout, out width, out height, out byteCount)
            || JpegCodec.TryGetDecodedSize(data, layout, out width, out height, out byteCount)
            || WebPCodec.TryGetDecodedSize(data, layout, out width, out height, out byteCount);
    }

    /// <summary>
    /// Decodes an image's pixels into <paramref name="pixels"/>, in whichever format the bytes are.
    /// </summary>
    /// <param name="data">The image's bytes.</param>
    /// <param name="pixels">
    /// Where the pixels go: exactly the byte count <see cref="TryGetDecodedSize"/> gives. Every byte is
    /// written.
    /// </param>
    /// <param name="layout">The layout the pixels are written in.</param>
    /// <param name="premultiplyAlpha">
    /// Whether the decoder multiplies each pixel's red, green and blue by its alpha, as GPU textures
    /// usually take them. Image files store straight alpha, so false gives the colors as stored; an
    /// opaque image (a JPEG) is the same either way.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// The bytes aren't in a format this library decodes, or the image is of a kind its format's
    /// decoder doesn't read (see <see cref="PngCodec.Decode"/> and <see cref="JpegCodec.Decode"/>).
    /// </exception>
    /// <exception cref="InvalidDataException">The image is damaged.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't the image's byte count.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static void Decode(
        ReadOnlySpan<byte> data,
        Span<byte> pixels,
        PixelLayout layout,
        bool premultiplyAlpha)
    {
        if (PngDecoder.HasSignature(data))
        {
            PngCodec.Decode(data, pixels, layout, premultiplyAlpha);
        }
        else if (JpegDecoder.HasSignature(data))
        {
            JpegCodec.Decode(data, pixels, layout);
        }
        else if (WebPDecoder.HasSignature(data))
        {
            WebPCodec.Decode(data, pixels, layout, premultiplyAlpha);
        }
        else
        {
            PixelLayouts.BytesPerPixel(layout);
            throw new NotSupportedException("The bytes aren't an image in a format this decodes (PNG, JPEG or WebP).");
        }
    }
}
