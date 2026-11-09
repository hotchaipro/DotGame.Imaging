// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using DotGame.Imaging.Png;
using DotGame.Imaging.WebP;

namespace DotGame.Imaging.Tests;

/// <summary>
/// The WebP decoder against libwebp 1.6.0's pixels (WebPDecodeRGBA's defaults: fancy upsampling,
/// straight alpha): small images made by its cwebp, lossy, lossless and lossy with alpha, each with the
/// SHA-256 of libwebp's RGBA. And the encoder against cwebp 1.6.0's files: one image encoded with
/// cwebp's settings, each with the SHA-256 of cwebp's file. The decoder was checked against libwebp on
/// 2,403 images, and the encoders against cwebp on 495 images (lossless) and 374 (lossy) in some 80
/// combinations of settings, every one the same.
/// </summary>
public sealed class WebPCodecTests
{
    public static readonly TheoryData<string, int, int, string> Fixtures = new()
    {
        { "Lossy1x1", 1, 1, "e3820096cb82366b860b8a4e668453a7aaaf423af03bdf289fa308ea03a79332" },
        { "Lossy2x3", 2, 3, "217f6edddf2a8ce2ae921f74966bca8c6fa689482bbdfca93c5a468cde0e5f81" },
        { "Lossy17x9", 17, 9, "a9cf9c855bc393a72c962ca86e6a0dda6f8da3dda735bbb242b5925ff238a5ac" },
        { "Lossy37x21", 37, 21, "e844917345230a15ae032d0c67ff4421a54fcea8f6b0e482f8ae5a69939714cb" },
        { "Lossy130x67", 130, 67, "a3feef97623c6d91c9366a19c62f09e052b66ddf2a7644d42edc2ddc96ff5990" },
        { "LossySharpYuv", 37, 21, "d41056c0caeeb3944d89a04d1b4aa571eb568cc9857900830ce3a58c11dcacb1" },
        { "Lossless37x21", 37, 21, "87ee368ed32b07a43b98793767e79f7d9a460a54a0e3bff15e02cbdf2095e405" },
        { "Lossless17x9", 17, 9, "5147775ec156e4812c1c4387975b20cc8202f86978aec05d4ef3b67b3bc45408" },
        { "LosslessFlat", 37, 21, "477fe78c84fe865607dc24007157da6de9d46dec940210c61ecc235ebe75846f" },
        { "LossyAlphaFlat17x9", 17, 9, "fea95ee6654315a11e31ce972ff371fc74f2ad8dc02dd7439dafe6ceff383e9a" },
        { "LossyAlpha37x21", 37, 21, "9cd584efe29dd1ca716b5d07b00ba8c4cee0c7c7283b9bfea20d4c58cc0f5470" },
        { "LossyAlpha130x67", 130, 67, "1179102e716af7d87c5de973bf8eff9d7b3779bc1704afafcca4b05eefdf3fd7" },
    };

    // The encoder's settings for EncodeSource.png, each with the SHA-256 of cwebp 1.6.0's file
    public static readonly TheoryData<string, string> EncoderSettings = new()
    {
        { "Lossless", "819576867e33a2dc41b3b105686405263beeee6a6b60a7f7e9e21c9357fba36f" },
        { "Lossy", "e33adba8d929df1fa921b3980053c7d22f9b43c34c1aadfdc7735a07373fffce" },
        { "Defaults", "5c8d627253c6f0a6fc4f940179b4348a4e0de7a0471f976ce7fff0cefa76079c" },
        { "LossyFast", "48b3e29bbddf410621c3fd5c7ecf06a0ae735499a2d916ba1a5c74fb09b52c0a" },
        { "NearLossless", "29eb3ef748e7ffeaa736020a3d65db3c10b8164993afeedd4bcedb9b1b4af958" },
        { "LosslessThreads", "819576867e33a2dc41b3b105686405263beeee6a6b60a7f7e9e21c9357fba36f" },
    };

    [Fact]
    public void TryGetDecodedSize_IsFalseForWhatIsntAWebP()
    {
        Assert.False(WebPCodec.TryGetDecodedSize([], PixelLayout.Rgba32, out _, out _, out _));
        Assert.False(WebPCodec.TryGetDecodedSize("RIFF\0\0\0\0WAVEfmt "u8, PixelLayout.Rgba32, out _, out _, out _));
        byte[] data = TestImages.Read("WebP/Lossy1x1.webp");
        Assert.False(WebPCodec.TryGetDecodedSize(data.AsSpan(0, 20), PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal((0, 0, 0), (width, height, byteCount));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decode_GivesLibwebpsPixels(
        string name,
        int width,
        int height,
        string sha256)
    {
        byte[] data = TestImages.Read($"WebP/{name}.webp");
        Assert.True(WebPCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out int w, out int h, out int byteCount));
        Assert.Equal((width, height, width * height * 4), (w, h, byteCount));

        var pixels = new byte[byteCount];
        WebPCodec.Decode(data, pixels, PixelLayout.Rgba32, premultiplyAlpha: false);

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(pixels)));
    }

