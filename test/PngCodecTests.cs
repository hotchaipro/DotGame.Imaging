// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using DotGame.Imaging.Png;

namespace DotGame.Imaging.Tests;

/// <summary>
/// The decoder against PNGs from other encoders and against plain references of its fast paths (each
/// filter for each pixel size, Paeth rows decoded in pairs, the premultiply arithmetic), and the
/// encoder's choices.
/// </summary>
public sealed class PngCodecTests
{
    // PNGs from an encoder independent of this one (every row filter, Adam7, every color type and
    // bit depth, palettes, tRNS, data split over IDAT chunks, an ancillary chunk), and one from
    // Pillow's, with their pixels as straight RGBA hex. Where Pillow reads the format at 8 bits, the
    // pixels are Pillow's; 16-bit samples keep their high byte.
    public static readonly TheoryData<string, int, int, string> Fixtures = new()
    {
        { "Rgba8AllFilters", 7, 5, "00000028250500464a0a00646f0f0082941400a0b91900bede1e00dc0b47002f304c0d4d55511a6b7a5627899f5b34a7c46041c5e9654ee3168e00363b931a5460983472859d4e90aaa268aecfa782ccf4ac9cea21d5003d46da275b6bdf4e7990e47597b5e99cb5daeec3d3fff3eaf12c1c004451213462762668809b2b9c9ec030d0bce53504da0a3a38f8" },
        { "Rgb8Interlaced", 9, 7, "000000ff1d0011ff3a0022ff570033ff740044ff910055ffae0066ffcb0077ffe80088ff002511ff1d2522ff3a2533ff572544ff742555ff912566ffae2577ffcb2588ffe82599ff004a22ff1d4a33ff3a4a44ff574a55ff744a66ff914a77ffae4a88ffcb4a99ffe84aaaff006f33ff1d6f44ff3a6f55ff576f66ff746f77ff916f88ffae6f99ffcb6faaffe86fbbff009444ff1d9455ff3a9466ff579477ff749488ff919499ffae94aaffcb94bbffe894ccff00b955ff1db966ff3ab977ff57b988ff74b999ff91b9aaffaeb9bbffcbb9ccffe8b9ddff00de66ff1dde77ff3ade88ff57de99ff74deaaff91debbffaedeccffcbdeddffe8deeeff" },
        { "Gray2", 5, 3, "000000ff555555ffaaaaaaffffffffff000000ff555555ffaaaaaaffffffffff000000ff555555ffaaaaaaffffffffff000000ff555555ffaaaaaaff" },
        { "Gray1", 11, 2, "000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff000000ffffffffff" },
        { "Gray16Transparent", 4, 3, "000000ff23232300464646ff696969ff4e4e4eff717171ff949494ffb7b7b7ff9c9c9cffbfbfbfffe2e2e2ff050505ff" },
        { "Rgb16Transparent", 3, 3, "000004ff4e0004ff9c0004ff007504ff4e7504009c7504ff00ea04ff4eea04ff9cea04ff" },
        { "Palette4Transparent", 6, 4, "ff00000000ff00800000fffffac864ff0a141effff0000000000fffffac864ff0a141effff00000000ff00800000ffff0a141effff00000000ff00800000fffffac864ff0a141eff00ff00800000fffffac864ff0a141effff00000000ff0080" },
        { "GrayAlpha8", 4, 4, "0000000f3c3c3c0f7878780fb4b4b40f0000005f3c3c3c5f7878785fb4b4b45f000000af3c3c3caf787878afb4b4b4af000000ff3c3c3cff787878ffb4b4b4ff" },
        { "Rgba16Interlaced", 3, 3, "00009cff75009cfcea009cf800759cff75759cfcea759cf800ea9cff75ea9cfceaea9cf8" },
        { "OnePixelInterlaced", 1, 1, "0a141e28" },
        { "PillowRgba", 16, 9, "00000000100000082000001030000018400000205000002860000030700000388000004090000048a0000050b0000058c0000060d0000068e0000070f0000078001c0014101c031c201c0624301c092c401c0c34501c0f3c601c1244701c154c801c1854901c1b5ca01c1e64b01c216cc01c2474d01c277ce01c2a84f01c2d8c003800281038063020380c38303812404038184850381e506038245870382a608038306890383670a0383c78b0384280c0384888d0384e90e0385498f0385aa00054003c105409442054124c30541b544054245c50542d646054366c70543f748054487c90545184a0545a8cb0546394c0546c9cd05475a4e0547eacf05487b40070005010700c5820701860307024684070307050703c7860704880707054888070609090706c98a07078a0b07084a8c07090b0d0709cb8e070a8c0f070b4c8008c0064108c0f6c208c1e74308c2d7c408c3c84508c4b8c608c5a94708c699c808c78a4908c87aca08c96b4b08ca5bcc08cb4c4d08cc3cce08cd2d4f08ce1dc00a8007810a8128020a8248830a8369040a8489850a85aa060a86ca870a87eb080a890b890a8a2c0a0a8b4c8b0a8c6d0c0a8d8d8d0a8eae0e0a8fce8f0a80ef000c4008c10c4159420c42a9c30c43fa440c454ac50c469b460c47ebc70c493c480c4a8cc90c4bdd4a0c4d2dcb0c4e7e4c0c4fcecd0c411f4e0c426fcf0c43b0400e000a010e018a820e030b030e048b840e060c050e078c860e090d070e0a8d880e0c0e090e0d8e8a0e0f0f0b0e008f8c0e02000d0e03808e0e05010f0e06818" },
    };

