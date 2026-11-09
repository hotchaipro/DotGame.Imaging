// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using DotGame.Imaging.Png;

namespace DotGame.Imaging.Tests;

/// <summary>
/// Damaged images the fuzzer (fuzz/) made, each of which once broke a decoder, in Images/Fuzz: the
/// decoders refuse them as they promise, with the exceptions they document.
/// </summary>
public sealed class FuzzedImageTests
{
    public static TheoryData<string> Files()
    {
        var files = new TheoryData<string>();
        string folder = Path.Combine(AppContext.BaseDirectory, "Images");
        foreach (string file in Directory.GetFiles(Path.Combine(folder, "Fuzz"), "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetRelativePath(folder, file).Replace('\\', '/'));
        }

        return files;
    }

    // What the fuzzer checks of every input: a size that's the pixels' byte count, only
    // InvalidDataException or NotSupportedException for a damaged image, and, for one that decodes,
    // every byte written and the same pixels each time
    [Theory]
    [MemberData(nameof(Files))]
    public void Decode_KeepsTheDecodersPromises(
        string path)
    {
        byte[] data = TestImages.Read(path);
        if (!ImageDecoder.TryGetDecodedSize(data, PixelLayout.Rgba32, out int width, out int height, out int byteCount))
        {
            var error = Record.Exception(() => ImageDecoder.Decode(data, [], PixelLayout.Rgba32, false));
            Assert.True(error is InvalidDataException or NotSupportedException or ArgumentException, $"Decode threw {error}");
            return;
        }

        Assert.Equal((long)width * height * 4, byteCount);
        var first = new byte[byteCount];
        var second = new byte[byteCount];
        second.AsSpan().Fill(0xFF);
        var firstError = Record.Exception(() => ImageDecoder.Decode(data, first, PixelLayout.Rgba32, false));
        var secondError = Record.Exception(() => ImageDecoder.Decode(data, second, PixelLayout.Rgba32, false));
        if (firstError != null)
        {
            Assert.True(firstError is InvalidDataException or NotSupportedException, $"Decode threw {firstError}");
            Assert.Equal(firstError.GetType(), secondError?.GetType());
        }
        else
        {
            Assert.Null(secondError);
            Assert.Equal(first, second);
        }
    }

    [Theory]
    // A Huffman table with more codes of a length than fit in its bits, which overran the decoder's
    // lookup table (IndexOutOfRangeException) before the check that refuses it
    [InlineData("Fuzz/Jpeg/HuffmanTableOverflow.jpg")]
    // Scans naming Huffman tables 4 to 15, of the four there are: IndexOutOfRangeException
    [InlineData("Fuzz/Jpeg/ScanAcTableOutOfRange.jpg")]
    [InlineData("Fuzz/Jpeg/ScanDcTableOutOfRange.jpg")]
    public void Decode_RefusesDamagedJpeg(
        string path)
    {
        byte[] data = TestImages.Read(path);
        int byteCount = ImageDecoder.TryGetDecodedSize(data, PixelLayout.Rgba32, out _, out _, out int count) ? count : 0;

        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(data, new byte[byteCount], PixelLayout.Rgba32, false));
    }

    // A PNG image 1229209940 x 2027578208, whose byte count overflowed a long: TryGetDecodedSize said
    // it read, with a negative byte count
    [Fact]
    public void TryGetDecodedSize_RefusesImagesTooLargeForABuffer()
    {
        byte[] data = TestImages.Read("Fuzz/Png/HugeSize.png");

        Assert.False(PngCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal((0, 0, 0), (width, height, byteCount));
        Assert.False(PixelLayouts.TryGetByteCount(int.MaxValue, int.MaxValue, 4, out _));
        Assert.False(PixelLayouts.TryGetByteCount(65536, 8192, 4, out _));
        Assert.True(PixelLayouts.TryGetByteCount(8192, 8192, 4, out byteCount));
        Assert.Equal(8192 * 8192 * 4, byteCount);
    }
}
