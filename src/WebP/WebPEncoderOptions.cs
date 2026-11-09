// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Imaging.WebP;

/// <summary>
/// How <see cref="WebPCodec.Encode"/> writes an image: its defaults are cwebp's, and with the same settings
/// the file is byte for byte what cwebp 1.6.0 writes (lossless as <c>-lossless</c>, and so on).
/// </summary>
public sealed class WebPEncoderOptions
{
    /// <summary>
    /// Whether the image is stored losslessly (VP8L, cwebp's <c>-lossless</c>) or lossy (VP8, the
    /// default), with its alpha stored as <see cref="AlphaQuality"/> says.
    /// </summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// 0 to 100 (cwebp's <c>-q</c>): for a lossy image its quality, from smallest to best; for a lossless
    /// one the effort spent making it small, from fastest to smallest.
    /// </summary>
    public float Quality { get; init; } = 75;

    /// <summary>
    /// 0 (fastest) to 6 (slowest, and smallest): how many ways the encoder tries (cwebp's <c>-m</c>).
    /// </summary>
    public int Method { get; init; } = 4;

    /// <summary>
    /// Whether the red, green and blue of fully transparent pixels are kept as they are (cwebp's
    /// <c>-exact</c>) rather than changed to whatever compresses smallest. Images whose channels hold
    /// data need it, as do pixels that are premultiplied again after decoding.
    /// </summary>
    public bool Exact { get; init; }

    /// <summary>
    /// For a lossy image, 0 to 100 (cwebp's <c>-alpha_q</c>): its alpha's quality, where 100 stores it
    /// losslessly. A lossless image's alpha is always lossless.
    /// </summary>
    public int AlphaQuality { get; init; } = 100;

    /// <summary>
    /// For a lossy image, whether its RGB is converted to YUV with the sharper, slower conversion
    /// (cwebp's <c>-sharp_yuv</c>), which keeps edges in saturated colors crisper.
    /// </summary>
    public bool UseSharpYuv { get; init; }

    /// <summary>
    /// For a lossless image, 0 to 100 (cwebp's <c>-near_lossless</c>): 100 is exactly lossless, and less
    /// lets pixels change, a little, to make the file smaller.
    /// </summary>
    public int NearLossless { get; init; } = 100;

    /// <summary>
    /// Whether the encoder uses more than one thread (cwebp's <c>-mt</c>). The file is the same either way.
    /// </summary>
    public bool UseThreads { get; init; }
}
