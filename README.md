# DotGame.Imaging

PNG, JPEG and WebP image codecs for .NET, with no dependencies: a decoder and an encoder for each.
Trim and AOT safe.

| Format | Based on | Matches |
|---|---|---|
| PNG | The PNG specification, with .NET's zlib | |
| JPEG | libjpeg-turbo 3.2.1, ported to C# | Its decoded pixels, and cjpeg's files (baseline and progressive) |
| WebP | libwebp 1.6.0, ported to C# | Its decoded pixels, and cwebp's files (lossless and lossy) |

## Installing

For .NET 11 and later (version 1.0.x supports .NET 10):

```bash
dotnet add package DotGame.Imaging
```

## Using it

Decoding is two calls: get the size, then decode into a buffer of that size, so decoding allocates
nothing for the pixels. `ImageDecoder` tells the format from the bytes; `PngCodec`, `JpegCodec` and
`WebPCodec` (in `DotGame.Imaging.Png`, `.Jpeg` and `.WebP`) decode one format each.

```csharp
using DotGame.Imaging;

byte[] file = File.ReadAllBytes("image.webp");
if (ImageDecoder.TryGetDecodedSize(file, PixelLayout.Rgba32, out int width, out int height, out int byteCount))
{
    byte[] pixels = new byte[byteCount];
    ImageDecoder.Decode(file, pixels, PixelLayout.Rgba32, premultiplyAlpha: false);
}
```

Encoding a PNG, to any `IBufferWriter<byte>`. A `PngCompressionLevel` trades size for speed:
`Optimal`, the default, for files that are kept, or `Fastest` and `NoCompression` for files read
again straight away:

```csharp
using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.Png;

var png = new ArrayBufferWriter<byte>();
PngCodec.Encode(png, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false);
File.WriteAllBytes("image.png", png.WrittenSpan);
```

Encoding a WebP: with the same settings, the file is byte for byte what cwebp 1.6.0 writes (here,
`cwebp -q 80 -m 6 -exact`; `Lossless = true` is `-lossless`):

```csharp
using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.WebP;

var webp = new ArrayBufferWriter<byte>();
WebPCodec.Encode(webp, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false,
    new WebPEncoderOptions { Quality = 80, Method = 6, Exact = true });
File.WriteAllBytes("image.webp", webp.WrittenSpan);
```

Encoding a JPEG: with the same settings, the file is byte for byte what cjpeg 3.2.1 writes (here,
`cjpeg -quality 90 -sample 1x1 -optimize`; `Progressive = true` is `-progressive`):

```csharp
using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.Jpeg;

var jpeg = new ArrayBufferWriter<byte>();
JpegCodec.Encode(jpeg, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false,
    new JpegEncoderOptions { Quality = 90, Subsampling = JpegSubsampling.YCbCr444, OptimizeHuffman = true });
File.WriteAllBytes("image.jpg", jpeg.WrittenSpan);
```

## Performance

Times relative to each format's reference C library (lower is faster): libpng, libjpeg-turbo and
libwebp. The first column is that library's time. DotGame.Imaging
and ImageSharp ran on .NET 11's JIT; DotGame.Imaging published with NativeAOT is within a few percent
of these.

PNG decode:

| Image | libpng | libspng | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Sprite atlas, 2048 x 2048, alpha | 23.9 ms | 0.98x | 1.03x | 1.27x |
| Sprite, 238 x 214, alpha | 0.31 ms | 0.99x | 0.91x | 1.06x |
| Photo, 960 x 1280 | 10.2 ms | 0.98x | 1.13x | 1.61x |

PNG encode, with the file's size (each library's default compression). The PNG encoder isn't a
port: it chooses filters its own way and compresses with .NET's zlib (zlib-ng), so its files differ
from libpng's in size:

| Image | libpng | libspng | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Sprite atlas, 2048 x 2048, alpha | 300 ms, 2,891 KB | 1.31x, 2,891 KB | 0.36x, 2,877 KB | 0.99x, 2,904 KB |
| Sprite, 238 x 214, alpha | 6.09 ms, 58 KB | 1.20x, 58 KB | 0.30x, 59 KB | 1.02x, 59 KB |
| Photo, 960 x 1280 | 195 ms, 2,640 KB | 1.14x, 2,640 KB | 0.24x, 2,343 KB | 0.93x, 2,649 KB |

JPEG decode:

| Image | libjpeg-turbo | IJG libjpeg | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Photo, 3000 x 4000 | 10.8 ms | 3.13x | 1.10x | 3.60x |
| Photo, 960 x 1280 | 2.02 ms | 2.29x | 1.09x | 2.65x |

JPEG encode, quality 75, 4:2:0 (cjpeg's defaults):

| Image | libjpeg-turbo | IJG libjpeg | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Photo, 3000 x 4000 | 13.1 ms | 6.07x | 1.13x | 6.96x |
| Photo, 960 x 1280 | 1.69 ms | 6.86x | 1.11x | 6.95x |

WebP decode:

| Image | libwebp | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|
| Lossy: Sprite atlas, 2048 x 2048, alpha | 20.8 ms | 1.10x | 2.85x |
| Lossy: Photo, 3000 x 4000 | 23.6 ms | 1.18x | 4.06x |
| Lossy: Photo, 960 x 1280 | 10.1 ms | 1.14x | 2.35x |
| Lossless: Sprite atlas, 2048 x 2048, alpha | 24.9 ms | 0.94x | 1.61x |
| Lossless: Sprite, 238 x 214, alpha | 0.31 ms | 0.89x | 1.70x |

WebP encode, quality 75, method 4 (cwebp's defaults, and `-lossless`):

| Image | libwebp | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|
| Lossy: Sprite atlas, 2048 x 2048, alpha | 188 ms | 1.06x | 5.68x |
| Lossy: Photo, 3000 x 4000 | 331 ms | 1.06x | 7.75x |
| Lossy: Photo, 960 x 1280 | 65.4 ms | 1.01x | 7.60x |
| Lossless: Sprite atlas, 2048 x 2048, alpha | 1301 ms | 1.05x | 1.17x |
| Lossless: Sprite, 238 x 214, alpha | 40.1 ms | 0.88x | 0.92x |

How these were measured:

- Apple M6 Mac mini (Arm64), macOS 27.2, one thread, everything in memory.
- Each time is the median of at least 15 batches over at least 2 seconds, after a warm-up.
- The C libraries are Homebrew's builds, called from C compiled with `-O2`: libpng 1.6.59 (with
  macOS's zlib), libspng 0.7.4, libjpeg-turbo 3.2.1, IJG libjpeg 10 and libwebp 1.6.0.
- .NET 11.0.0 (SDK 11.0.100); ImageSharp 3.1.12.
- Decoders write RGBA, except IJG libjpeg, which has no RGBA output and writes RGB. DotGame.Imaging
  decodes into a buffer it's given; ImageSharp allocates its image.
- Encoders read RGBA, except IJG libjpeg, which reads RGB.

## License

MIT (`LICENSE`). The JPEG and WebP decoders and encoders are ports of libjpeg-turbo and libwebp,
under their own licenses: see `THIRD-PARTY-NOTICES.md`. A product that ships the JPEG codec must
say in its documentation: "this software is based in part on the work of the Independent JPEG
Group".

## Building and testing

`src/` has the library, a folder per format, and `test/` its tests. Building needs the .NET 11 SDK
or later.

```bash
dotnet build
dotnet test
```

The API reference, a page for every public type and member, is in
[docs/](https://github.com/hotchaipro/DotGame.Imaging/blob/main/docs/README.md). To
contribute, see `CONTRIBUTING.md`.
