// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using DotGame.Imaging.Jpeg;
using DotGame.Imaging.Png;

namespace DotGame.Imaging.Tests;

/// <summary>
/// The JPEG decoder against libjpeg-turbo 3.2.1's pixels (its defaults, decoding to RGBA): small
/// images made by its cjpeg in every sampling, progressive, restart-interval, RGB and grayscale
/// form, each with the SHA-256 of libjpeg-turbo's RGBA. The decoder was checked against libjpeg-turbo
/// on 1,108 images, every one the same. And the encoder against cjpeg 3.2.1's files, from the WebP
/// tests' EncodeSource.png and its 37 x 21 middle; it was checked against cjpeg on 1,091 images in 27
/// settings, every file the same.
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

    // The encoder's settings (cjpeg's switches in EncoderOptions), each with the SHA-256 of cjpeg 3.2.1's
    // file from the same pixels
    public static readonly TheoryData<string, string> EncoderSettings = new()
    {
        { "Default", "eafed6e2c754c25923c1e35690d9ab1179b48a2a069322c145a9d552a4786b51" },
        { "Quality90YCbCr444", "7951ac02131d792e97c9506a71733c7ac4970cca3d8a567e4c50aab9b8c5b8be" },
        { "Quality50YCbCr422", "4880f3b82bbafd8ad2be1e25e6aee12cfe559a6c7ddb2d47f779acb22425e07d" },
        { "Quality10", "d758feb068d9a9d0c359a839aa8fd7d6a70b075e32385940ca1047edeb251e7d" },
        { "Quality10Baseline", "ef22657325df47929b22f9e06f0bf5c8cf0a88fb1915e09a58b030b0da095b0d" },
        { "Quality100", "e095fdd1a1f9dc6cf938abe5b5f78902e5153e79b70970c332692a9b74c29b90" },
        { "Optimized", "6acdb73291ea43db2941c1029816da504482835f141a9352cf170ef00bd5388d" },
        { "Progressive", "4512c68bf898e082321222901e20b2e6321079e8ab4fa9a74cf7a72e82e23007" },
        { "Progressive444", "1f6cf6b52bbdcc106a5b20023a5c4b46e051727dfcb2dea62e370d28d0ae4cf6" },
        { "Restarts", "c47fa4e6d9d76da2241957826f34a264d8b765515396dc1fcc5886e2b95976e0" },
        { "RestartBlocks422", "c1111b6e114edf3832f7ba9b5ffd41abd76e33d7ef24024d09e40d507ae6d84a" },
        { "Grayscale", "cbd382e7afc5dbfcf8f004dfed7d569ed285a63d03ab8ce9cd75ff327cc3fcfc" },
        { "GrayscaleProgressive", "cd796339bb7868cb05a85740d067bd6cb7e3a6e31690da45be6159063130c889" },
        { "FastDct", "b573dd8ae65d1ae7bcb78dd7d0e5a619643685f4f947333b4bb93a7a19958971" },
        { "Crop", "89b9a02c6a3794d92418cdace03e12dc860327aaa4e4b71417a457f9cdafb30d" },
        { "CropOptimized", "513a79b59f26ff349601a9708a5241cd3e6889e46c52df55278654063269dda7" },
        { "CropProgressive", "2fddf7d9baade4107a6116f2e8f2e19c88bfabeb1407f787608cfc9ae7107989" },
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

    [Theory]
    [MemberData(nameof(EncoderSettings))]
    public void Encode_WritesCjpegsFile(
        string name,
        string sha256)
    {
        byte[] pixels = EncodeSource(name.StartsWith("Crop", StringComparison.Ordinal), out int width, out int height);
        var writer = new ArrayBufferWriter<byte>();

        JpegCodec.Encode(writer, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, EncoderOptions(name));

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(writer.WrittenSpan)));
    }

    [Fact]
    public void Encode_WithNoOptionsUsesCjpegsDefaults()
    {
        byte[] pixels = EncodeSource(crop: false, out int width, out int height);
        var withDefaults = new ArrayBufferWriter<byte>();
        var withNone = new ArrayBufferWriter<byte>();

        JpegCodec.Encode(withDefaults, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, new JpegEncoderOptions());
        JpegCodec.Encode(withNone, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false);

        Assert.Equal(withDefaults.WrittenSpan, withNone.WrittenSpan);
    }

    [Fact]
    public void Encode_DecodesToThePixelsNearly()
    {
        // At quality 100 and 4:4:4, each opaque pixel's channels come back within a few levels
        byte[] pixels = EncodeSource(crop: false, out int width, out int height);
        var writer = new ArrayBufferWriter<byte>();
        JpegCodec.Encode(writer, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, new JpegEncoderOptions { Quality = 100, Subsampling = JpegSubsampling.YCbCr444 });

        Assert.True(JpegCodec.TryGetDecodedSize(writer.WrittenSpan, PixelLayout.Rgba32, out int w, out int h, out int byteCount));
        Assert.Equal((width, height), (w, h));
        var decoded = new byte[byteCount];
        JpegCodec.Decode(writer.WrittenSpan, decoded, PixelLayout.Rgba32);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 255)
            {
                for (int c = 0; c < 3; c++)
                {
                    Assert.InRange(decoded[i + c] - pixels[i + c], -4, 4);
                }
            }
        }
    }

    [Fact]
    public void Encode_UnpremultipliesWhenAsked()
    {
        byte[] pixels = EncodeSource(crop: false, out int width, out int height);
        byte[] premultiplied = (byte[])pixels.Clone();
        Premultiply.Pixels(premultiplied);
        byte[] straight = (byte[])premultiplied.Clone();
        Premultiply.UndoPixels(straight);
        var fromStraight = new ArrayBufferWriter<byte>();
        var fromPremultiplied = new ArrayBufferWriter<byte>();

        JpegCodec.Encode(fromStraight, width, height, straight, PixelLayout.Rgba32, unpremultiplyAlpha: false);
        JpegCodec.Encode(fromPremultiplied, width, height, premultiplied, PixelLayout.Rgba32, unpremultiplyAlpha: true);

        Assert.Equal(fromStraight.WrittenSpan, fromPremultiplied.WrittenSpan);
    }

    [Fact]
    public void Encode_RejectsWhatIsntAnImage()
    {
        var writer = new ArrayBufferWriter<byte>();

        Assert.Throws<ArgumentNullException>(() => JpegCodec.Encode(null!, 1, 1, new byte[4], PixelLayout.Rgba32, unpremultiplyAlpha: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.Encode(writer, 0, 1, [], PixelLayout.Rgba32, unpremultiplyAlpha: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.Encode(writer, 8193, 8193, [], PixelLayout.Rgba32, unpremultiplyAlpha: false));
        Assert.Throws<ArgumentException>(() => JpegCodec.Encode(writer, 2, 2, new byte[15], PixelLayout.Rgba32, unpremultiplyAlpha: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.Encode(writer, 1, 1, new byte[4], PixelLayout.Rgba32, unpremultiplyAlpha: false, new JpegEncoderOptions { Quality = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.Encode(writer, 1, 1, new byte[4], PixelLayout.Rgba32, unpremultiplyAlpha: false, new JpegEncoderOptions { RestartBlocks = 65536 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.Encode(writer, 1, 1, new byte[4], PixelLayout.Rgba32, unpremultiplyAlpha: false, new JpegEncoderOptions { Subsampling = (JpegSubsampling)3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.Encode(writer, 1, 1, new byte[4], (PixelLayout)1, unpremultiplyAlpha: false));
        Assert.Equal(0, writer.WrittenCount);
    }

    // EncodeSource.png's pixels (96 x 64), or its middle 37 x 21
    private static byte[] EncodeSource(
        bool crop,
        out int width,
        out int height)
    {
        byte[] png = TestImages.Read("WebP/EncodeSource.png");
        Assert.True(PngCodec.TryGetDecodedSize(png, PixelLayout.Rgba32, out int w, out int h, out int byteCount));
        var pixels = new byte[byteCount];
        PngCodec.Decode(png, pixels, PixelLayout.Rgba32, premultiplyAlpha: false);
        if (!crop)
        {
            (width, height) = (w, h);
            return pixels;
        }

        (width, height) = (37, 21);
        var middle = new byte[width * height * 4];
        int left = (w - width) / 2;
        int top = (h - height) / 2;
        for (int y = 0; y < height; y++)
        {
            pixels.AsSpan((((top + y) * w) + left) * 4, width * 4).CopyTo(middle.AsSpan(y * width * 4));
        }

        return middle;
    }

    private static JpegEncoderOptions EncoderOptions(
        string name)
    {
        return name switch
        {
            "Quality90YCbCr444" => new JpegEncoderOptions { Quality = 90, Subsampling = JpegSubsampling.YCbCr444 },
            "Quality50YCbCr422" => new JpegEncoderOptions { Quality = 50, Subsampling = JpegSubsampling.YCbCr422 },
            "Quality10" => new JpegEncoderOptions { Quality = 10 },
            "Quality10Baseline" => new JpegEncoderOptions { Quality = 10, ForceBaseline = true },
            "Quality100" => new JpegEncoderOptions { Quality = 100 },
            "Optimized" or "CropOptimized" => new JpegEncoderOptions { OptimizeHuffman = true },
            "Progressive" => new JpegEncoderOptions { Progressive = true },
            "Progressive444" => new JpegEncoderOptions { Progressive = true, Subsampling = JpegSubsampling.YCbCr444, Quality = 90 },
            "Restarts" => new JpegEncoderOptions { RestartRows = 1 },
            "RestartBlocks422" => new JpegEncoderOptions { RestartBlocks = 3, Subsampling = JpegSubsampling.YCbCr422 },
            "Grayscale" => new JpegEncoderOptions { Grayscale = true },
            "GrayscaleProgressive" => new JpegEncoderOptions { Grayscale = true, Progressive = true },
            "FastDct" => new JpegEncoderOptions { Dct = JpegEncoderDct.Fast },
            "CropProgressive" => new JpegEncoderOptions { Progressive = true, RestartBlocks = 2 },
            _ => new JpegEncoderOptions(),
        };
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
