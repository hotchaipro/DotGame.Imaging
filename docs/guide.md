# Using DotGame.Imaging

How to decode and encode PNG, JPEG and WebP images. The [API reference](api/README.md) has every type,
member and option.

## Decoding

Decoding is two calls: [`TryGetDecodedSize`](api/ImageDecoder.md#trygetdecodedsize) gets the size, then
[`Decode`](api/ImageDecoder.md#decode) fills a buffer of that size, so decoding allocates nothing for
the pixels. [`ImageDecoder`](api/ImageDecoder.md) tells the format from the bytes;
[`PngCodec`](api/PngCodec.md), [`JpegCodec`](api/JpegCodec.md) and [`WebPCodec`](api/WebPCodec.md) (in
`DotGame.Imaging.Png`, `.Jpeg` and `.WebP`) decode one format each. A
[`PixelLayout`](api/PixelLayout.md) says how the buffer's pixels are laid out.

```csharp
using DotGame.Imaging;

byte[] file = File.ReadAllBytes("image.webp");
if (ImageDecoder.TryGetDecodedSize(file, PixelLayout.Rgba32, out int width, out int height, out int byteCount))
{
    byte[] pixels = new byte[byteCount];
    ImageDecoder.Decode(file, pixels, PixelLayout.Rgba32, premultiplyAlpha: false);
}
```

## Encoding

Each codec's `Encode` writes the file to any `IBufferWriter<byte>`.

### PNG

A [`PngCompressionLevel`](api/PngCompressionLevel.md) trades size for speed: `Optimal`, the default,
for files that are kept, or `Fastest` and `NoCompression` for files read again straight away:

```csharp
using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.Png;

var png = new ArrayBufferWriter<byte>();
PngCodec.Encode(png, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false);
File.WriteAllBytes("image.png", png.WrittenSpan);
```

### WebP

With the same settings, the file is byte for byte what cwebp 1.6.0 writes (here,
`cwebp -q 80 -m 6 -exact`; `Lossless = true` is `-lossless`). The options are in
[`WebPEncoderOptions`](api/WebPEncoderOptions.md):

```csharp
using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.WebP;

var webp = new ArrayBufferWriter<byte>();
WebPCodec.Encode(webp, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false,
    new WebPEncoderOptions { Quality = 80, Method = 6, Exact = true });
File.WriteAllBytes("image.webp", webp.WrittenSpan);
```

### JPEG

With the same settings, the file is byte for byte what cjpeg 3.2.1 writes (here,
`cjpeg -quality 90 -sample 1x1 -optimize`; `Progressive = true` is `-progressive`). The options are
in [`JpegEncoderOptions`](api/JpegEncoderOptions.md):

```csharp
using System.Buffers;
using DotGame.Imaging;
using DotGame.Imaging.Jpeg;

var jpeg = new ArrayBufferWriter<byte>();
JpegCodec.Encode(jpeg, width, height, pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false,
    new JpegEncoderOptions { Quality = 90, Subsampling = JpegSubsampling.YCbCr444, OptimizeHuffman = true });
File.WriteAllBytes("image.jpg", jpeg.WrittenSpan);
```
