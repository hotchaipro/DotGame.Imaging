// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Imaging.Jpeg;
using DotGame.Imaging.Png;
using DotGame.Imaging.WebP;

namespace DotGame.Imaging.Tests;

/// <summary>
/// <see cref="ImageDecoder"/> tells the format from the bytes, and gives the size and pixels that
/// format's codec gives.
/// </summary>
public sealed class ImageDecoderTests
{
    // One image of each format; the PNG and the WebP have translucent pixels, so premultiplying
    // changes them, and the JPEG is opaque, so it doesn't
    [Theory]
    [InlineData("Png/PillowRgba.png", false)]
    [InlineData("Png/PillowRgba.png", true)]
    [InlineData("Jpeg/Baseline411.jpg", false)]
    [InlineData("Jpeg/Baseline411.jpg", true)]
    [InlineData("WebP/LossyAlpha37x21.webp", false)]
    [InlineData("WebP/LossyAlpha37x21.webp", true)]
    public void Decode_GivesWhatTheFormatsCodecGives(
        string path,
        bool premultiplyAlpha)
    {
        byte[] data = TestImages.Read(path);
        var (size, expected) = DecodeWithCodec(path[..path.IndexOf('/', StringComparison.Ordinal)], data, premultiplyAlpha);

        Assert.True(ImageDecoder.TryGetDecodedSize(data, PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal(size, (width, height, byteCount));
        var pixels = new byte[byteCount];
        ImageDecoder.Decode(data, pixels, PixelLayout.Rgba32, premultiplyAlpha);

        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void Decode_DoesntSupportOtherFormats()
    {
        byte[] gif = "GIF89a\u0001\0\u0001\0\0\0\0;"u8.ToArray();

        Assert.False(ImageDecoder.TryGetDecodedSize(gif, PixelLayout.Rgba32, out int width, out int height, out int byteCount));
        Assert.Equal((0, 0, 0), (width, height, byteCount));
        Assert.Throws<NotSupportedException>(() => ImageDecoder.Decode(gif, new byte[4], PixelLayout.Rgba32, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageDecoder.Decode(gif, new byte[4], (PixelLayout)5, false));
    }

    // The size and pixels the format's own codec gives
    private static ((int Width, int Height, int ByteCount) Size, byte[] Pixels) DecodeWithCodec(
        string format,
        byte[] data,
        bool premultiplyAlpha)
    {
        int width;
        int height;
        int byteCount;
        bool sized = format switch
        {
            "Png" => PngCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out width, out height, out byteCount),
            "Jpeg" => JpegCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out width, out height, out byteCount),
            _ => WebPCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out width, out height, out byteCount),
        };
        Assert.True(sized);
        var pixels = new byte[byteCount];
        switch (format)
        {
            case "Png":
                PngCodec.Decode(data, pixels, PixelLayout.Rgba32, premultiplyAlpha);
                break;
            case "Jpeg":
                JpegCodec.Decode(data, pixels, PixelLayout.Rgba32);
                break;
            default:
                WebPCodec.Decode(data, pixels, PixelLayout.Rgba32, premultiplyAlpha);
                break;
        }

        return ((width, height, byteCount), pixels);
    }
}
