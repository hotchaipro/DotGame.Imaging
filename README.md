# DotGame.Imaging

PNG, JPEG and WebP image codecs for .NET, with no dependencies. Trim and AOT safe.

| Format | Decode | Encode |
|---|---|---|
| PNG | Yes, written from the PNG specification, using .NET's zlib | Yes |
| JPEG | Yes, a C# port of libjpeg-turbo 3.2.1 with the same pixels | No |
| WebP | Yes, a C# port of libwebp 1.6.0 with the same pixels | No |

Pixels go into a buffer you provide, so decoding allocates nothing for them.

## Installing

For .NET 10 and later:

```bash
dotnet add package DotGame.Imaging
```

## Using it

Decoding: the size first, then decode into a buffer of that size. `ImageDecoder` tells the format
from the bytes; `PngCodec`, `JpegCodec` and `WebPCodec` (in `DotGame.Imaging.Png`, `.Jpeg` and
`.WebP`) decode one format each.

```csharp
using DotGame.Imaging;

byte[] file = File.ReadAllBytes("image.webp");
if (ImageDecoder.TryGetDecodedSize(file, PixelLayout.Rgba32, out int width, out int height, out int byteCount))
{
    byte[] pixels = new byte[byteCount];
    ImageDecoder.Decode(file, pixels, PixelLayout.Rgba32, premultiplyAlpha: false);
}
```

Encoding a PNG, to any `IBufferWriter<byte>`:

```csharp
using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.Png;

var png = new ArrayBufferWriter<byte>();
PngCodec.Encode(png, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false);
File.WriteAllBytes("image.png", png.WrittenSpan);
```

## License

MIT (`LICENSE`). The JPEG and WebP decoders are ports of libjpeg-turbo and libwebp, under their own
licenses: see `THIRD-PARTY-NOTICES.md`. An app that ships the JPEG decoder says in its credits: "this
software is based in part on the work of the Independent JPEG Group".

## Building

`src/` has the library, a folder per format, and `test/` its tests. Building needs the .NET 10 SDK
or later.

```bash
dotnet build
```

## Testing

```bash
dotnet test
```