    [Fact]
    public void TryGetDecodedSize_ReadsTheHeader()
    {
        byte[] png = TestImages.Read("Png/Rgb8Interlaced.png");

        Assert.True(PngCodec.TryGetDecodedSize(png, PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal((9, 7, 9 * 7 * 4), (width, height, byteCount));

        // The header is enough: the signature and IHDR
        Assert.True(PngCodec.TryGetDecodedSize(png.AsSpan(0, 33), PixelLayout.Rgba32, out _, out _, out _));
    }

    [Fact]
    public void TryGetDecodedSize_IsFalseForWhatIsntAReadablePng()
    {
        byte[] png = Encode(4, 4, new byte[64], false);
        byte[] damaged = (byte[])png.Clone();
        damaged[20] ^= 1;

        Assert.False(PngCodec.TryGetDecodedSize([], PixelLayout.Rgba32, out _, out _, out _));
        Assert.False(PngCodec.TryGetDecodedSize([0xff, 0xd8, 0xff, 0xe0, 0, 0, 0, 0, 0, 0], PixelLayout.Rgba32, out _, out _, out _));
        Assert.False(PngCodec.TryGetDecodedSize(png.AsSpan(0, 30), PixelLayout.Rgba32, out _, out _, out _));
        Assert.False(PngCodec.TryGetDecodedSize(damaged, PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal((0, 0, 0), (width, height, byteCount));
    }

    [Fact]
    public void TryGetDecodedSize_GivesTheSizeOfAnImageTooLargeToDecode()
    {
        byte[] png = PngFiles.Write(9000, 9000, PngFiles.Gray, 8, [], [], headerOnly: true);

        Assert.True(PngCodec.TryGetDecodedSize(png, PixelLayout.Rgba32, out int width, out int height, out _));
        Assert.Equal((9000, 9000), (width, height));
        var error = Assert.Throws<NotSupportedException>(() => PngCodec.Decode(png, new byte[4], PixelLayout.Rgba32, false));
        Assert.Contains("8192", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decode_ReadsEveryFormat(
        string name,
        int width,
        int height,
        string expected)
    {
        var pixels = Decode(TestImages.Read($"Png/{name}.png"), premultiplyAlpha: false, out int w, out int h);

        Assert.Equal((width, height), (w, h));
        Assert.Equal(expected, Convert.ToHexStringLower(pixels));
    }

    // Color type, bit depth, and the bytes per pixel the filters step by
    [Theory]
    [InlineData(PngFiles.Gray, 8, 1)]
    [InlineData(PngFiles.GrayAlpha, 8, 2)]
    [InlineData(PngFiles.Rgb, 8, 3)]
    [InlineData(PngFiles.Rgba, 8, 4)]
    [InlineData(PngFiles.Rgb, 16, 6)]
    [InlineData(PngFiles.Rgba, 16, 8)]
    public void Decode_UnfiltersEveryFilterAtEveryPixelSize(
        byte colorType,
        byte bitDepth,
        int bytesPerPixel)
    {
        // Rows filtered in the order below (0 none, 1 sub, 2 up, 3 average, 4 Paeth): every filter after
        // every other, and runs of Paeth rows (decoded two at a time) of odd and even lengths. Widths
        // odd and even.
        byte[] filters = [4, 4, 0, 1, 2, 3, 4, 1, 4, 4, 4, 2, 4, 3, 0, 4, 4, 4, 4, 1, 3, 2];
        foreach (int width in new[] { 1, 2, 3, 17, 64 })
        {
            int height = filters.Length;
            var random = new Random((width * 31) + bytesPerPixel);
            var samples = new byte[width * bytesPerPixel * height];
            random.NextBytes(samples);
            byte[] png = PngFiles.Write(width, height, colorType, bitDepth, samples, filters);

            var pixels = Decode(png, premultiplyAlpha: false, out _, out _);

            for (int i = 0; i < width * height; i++)
            {
                Assert.Equal(Expected(samples, i * bytesPerPixel, colorType, bitDepth), pixels.AsSpan(i * 4, 4).ToArray());
            }
        }
    }

    [Fact]
    public void Decode_Premultiplies()
    {
        byte[] pixels = [200, 100, 50, 128, 255, 255, 255, 0, 10, 20, 30, 255];
        var png = Encode(3, 1, pixels, false);

        var decoded = Decode(png, premultiplyAlpha: true, out _, out _);

        Assert.Equal([100, 50, 25, 128, 0, 0, 0, 0, 10, 20, 30, 255], decoded);
    }

    [Fact]
    public void Decode_RoundTripsPremultipliedPixelsWithinRounding()
    {
        // Dividing by alpha and multiplying again rounds, so a color may come back a step off
        var pixels = new byte[16 * 16 * 4];
        for (int i = 0; i < 256; i++)
        {
            byte a = (byte)i;
            pixels[i * 4] = (byte)((a * 3) / 4);
            pixels[(i * 4) + 1] = (byte)(a / 2);
            pixels[(i * 4) + 2] = (byte)(a / 5);
            pixels[(i * 4) + 3] = a;
        }

        var decoded = Decode(Encode(16, 16, pixels, unpremultiplyAlpha: true), premultiplyAlpha: true, out _, out _);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(pixels[i + 3], decoded[i + 3]);
            for (int c = 0; c < 3; c++)
            {
                Assert.InRange(decoded[i + c] - pixels[i + c], -1, 1);
            }
        }
    }

    [Fact]
    public void Decode_PremultipliesEveryColorAndAlphaExactly()
    {
        // Red takes every value, alpha every value, green and blue others
        var pixels = new byte[256 * 256 * 4];
        for (int a = 0; a < 256; a++)
        {
            for (int x = 0; x < 256; x++)
            {
                int i = ((a * 256) + x) * 4;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)(255 - x);
                pixels[i + 2] = (byte)(x * 7);
                pixels[i + 3] = (byte)a;
            }
        }

        var decoded = Decode(Encode(256, 256, pixels, false), premultiplyAlpha: true, out _, out _);

        Assert.Equal(TestPixels.Premultiplied(pixels), decoded);
    }

    [Fact]
    public void Decode_PremultipliesGrayAndAlpha()
    {
        var pixels = new byte[256 * 256 * 4];
        for (int i = 0; i < 256 * 256; i++)
        {
            byte gray = (byte)i;
            pixels[i * 4] = gray;
            pixels[(i * 4) + 1] = gray;
            pixels[(i * 4) + 2] = gray;
            pixels[(i * 4) + 3] = (byte)(i >> 8);
        }

        var decoded = Decode(Encode(256, 256, pixels, false), premultiplyAlpha: true, out _, out _);

        Assert.Equal(TestPixels.Premultiplied(pixels), decoded);
    }

    [Fact]
    public void Decode_RejectsABufferOfTheWrongSize()
    {
        byte[] png = Encode(4, 4, new byte[64], false);

        Assert.Throws<ArgumentException>(() => PngCodec.Decode(png, new byte[63], PixelLayout.Rgba32, false));
        Assert.Throws<ArgumentException>(() => PngCodec.Decode(png, new byte[65], PixelLayout.Rgba32, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngCodec.Decode(png, new byte[64], (PixelLayout)7, false));
    }

    [Fact]
    public void Decode_RejectsBytesThatArentAPng()
    {
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode([0xff, 0xd8, 0xff, 0xe0, 0, 0, 0, 0, 0, 0], new byte[4], PixelLayout.Rgba32, false));
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode([], new byte[4], PixelLayout.Rgba32, false));
    }

    [Fact]
    public void Decode_RejectsADamagedChunk()
    {
        var png = Encode(4, 4, new byte[64], false);
        png[^20] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(png, new byte[64], PixelLayout.Rgba32, false));
    }

    [Fact]
    public void Decode_RejectsAnImageCutShort()
    {
        var png = Encode(4, 4, new byte[64], false);

        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(png.AsSpan(0, png.Length - 16), new byte[64], PixelLayout.Rgba32, false));
    }

    [Fact]
    public void Decode_RejectsPixelsThatArentZlib()
    {
        byte[] png = PngFiles.Write(2, 2, PngFiles.Rgba, 8, new byte[16], [PngFiles.None, PngFiles.None]);

        // The zlib header's compression method, which the chunk's CRC is then made to match
        int idat = PngFiles.FindChunk(png, "IDAT"u8);
        png[idat + 8] = 0x77;
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat));
        uint crc = PngFiles.Crc(png.AsSpan(idat + 4, 4 + length));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(idat + 8 + length), crc);

        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(png, new byte[16], PixelLayout.Rgba32, false));
    }

    [Fact]
    public void Decode_DoesntSupportAnUnknownCriticalChunk()
    {
        byte[] png = PngFiles.Write(1, 1, PngFiles.Rgba, 8, new byte[4], [PngFiles.None], extraChunk: "ZZZZ");

        Assert.Throws<NotSupportedException>(() => PngCodec.Decode(png, new byte[4], PixelLayout.Rgba32, false));
    }

    [Theory]
    [InlineData(PngCompressionLevel.Optimal)]
    [InlineData(PngCompressionLevel.Fastest)]
    [InlineData(PngCompressionLevel.NoCompression)]
    [InlineData(PngCompressionLevel.SmallestSize)]
    public void Encode_IsReadBackAtEveryCompressionLevel(
        PngCompressionLevel level)
    {
        var pixels = new byte[37 * 23 * 4];
        for (int i = 0; i < pixels.Length / 4; i++)
        {
            pixels[i * 4] = (byte)(i * 7);
            pixels[(i * 4) + 1] = (byte)(i * 13);
            pixels[(i * 4) + 2] = (byte)(i * 3);
            pixels[(i * 4) + 3] = (byte)(i * 11);
        }

        var decoded = Decode(Encode(37, 23, pixels, false, level), premultiplyAlpha: false, out int width, out int height);

        Assert.Equal((37, 23), (width, height));
        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void Encode_WritesLargeImagesInSeveralChunks()
    {
        // Noise doesn't compress, so its data takes more than one IDAT chunk of 64 KB
        var pixels = new byte[300 * 200 * 4];
        new Random(9).NextBytes(pixels);

        byte[] png = Encode(300, 200, pixels, false, PngCompressionLevel.Fastest);

        Assert.True(PngFiles.CountChunks(png, "IDAT"u8) > 1);
        Assert.Equal(pixels, Decode(png, premultiplyAlpha: false, out _, out _));
    }

    [Theory]
    [InlineData(255, false, PngFiles.Gray)]
    [InlineData(128, false, PngFiles.GrayAlpha)]
    [InlineData(255, true, PngFiles.Rgb)]
    [InlineData(128, true, PngFiles.Rgba)]
    public void Encode_WritesTheSmallestColorTypeThatHoldsThePixels(
        byte alpha,
        bool color,
        byte colorType)
    {
        var pixels = new byte[19 * 7 * 4];
        for (int i = 0; i < 19 * 7; i++)
        {
            byte value = (byte)(i * 5);
            pixels[i * 4] = value;
            pixels[(i * 4) + 1] = color ? (byte)(i * 3) : value;
            pixels[(i * 4) + 2] = value;
            pixels[(i * 4) + 3] = (i == 3) ? alpha : (byte)255;
        }

        byte[] png = Encode(19, 7, pixels, false);

        Assert.Equal(colorType, png[PngFiles.ColorTypeOffset]);
        Assert.Equal(pixels, Decode(png, premultiplyAlpha: false, out _, out _));

        // Faster levels write RGBA as it is
        Assert.Equal(PngFiles.Rgba, Encode(19, 7, pixels, false, PngCompressionLevel.Fastest)[PngFiles.ColorTypeOffset]);
    }

    [Fact]
    public void Encode_FiltersWhatPredictionShrinks()
    {
        var gradient = new byte[128 * 128 * 4];
        var flat = new byte[128 * 128 * 4];
        var random = new Random(5);
        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < 128; x++)
            {
                // A noisy gradient, which prediction makes small, and noise of two colors, whose
                // differences take more values than the colors do
                int i = ((y * 128) + x) * 4;
                gradient[i] = (byte)(x + random.Next(4));
                gradient[i + 1] = (byte)(y + random.Next(4));
                gradient[i + 2] = (byte)(x + y);
                gradient[i + 3] = 255;
                bool first = random.Next(2) == 0;
                flat[i] = first ? (byte)200 : (byte)20;
                flat[i + 1] = first ? (byte)30 : (byte)180;
                flat[i + 2] = first ? (byte)90 : (byte)60;
                flat[i + 3] = first ? (byte)255 : (byte)0;
            }
        }

        byte[] filtered = Encode(128, 128, gradient, false);
        byte[] unfiltered = Encode(128, 128, flat, false);

        Assert.Equal(PngFiles.Paeth, PngFiles.FirstRowFilter(filtered));
        Assert.Equal(PngFiles.None, PngFiles.FirstRowFilter(unfiltered));
        Assert.Equal(gradient, Decode(filtered, premultiplyAlpha: false, out _, out _));
        Assert.Equal(flat, Decode(unfiltered, premultiplyAlpha: false, out _, out _));
    }

    [Fact]
    public void Encode_RejectsWhatIsntAnImage()
    {
        var writer = new ArrayBufferWriter<byte>();

        Assert.Throws<ArgumentNullException>(() => PngCodec.Encode(null!, 1, 1, new byte[4], PixelLayout.Rgba32, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngCodec.Encode(writer, 0, 1, [], PixelLayout.Rgba32, false));
        Assert.Throws<ArgumentException>(() => PngCodec.Encode(writer, 2, 2, new byte[15], PixelLayout.Rgba32, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngCodec.Encode(writer, 1, 1, new byte[4], PixelLayout.Rgba32, false, (PngCompressionLevel)9));
        Assert.Equal(0, writer.WrittenCount);
    }

    [Fact]
    public void Crc_MatchesTheBytewiseCrc()
    {
        var random = new Random(3);
        foreach (int length in new[] { 0, 1, 7, 8, 9, 63, 1000 })
        {
            var data = new byte[length];
            random.NextBytes(data);

            Assert.Equal(PngFiles.Crc([.. "IDAT"u8, .. data]), PngChunks.Crc("IDAT"u8, data));
        }

        Assert.Equal(0xae426082u, PngChunks.Crc("IEND"u8, ReadOnlySpan<byte>.Empty));
    }

    private static byte[] Decode(
        byte[] png,
        bool premultiplyAlpha,
        out int width,
        out int height)
    {
        Assert.True(PngCodec.TryGetDecodedSize(png, PixelLayout.Rgba32, out width, out height, out int byteCount));
        var pixels = new byte[byteCount];
        PngCodec.Decode(png, pixels, PixelLayout.Rgba32, premultiplyAlpha);
        return pixels;
    }

    private static byte[] Encode(
        int width,
        int height,
        byte[] pixels,
        bool unpremultiplyAlpha,
        PngCompressionLevel level = PngCompressionLevel.Optimal)
    {
        var writer = new ArrayBufferWriter<byte>();
        PngCodec.Encode(writer, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha, level);
        return writer.WrittenSpan.ToArray();
    }

    // A pixel as the PNG specification reads it: 16-bit samples keep their high byte
    private static byte[] Expected(
        byte[] samples,
        int offset,
        byte colorType,
        byte bitDepth)
    {
        int size = bitDepth / 8;
        byte Sample(int index) => samples[offset + (index * size)];
        return colorType switch
        {
            PngFiles.Gray => [Sample(0), Sample(0), Sample(0), 255],
            PngFiles.GrayAlpha => [Sample(0), Sample(0), Sample(0), Sample(1)],
            PngFiles.Rgb => [Sample(0), Sample(1), Sample(2), 255],
            _ => [Sample(0), Sample(1), Sample(2), Sample(3)],
        };
    }
}
