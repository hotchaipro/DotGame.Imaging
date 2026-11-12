# DotGame.Imaging documentation

DotGame.Imaging is a set of PNG, JPEG and WebP image codecs for .NET 11, with no dependencies: a
decoder and an encoder for each. The [README](../README.md) shows how to install it and how fast it
is.

## Guide

[Using DotGame.Imaging](guide.md) shows how to decode and encode an image: the size first, then the
pixels, and each encoder's options.

## API reference

[The API reference](api/README.md) has a page for every public type and member, generated from the
source code's doc comments:

| Namespace | What's in it |
|---|---|
| `DotGame.Imaging` | [`ImageDecoder`](api/ImageDecoder.md), which reads any of the formats, telling which from the bytes, and [`PixelLayout`](api/PixelLayout.md), the layout of a buffer of pixels |
| `DotGame.Imaging.Png` | [`PngCodec`](api/PngCodec.md), and [`PngCompressionLevel`](api/PngCompressionLevel.md) for encoding |
| `DotGame.Imaging.Jpeg` | [`JpegCodec`](api/JpegCodec.md), and [`JpegEncoderOptions`](api/JpegEncoderOptions.md) and [`JpegSubsampling`](api/JpegSubsampling.md) for encoding |
| `DotGame.Imaging.WebP` | [`WebPCodec`](api/WebPCodec.md), and [`WebPEncoderOptions`](api/WebPEncoderOptions.md) for encoding |

To find something fast, press `t` on the repository's GitHub page and type the type's name:
`PngCodec.md` takes you straight to it.

## Contributing

How to build, test and change the library is in [CONTRIBUTING.md](../CONTRIBUTING.md). The API
reference follows the doc comments: after changing one, regenerate it (see "The docs follow the code"
there).
