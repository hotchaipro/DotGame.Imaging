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

The [guide](https://github.com/hotchaipro/DotGame.Imaging/blob/main/docs/guide.md) shows how to
decode and encode an image, and the
[API reference](https://github.com/hotchaipro/DotGame.Imaging/blob/main/docs/README.md) has a page
for every public type and member, with every option the encoders take.

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
