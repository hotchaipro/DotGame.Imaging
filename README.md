# DotGame.Imaging

PNG, JPEG and WebP image codecs for .NET, with no dependencies. Trim and AOT safe.

| Format | Decode | Encode |
|---|---|---|
| PNG | Yes, written from the PNG specification, using .NET's zlib | Yes |
| JPEG | Yes, a C# port of libjpeg-turbo 3.2.1 with the same pixels | Yes, baseline and progressive: the same port, with cjpeg's files |
| WebP | Yes, a C# port of libwebp 1.6.0 with the same pixels | Yes, lossless and lossy: the same port, with cwebp's files |

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
`cjpeg -quality 90 -sample 1x1 -optimize`):

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

Times relative to the C library each format's port follows (lower is faster): libpng for PNG,
libjpeg-turbo for JPEG, libwebp for WebP. The first column is that library's time. DotGame.Imaging
and ImageSharp ran on .NET 10's JIT; DotGame.Imaging published with NativeAOT is within a few percent
of these.

PNG decode:

| Image | libpng | libspng | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Sprite atlas, 2048 x 2048, alpha | 23.4 ms | 1.00x | 1.04x | 1.29x |
| Sprite, 238 x 214, alpha | 0.31 ms | 0.99x | 0.90x | 1.07x |
| Photo, 960 x 1280 | 10.2 ms | 0.98x | 1.14x | 1.60x |

PNG encode, with the file's size (each library's default compression):

| Image | libpng | libspng | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Sprite atlas, 2048 x 2048, alpha | 300 ms, 2,891 KB | 1.32x, 2,891 KB | 0.36x, 2,877 KB | 1.00x, 2,904 KB |
| Sprite, 238 x 214, alpha | 6.14 ms, 58 KB | 1.18x, 58 KB | 0.29x, 59 KB | 1.03x, 59 KB |
| Photo, 960 x 1280 | 195 ms, 2,640 KB | 1.14x, 2,640 KB | 0.24x, 2,343 KB | 0.94x, 2,649 KB |

JPEG decode:

| Image | libjpeg-turbo | IJG libjpeg | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Photo, 3000 x 4000 | 10.8 ms | 3.16x | 1.26x | 3.45x |
| Photo, 960 x 1280 | 2.25 ms | 2.06x | 1.05x | 2.29x |

JPEG encode, quality 75, 4:2:0 (cjpeg's defaults):

| Image | libjpeg-turbo | IJG libjpeg | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|---:|
| Photo, 3000 x 4000 | 13.2 ms | 6.03x | 1.12x | 7.03x |
| Photo, 960 x 1280 | 1.68 ms | 6.90x | 1.15x | 7.06x |

WebP decode:

| Image | libwebp | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|
| Lossy: Sprite atlas, 2048 x 2048, alpha | 20.6 ms | 1.11x | 3.02x |
| Lossy: Photo, 3000 x 4000 | 23.6 ms | 1.18x | 4.44x |
| Lossy: Photo, 960 x 1280 | 10.2 ms | 1.12x | 2.41x |
| Lossless: Sprite atlas, 2048 x 2048, alpha | 25.0 ms | 0.95x | 1.93x |
| Lossless: Sprite, 238 x 214, alpha | 0.31 ms | 0.85x | 1.82x |

WebP encode, quality 75, method 4 (cwebp's defaults, and `-lossless`):

| Image | libwebp | DotGame.Imaging | ImageSharp |
|---|---:|---:|---:|
| Lossy: Sprite atlas, 2048 x 2048, alpha | 187 ms | 1.07x | 5.99x |
| Lossy: Photo, 3000 x 4000 | 330 ms | 1.06x | 8.29x |
| Lossy: Photo, 960 x 1280 | 65.2 ms | 1.02x | 7.90x |
| Lossless: Sprite atlas, 2048 x 2048, alpha | 1293 ms | 1.04x | 1.19x |
| Lossless: Sprite, 238 x 214, alpha | 40.1 ms | 0.90x | 0.93x |

The JPEG and WebP encoders write the same files as libjpeg-turbo and libwebp. The PNG encoder isn't
a port: it chooses filters its own way and compresses with .NET's zlib (zlib-ng), so its files
differ from libpng's in size.

How these were measured:

- Apple M6 Mac mini (Arm64), macOS 27.2, one thread, everything in memory.
- Each time is the median of at least 15 batches over at least 2 seconds, after a warm-up.
- The C libraries are Homebrew's builds, called from C compiled with `-O2`: libpng 1.6.59 (with
  macOS's zlib), libspng 0.7.4, libjpeg-turbo 3.2.1, IJG libjpeg 10 and libwebp 1.6.0.
- .NET 10.0.403; ImageSharp 3.1.12.
- Decoders write RGBA, except IJG libjpeg, which has no RGBA output and writes RGB. DotGame.Imaging
  decodes into a buffer it's given; ImageSharp allocates its image.
- Encoders read RGBA, except IJG libjpeg, which reads RGB.

## License

MIT (`LICENSE`). The JPEG and WebP decoders and encoders are ports of libjpeg-turbo and
libwebp, under their own licenses: see `THIRD-PARTY-NOTICES.md`. An app that ships the JPEG codec says in its credits: "this
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
