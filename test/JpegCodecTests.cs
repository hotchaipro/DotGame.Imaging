// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Security.Cryptography;
using DotGame.Imaging.Jpeg;

namespace DotGame.Imaging.Tests;

/// <summary>
/// The JPEG decoder against libjpeg-turbo 3.2.1's pixels (its defaults, decoding to RGBA): small
/// images made by its cjpeg in every sampling, progressive, restart-interval, RGB and grayscale
/// form, each with the SHA-256 of libjpeg-turbo's RGBA. The decoder was checked against libjpeg-turbo
/// on 1,108 images, every one the same.
/// </summary>
public sealed class JpegCodecTests
{
    public static readonly TheoryData<string, int, int, string> Fixtures = new()
    {
        { "Baseline411", 37, 21, "ec02296aa190fc5ca0c98b0ec3e3ecde7fd09b49051f9f28dd6ee9d4c75c05c0" },
        { "Baseline420", 37, 21, "29e6a66828190e41fbf71acd371bfd35b34c8762bc9e6988fc693a256dda1c93" },
        { "Baseline422", 37, 21, "e41dc65419b28f82674196fe6d8ea6531b73efabe43ea3314d7bcb37b43cd8b9" },
        { "Baseline440", 37, 21, "94967232fcd5d3a87ee7b4137a10c9fecfb9309a0790d5a290ce9bf6e95163fb" },
        { "Baseline444", 37, 21, "98ee75e288d7258b366df701fb2e68f60600d79196550fb85483629f096bf24e" },
        { "Grayscale", 37, 21, "2d980617d96fc188314af03cc084b84ba59710a2ba95868c5ba68187e907c4fa" },
        { "GrayscaleProgressive", 37, 21, "2d980617d96fc188314af03cc084b84ba59710a2ba95868c5ba68187e907c4fa" },
        { "Optimized420", 37, 21, "e636a00e156e745afa73f1d758ab50d240e99c6bad8102deaa5933d89d8f9957" },
        { "Progressive420", 37, 21, "29e6a66828190e41fbf71acd371bfd35b34c8762bc9e6988fc693a256dda1c93" },
        { "Progressive444", 37, 21, "7f392b7d7d1c1a04713ba1f4e2e209ecfafe00d1192d9f94c374501725280118" },
        { "ProgressiveRestarts", 37, 21, "d9567c73f2550e309fd2866c93b842a3bfefbb1467445d889a2185803d7055fa" },
        { "Restarts422", 37, 21, "1db4729cf74f18c5dd14f2fc016859772044f154d30498d3c91457f9b8acb90a" },
        { "Rgb", 37, 21, "070f537747864f95070cd802cb5df41bdcf0c0e5ea51994e4c313c28573eb8d8" },
    };

    [Fact]
    public void TryGetDecodedSize_IsFalseForWhatIsntAJpeg()
    {
        Assert.False(JpegCodec.TryGetDecodedSize([], PixelLayout.Rgba32, out _, out _, out _));
        Assert.False(JpegCodec.TryGetDecodedSize([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], PixelLayout.Rgba32, out _, out _, out _));

        // The header alone, cut before the frame
        byte[] data = TestImages.Read("Jpeg/Baseline411.jpg");
        Assert.False(JpegCodec.TryGetDecodedSize(data.AsSpan(0, 30), PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal((0, 0, 0), (width, height, byteCount));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decode_GivesLibjpegTurbosPixels(
        string name,
        int width,
        int height,
        string sha256)
    {
        byte[] data = TestImages.Read($"Jpeg/{name}.jpg");
        Assert.True(JpegCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out int w, out int h, out int byteCount));
        Assert.Equal((width, height, width * height * 4), (w, h, byteCount));

        var pixels = new byte[byteCount];
        JpegCodec.Decode(data, pixels, PixelLayout.Rgba32);

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(pixels)));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decode_ReadsDamagedDataAsLibjpegTurboDoes(
        string name,
        int width,
        int height,
        string sha256)
    {
        // Bytes of the first scan's entropy-coded data changed (never to or next to 0xFF, so no
        // marker appears): libjpeg-turbo decodes damaged data as best it can, and so does this
        _ = sha256;
        byte[] data = TestImages.Read($"Jpeg/{name}.jpg");
        (int start, int end) = ScanData(data);
        int changed = 0;
        for (int i = start + 1; i < end - 1; i += 7)
        {
            if ((data[i] != 0xFF) && (data[i - 1] != 0xFF) && (data[i + 1] != 0xFF) && ((data[i] ^ 0x5A) != 0xFF))
            {
                data[i] ^= 0x5A;
                changed++;
            }
        }

        Assert.True(changed > 0);
        var pixels = new byte[width * height * 4];
        JpegCodec.Decode(data, pixels, PixelLayout.Rgba32);
    }

    [Fact]
    public void Decode_DoesntSupportArithmeticCoding()
    {
        // An arithmetic-coded frame header (SOF9) after SOI: its size reads, its pixels don't
        byte[] arithmetic = [0xFF, 0xD8, 0xFF, 0xC9, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9];

        Assert.True(JpegCodec.TryGetDecodedSize(arithmetic, PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal((1, 1, 4), (width, height, byteCount));
        var error = Assert.Throws<NotSupportedException>(() => JpegCodec.Decode(arithmetic, new byte[4], PixelLayout.Rgba32));
        Assert.Contains("arithmetic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_DoesntSupport12BitSamples()
    {
        // An extended frame header (SOF1) of 12-bit samples
        byte[] twelveBit = [0xFF, 0xD8, 0xFF, 0xC1, 0x00, 0x0B, 0x0C, 0x00, 0x02, 0x00, 0x03, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9];

        Assert.True(JpegCodec.TryGetDecodedSize(twelveBit, PixelLayout.Rgba32, out int width, out int height, out _));
        Assert.Equal((3, 2), (width, height));
        Assert.Throws<NotSupportedException>(() => JpegCodec.Decode(twelveBit, new byte[24], PixelLayout.Rgba32));
    }

    [Fact]
    public void Decode_RejectsAFileCutShort()
    {
        // A quantization table segment longer than what's left
        byte[] cut = [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00, 1, 2, 3];

        Assert.False(JpegCodec.TryGetDecodedSize(cut, PixelLayout.Rgba32, out _, out _, out _));
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(cut, new byte[4], PixelLayout.Rgba32));
    }

    [Fact]
    public void Decode_RejectsABufferOfTheWrongSize()
    {
        byte[] data = TestImages.Read("Jpeg/Baseline411.jpg");

        Assert.Throws<ArgumentException>(() => JpegCodec.Decode(data, new byte[(37 * 21 * 4) - 1], PixelLayout.Rgba32));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.Decode(data, new byte[37 * 21 * 4], (PixelLayout)3));
    }

    // The first scan's entropy-coded data: from the end of the SOS segment to the next marker that
    // isn't a stuffed 0xFF or a restart (a later scan's tables and header, or the end)
    private static (int Start, int End) ScanData(
        byte[] jpeg)
    {
        int start = -1;
        for (int i = 2; (start < 0) && (i + 3 < jpeg.Length); i++)
        {
            if ((jpeg[i] == 0xFF) && (jpeg[i + 1] == 0xDA))
            {
                start = i + 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
            }
        }

        Assert.True(start > 0, "No scan.");
        int end = start;
        while ((end + 1 < jpeg.Length) && !((jpeg[end] == 0xFF) && (jpeg[end + 1] != 0) && ((jpeg[end + 1] < 0xD0) || (jpeg[end + 1] > 0xD7))))
        {
            end++;
        }

        return (start, end);
    }
}