    [Fact]
    public void Decode_Premultiplies()
    {
        byte[] data = TestImages.Read("WebP/LossyAlpha37x21.webp");
        Assert.True(WebPCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out _, out _, out int byteCount));
        var straight = new byte[byteCount];
        var premultiplied = new byte[byteCount];
        WebPCodec.Decode(data, straight, PixelLayout.Rgba32, premultiplyAlpha: false);
        WebPCodec.Decode(data, premultiplied, PixelLayout.Rgba32, premultiplyAlpha: true);

        // The image has translucent pixels, so there's something to premultiply
        bool anyTranslucent = false;
        for (int i = 3; i < byteCount; i += 4)
        {
            anyTranslucent |= straight[i] < 255;
        }

        Assert.True(anyTranslucent);
        Assert.Equal(TestPixels.Premultiplied(straight), premultiplied);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decode_ReadsDamagedDataOrRejectsIt(
        string name,
        int width,
        int height,
        string sha256)
    {
        // Every 7th byte of the image's compressed data changed, past its headers: libwebp decodes some
        // damage and reports the rest as an error, so either is right, but nothing else (reading past
        // a buffer, say)
        _ = sha256;
        byte[] data = TestImages.Read($"WebP/{name}.webp");
        (int start, int end) = CompressedData(data);
        Assert.True(end > start);
        for (int i = start; i < end; i += 7)
        {
            data[i] ^= 0x5A;
        }

        var pixels = new byte[width * height * 4];
        try
        {
            WebPCodec.Decode(data, pixels, PixelLayout.Rgba32, premultiplyAlpha: false);
        }
        catch (InvalidDataException)
        {
        }
    }

    [Fact]
    public void Decode_DoesntSupportAnimation()
    {
        // A VP8X header with the animation flag, and its canvas size, and nothing more
        byte[] animated = [.. "RIFF"u8, 22, 0, 0, 0, .. "WEBP"u8, .. "VP8X"u8, 10, 0, 0, 0, 0x02, 0, 0, 0, 4, 0, 0, 2, 0, 0];

        Assert.True(WebPCodec.TryGetDecodedSize(animated, PixelLayout.Rgba32, out int width, out int height, out _));
        Assert.Equal((5, 3), (width, height));
        Assert.Throws<NotSupportedException>(() => WebPCodec.Decode(animated, new byte[5 * 3 * 4], PixelLayout.Rgba32, false));
    }

    [Fact]
    public void Decode_RejectsABufferOfTheWrongSize()
    {
        byte[] data = TestImages.Read("WebP/Lossy1x1.webp");

        Assert.Throws<ArgumentException>(() => WebPCodec.Decode(data, new byte[3], PixelLayout.Rgba32, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPCodec.Decode(data, new byte[4], (PixelLayout)2, false));
    }

    [Fact]
    public void Decode_RejectsAFileCutShort()
    {
        byte[] data = TestImages.Read("WebP/Lossy130x67.webp");
        Assert.True(WebPCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out _, out _, out int byteCount));

        Assert.Throws<InvalidDataException>(() => WebPCodec.Decode(data.AsSpan(0, data.Length - 40), new byte[byteCount], PixelLayout.Rgba32, false));
    }

