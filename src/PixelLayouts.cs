// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Imaging;

/// <summary>
/// What the decoders and the encoder share about <see cref="PixelLayout"/>.
/// </summary>
internal static class PixelLayouts
{
    /// <summary>
    /// The bytes a pixel takes in a layout.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> isn't a layout.</exception>
    public static int BytesPerPixel(
        PixelLayout layout)
    {
        if (layout != PixelLayout.Rgba32)
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, "That isn't a pixel layout.");
        }

        return 4;
    }

    /// <summary>
    /// The bytes an image's pixels take, if a buffer can hold them (at most <see cref="int.MaxValue"/>).
    /// </summary>
    public static bool TryGetByteCount(
        int width,
        int height,
        int bytesPerPixel,
        out int byteCount)
    {
        // width x height fits in a long, but times the bytes per pixel it can overflow (a PNG image's
        // sides go to 2^31 - 1), so the pixel count is checked first
        long pixelCount = (long)width * height;
        long count = pixelCount * bytesPerPixel;
        if ((width <= 0) || (height <= 0) || (pixelCount > (int.MaxValue / bytesPerPixel)) || (count > int.MaxValue))
        {
            byteCount = 0;
            return false;
        }

        byteCount = (int)count;
        return true;
    }

    /// <summary>
    /// Throws unless a buffer's <paramref name="length"/> is exactly an image's byte count, naming the
    /// caller's parameter.
    /// </summary>
    public static void CheckBuffer(
        int width,
        int height,
        int bytesPerPixel,
        int length,
        string paramName)
    {
        if (!TryGetByteCount(width, height, bytesPerPixel, out int byteCount) || (length != byteCount))
        {
            throw new ArgumentException($"The {width} x {height} image is {(long)width * height * bytesPerPixel} bytes in this layout, but the buffer is {length}.", paramName);
        }
    }
}
