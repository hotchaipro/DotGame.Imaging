// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging;

/// <summary>
/// Premultiplying decoded pixels by their alpha, rounding to the nearest: (c x a + 127) /
/// 255, exactly.
/// </summary>
internal static class Premultiply
{
    /// <summary>
    /// Multiplies each RGBA pixel's red, green and blue by its alpha, in place.
    /// </summary>
    public static void Pixels(
        Span<byte> rgba)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(rgba);
        for (int i = 0; i < pixels.Length; i++)
        {
            uint pixel = pixels[i];
            uint a = pixel >> 24;
            if (a != 255)
            {
                pixels[i] = Pixel(pixel);
            }
        }
    }

    /// <summary>
    /// One little-endian RGBA word, red in the low byte, premultiplied: red and blue in one multiply,
    /// with no branches (which antialiased edges would mispredict), and t / 255 rounded as
    /// (t + 1 + (t >> 8)) >> 8, which is exact for t up to 255 x 255 + 127.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pixel(
        uint pixel)
    {
        uint a = pixel >> 24;
        uint redBlue = ((pixel & 0x00ff00ff) * a) + 0x007f007f;
        uint green = (((pixel >> 8) & 0xff) * a) + 0x7f;
        redBlue = ((redBlue + 0x00010001 + ((redBlue >> 8) & 0x00ff00ff)) >> 8) & 0x00ff00ff;
        green = ((green + 1 + (green >> 8)) >> 8) & 0xff;
        return redBlue | (green << 8) | (a << 24);
    }
}