    [Theory]
    [MemberData(nameof(EncoderSettings))]
    public void Encode_WritesCwebpsFile(
        string name,
        string sha256)
    {
        byte[] pixels = EncodeSource(out int width, out int height, premultiplyAlpha: false);
        var writer = new ArrayBufferWriter<byte>();

        WebPCodec.Encode(writer, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, EncoderOptions(name));

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(writer.WrittenSpan)));
    }

    [Fact]
    public void Encode_LosslessGivesThePixelsBack()
    {
        byte[] pixels = EncodeSource(out int width, out int height, premultiplyAlpha: false);
        var writer = new ArrayBufferWriter<byte>();
        WebPCodec.Encode(writer, width, height, pixels, PixelLayout.Rgba32, false, new WebPEncoderOptions { Lossless = true, Exact = true });

        var decoded = new byte[pixels.Length];
        WebPCodec.Decode(writer.WrittenSpan, decoded, PixelLayout.Rgba32, premultiplyAlpha: false);

        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void Encode_UnpremultipliesAsPngDoes()
    {
        byte[] premultiplied = EncodeSource(out int width, out int height, premultiplyAlpha: true);
        var webp = new ArrayBufferWriter<byte>();
        WebPCodec.Encode(webp, width, height, premultiplied, PixelLayout.Rgba32, unpremultiplyAlpha: true, new WebPEncoderOptions { Lossless = true, Exact = true });
        var png = new ArrayBufferWriter<byte>();
        PngCodec.Encode(png, width, height, premultiplied, PixelLayout.Rgba32, unpremultiplyAlpha: true, PngCompressionLevel.Fastest);

        var fromWebP = new byte[premultiplied.Length];
        WebPCodec.Decode(webp.WrittenSpan, fromWebP, PixelLayout.Rgba32, premultiplyAlpha: false);
        var fromPng = new byte[premultiplied.Length];
        PngCodec.Decode(png.WrittenSpan, fromPng, PixelLayout.Rgba32, premultiplyAlpha: false);

        Assert.Equal(fromPng, fromWebP);
    }

    [Fact]
    public void Encode_WithNoOptionsUsesCwebpsDefaults()
    {
        byte[] pixels = EncodeSource(out int width, out int height, premultiplyAlpha: false);
        var withDefaults = new ArrayBufferWriter<byte>();
        var withNone = new ArrayBufferWriter<byte>();

        WebPCodec.Encode(withDefaults, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, new WebPEncoderOptions());
        WebPCodec.Encode(withNone, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false);

        Assert.Equal(withDefaults.WrittenSpan.ToArray(), withNone.WrittenSpan.ToArray());
    }

    [Fact]
    public void Encode_RejectsWhatIsntAnImage()
    {
        var writer = new ArrayBufferWriter<byte>();
        var options = new WebPEncoderOptions();

        Assert.Throws<ArgumentNullException>(() => WebPCodec.Encode(null!, 1, 1, new byte[4], PixelLayout.Rgba32, false, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPCodec.Encode(writer, 0, 1, [], PixelLayout.Rgba32, false, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPCodec.Encode(writer, 16384, 1, new byte[16384 * 4], PixelLayout.Rgba32, false, options));
        Assert.Throws<ArgumentException>(() => WebPCodec.Encode(writer, 2, 2, new byte[15], PixelLayout.Rgba32, false, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPCodec.Encode(writer, 1, 1, new byte[4], PixelLayout.Rgba32, false, new WebPEncoderOptions { Quality = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPCodec.Encode(writer, 1, 1, new byte[4], PixelLayout.Rgba32, false, new WebPEncoderOptions { Method = 7 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPCodec.Encode(writer, 1, 1, new byte[4], PixelLayout.Rgba32, false, new WebPEncoderOptions { Quality = float.NaN }));
        Assert.Equal(0, writer.WrittenCount);
    }

    // An image's compressed data, past its headers: a lossy image's token partitions (after the frame
    // header and the first partition, whose size the frame tag gives), or a lossless image's
    // bitstream after its 5-byte header
    private static (int Start, int End) CompressedData(
        byte[] webp)
    {
        int chunk = 12;
        while (chunk + 8 <= webp.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(chunk + 4));
            int payload = chunk + 8;
            if (webp.AsSpan(chunk, 4).SequenceEqual("VP8 "u8))
            {
                int firstPartition = (webp[payload] | (webp[payload + 1] << 8) | (webp[payload + 2] << 16)) >> 5;
                return (payload + 10 + firstPartition, payload + size);
            }

            if (webp.AsSpan(chunk, 4).SequenceEqual("VP8L"u8))
            {
                return (payload + 5, payload + size);
            }

            chunk = payload + size + (size & 1);
        }

        throw new InvalidDataException("No image chunk.");
    }

    // EncodeSource.png: 96 x 64, with gradients, noise, every kind of alpha and colors under transparent pixels
    private static byte[] EncodeSource(
        out int width,
        out int height,
        bool premultiplyAlpha)
    {
        byte[] png = TestImages.Read("WebP/EncodeSource.png");
        Assert.True(PngCodec.TryGetDecodedSize(png, PixelLayout.Rgba32, out width, out height, out int byteCount));
        var pixels = new byte[byteCount];
        PngCodec.Decode(png, pixels, PixelLayout.Rgba32, premultiplyAlpha);
        return pixels;
    }

    private static WebPEncoderOptions EncoderOptions(
        string name)
    {
        return name switch
        {
            "Lossless" => new WebPEncoderOptions { Lossless = true, Method = 6, Quality = 100, Exact = true },
            "Lossy" => new WebPEncoderOptions { Quality = 80, Method = 6, Exact = true },
            "Defaults" => new WebPEncoderOptions(),
            "LossyFast" => new WebPEncoderOptions { Quality = 50, Method = 0, AlphaQuality = 50, UseSharpYuv = true },
            "NearLossless" => new WebPEncoderOptions { Lossless = true, NearLossless = 60, Method = 3 },
            "LosslessThreads" => new WebPEncoderOptions { Lossless = true, Method = 6, Quality = 100, Exact = true, UseThreads = true },
            _ => throw new ArgumentException(name),
        };
    }
}
