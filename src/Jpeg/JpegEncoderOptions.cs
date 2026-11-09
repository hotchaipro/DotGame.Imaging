// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Imaging.Jpeg;

/// <summary>
/// How a JPEG image's chroma (its Cb and Cr components) is sampled against its luma, as cjpeg's
/// <c>-sample</c> sets it.
/// </summary>
public enum JpegSubsampling
{
    /// <summary>4:2:0, cjpeg's default (<c>-sample 2x2</c>): chroma at half the width and half the height.</summary>
    YCbCr420,

    /// <summary>4:2:2 (<c>-sample 2x1</c>): chroma at half the width.</summary>
    YCbCr422,

    /// <summary>4:4:4 (<c>-sample 1x1</c>): chroma at full size.</summary>
    YCbCr444,
}

/// <summary>
/// The forward DCT the JPEG encoder uses, as cjpeg's <c>-dct</c> chooses it.
/// </summary>
internal enum JpegEncoderDct
{
    /// <summary>The accurate integer DCT (<c>-dct int</c>), cjpeg's default.</summary>
    Integer,

    /// <summary>The faster, less accurate integer DCT (<c>-dct fast</c>).</summary>
    Fast,
}

/// <summary>
/// How <see cref="JpegCodec.Encode"/> writes an image: its defaults are cjpeg 3.2.1's, and with the same
/// settings the file is byte for byte what cjpeg writes. Each setting is the cjpeg switch it names.
/// </summary>
public sealed class JpegEncoderOptions
{
    /// <summary>
    /// The quality, 0 to 100 (<c>-quality</c>; 75 by default), scaling the standard quantization tables
    /// as libjpeg does: below 50 by 5000 / quality, from 50 by 200 - 2 x quality (0 counts as 1).
    /// </summary>
    public int Quality { get; init; } = 75;

    /// <summary>
    /// Whether the quantization tables are limited to 8-bit values, so the file is baseline at any
    /// quality (<c>-baseline</c>). Without it, qualities below 25 can need 16-bit tables, and the file
    /// is extended sequential (SOF1).
    /// </summary>
    internal bool ForceBaseline { get; init; }

    /// <summary>The chroma subsampling (<c>-sample</c>): 4:2:0 by default. A grayscale file has none.</summary>
    public JpegSubsampling Subsampling { get; init; } = JpegSubsampling.YCbCr420;

    /// <summary>Whether the file is grayscale, its one component the pixels' luma (<c>-grayscale</c>).</summary>
    public bool Grayscale { get; init; }

    /// <summary>
    /// Whether the Huffman tables are made for the image, in a pass that counts its symbols, rather than
    /// the standard ones (<c>-optimize</c>). A progressive file always has them.
    /// </summary>
    public bool OptimizeHuffman { get; init; }

    /// <summary>Whether the file is progressive, with libjpeg's simple progression (<c>-progressive</c>).</summary>
    public bool Progressive { get; init; }

    /// <summary>
    /// The restart interval in MCU rows (<c>-restart N</c>), or 0 for none; it wins over
    /// <see cref="RestartBlocks"/>. Each scan's interval is that many rows of its MCUs, at most 65535 MCUs.
    /// </summary>
    internal int RestartRows { get; init; }

    /// <summary>The restart interval in MCUs (<c>-restart NB</c>), 0 to 65535, or 0 for none.</summary>
    internal int RestartBlocks { get; init; }

    /// <summary>The forward DCT (<c>-dct</c>): the accurate integer one by default.</summary>
    internal JpegEncoderDct Dct { get; init; }
}
