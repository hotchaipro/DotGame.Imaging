// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Imaging.Png;

/// <summary>
/// How hard <see cref="PngCodec.Encode"/> works to make a PNG file small: its members, and their order, are
/// those of the runtime's <c>System.IO.Compression.CompressionLevel</c>.
/// </summary>
public enum PngCompressionLevel
{
    /// <summary>
    /// A small file for the time it takes: the smallest color type that holds the pixels (RGB when
    /// every pixel is opaque, gray when every pixel is gray), and rows unfiltered or Paeth filtered,
    /// whichever a sample of them compresses smaller (unfiltered for flat-colored art, Paeth for
    /// photographs, gradients and most screenshots). For files that are kept or sent.
    /// </summary>
    Optimal,

    /// <summary>
    /// As fast as encoding goes: RGBA, unfiltered, compressed as quickly as deflate can. For files
    /// that are written and read again straight away, such as a build's intermediate images.
    /// </summary>
    Fastest,

    /// <summary>
    /// RGBA, unfiltered, and not compressed (deflate's stored blocks): the file is a little larger than
    /// the pixels.
    /// </summary>
    NoCompression,

    /// <summary>
    /// The choices of <see cref="Optimal"/>, with deflate's slowest and smallest compression.
    /// </summary>
    SmallestSize,
}
