// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Imaging.Tests;

/// <summary>
/// References for the decoders' pixels, computed the plain way.
/// </summary>
internal static class TestPixels
{
    // Each color times its alpha, rounded to the nearest: the plain division, which
    // Premultiply.Pixel's shift arithmetic must match
    public static byte[] Premultiplied(
        byte[] pixels)
    {
        var result = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            for (int c = 0; c < 3; c++)
            {
                result[i + c] = (byte)(((pixels[i + c] * a) + 127) / 255);
            }

            result[i + 3] = (byte)a;
        }

        return result;
    }
}
