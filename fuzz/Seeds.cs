// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using DotGame.Imaging.Jpeg;
using DotGame.Imaging.Png;
using DotGame.Imaging.WebP;

namespace DotGame.Imaging.Fuzz;

/// <summary>
/// The inputs fuzzing starts from: the tests' images, and images the encoders write in each of their
/// modes, at sizes that aren't multiples of the blocks and macroblocks.
/// </summary>
internal static class Seeds
{
    public static List<byte[]> For(
        Format format,
        string testImages)
    {
        var seeds = new List<byte[]>();
        foreach (string name in format == Format.Any ? new[] { "Png", "Jpeg", "WebP" } : new[] { format.ToString() })
        {
            string folder = Path.Combine(testImages, name);
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    if ((bytes.Length <= 64 << 10) && (Path.GetExtension(file) != ".png" || name == "Png"))
                    {
                        seeds.Add(bytes);
                    }
                }
            }
        }

        foreach ((int width, int height) in new[] { (1, 1), (3, 2), (9, 17), (33, 16), (24, 40) })
        {
            byte[] pixels = Pixels(width, height);
            if (format is Format.Png or Format.Any)
            {
                seeds.Add(Encode(w => PngCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, PngCompressionLevel.Fastest)));
            }

            if (format is Format.Jpeg or Format.Any)
            {
                seeds.Add(Encode(w => JpegCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, new JpegEncoderOptions { Quality = 50 })));
                seeds.Add(Encode(w => JpegCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, new JpegEncoderOptions { Progressive = true, Subsampling = JpegSubsampling.YCbCr422 })));
                seeds.Add(Encode(w => JpegCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, new JpegEncoderOptions { Progressive = true, Grayscale = true })));
                seeds.Add(Encode(w => JpegCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, new JpegEncoderOptions { OptimizeHuffman = true, Subsampling = JpegSubsampling.YCbCr444, Quality = 95 })));
            }

            if (format is Format.WebP or Format.Any)
            {
                seeds.Add(Encode(w => WebPCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, new WebPEncoderOptions { Quality = 50, Method = 0 })));
                seeds.Add(Encode(w => WebPCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, new WebPEncoderOptions { Lossless = true, Method = 0 })));
                seeds.Add(Encode(w => WebPCodec.Encode(w, width, height, pixels, PixelLayout.Rgba32, false, new WebPEncoderOptions { Lossless = true, Method = 6, Quality = 100 })));
            }
        }

        return seeds;
    }

    private static byte[] Encode(
        Action<IBufferWriter<byte>> encode)
    {
        var writer = new ArrayBufferWriter<byte>();
        encode(writer);
        return writer.WrittenSpan.ToArray();
    }

    // Gradients, a few colors repeated (for palettes and backward references) and translucency
    private static byte[] Pixels(
        int width,
        int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                bool flat = ((x / 4) + (y / 4)) % 3 == 0;
                pixels[i] = flat ? (byte)200 : (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[i + 1] = flat ? (byte)40 : (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[i + 2] = (byte)((x ^ y) * 16);
                pixels[i + 3] = (x % 5 == 0) ? (byte)128 : (byte)255;
            }
        }

        return pixels;
    }
}
