// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/vp8l_enc.c, and the lossless parts of webp_enc.c, config_enc.c,
// syntax_enc.c, picture_csp_enc.c and picture_tools_enc.c), modified by Hot Chai Games LLC: written in C#,
// its trials of transforms run in parallel when threads are allowed (they're independent, so the
// result is the same). libwebp carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

/// <summary>
/// The settings of a lossless WebP encode: libwebp's WebPConfig fields that its lossless encoder
/// reads, with WebPConfigInit's defaults.
/// </summary>
internal readonly struct Vp8LEncoderOptions
{
    public Vp8LEncoderOptions()
    {
    }

    /// <summary>Effort spent on compression, 0 to 100 (cwebp's -q).</summary>
    public float Quality { get; init; } = 75;

    /// <summary>Speed against size, 0 (fast) to 6 (small) (cwebp's -m).</summary>
    public int Method { get; init; } = 4;

    /// <summary>Whether transparent pixels keep their colors (cwebp's -exact); else they become 0.</summary>
    public bool Exact { get; init; }

    /// <summary>Near-lossless preprocessing, 0 (most) to 100 (none) (cwebp's -near_lossless).</summary>
    public int NearLossless { get; init; } = 100;

    /// <summary>Whether trials may run on other threads (cwebp's -mt). The bytes are the same either way.</summary>
    public bool UseThreads { get; init; }

    /// <summary>The settings of a lossless preset, 0 to 9 (cwebp's -z: WebPConfigLosslessPreset).</summary>
    public static Vp8LEncoderOptions Preset(
        int level)
    {
        ReadOnlySpan<byte> methods = [0, 1, 2, 3, 3, 4, 4, 4, 5, 6];
        ReadOnlySpan<byte> qualities = [0, 20, 25, 30, 50, 50, 75, 90, 90, 100];
        if ((uint)level > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "A lossless preset is 0 to 9.");
        }

        return new Vp8LEncoderOptions { Method = methods[level], Quality = qualities[level] };
    }
}

/// <summary>
/// The lossless WebP encoder: RGBA to a VP8L image in a RIFF file, the bytes cwebp writes with the
/// same settings.
/// </summary>
internal static partial class Vp8LEncoder
{
    private const int MaxHuffImageSize = 2600;
    private const int MinHuffmanBits = 2;
    private const int NumHuffmanBits = 3;
    private const int MaxHuffmanBits = MinHuffmanBits + (1 << NumHuffmanBits) - 1;
    private const int MaxPredictorImageSize = 1 << 14;

    private const int EntropyDirect = 0;
    private const int EntropySpatial = 1;
    private const int EntropySubGreen = 2;
    private const int EntropySpatialSubGreen = 3;
    private const int EntropyPalette = 4;
    private const int EntropyPaletteAndSpatial = 5;
    private const int NumEntropyIx = 6;

    private const int ContentNone = 0;
    private const int ContentArgb = 1;
    private const int ContentNearLossless = 2;
    private const int ContentPalette = 3;

    private const int PredictorTransform = 0;
    private const int CrossColorTransform = 1;
    private const int SubtractGreenTransform = 2;
    private const int ColorIndexingTransform = 3;

    /// <summary>
    /// Encodes the pixels (width x height RGBA, 4 bytes each) as a lossless WebP file.
    /// </summary>
    public static void Encode(
        IBufferWriter<byte> writer,
        int width,
        int height,
        ReadOnlySpan<byte> rgba,
        Vp8LEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        CheckOptions(options);
        if ((width < 1) || (width > 16383) || (height < 1) || (height > 16383))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A WebP image is 1 to 16383 pixels on each side.");
        }

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"A {width} x {height} image is {width * height * 4} bytes, but {rgba.Length} were given.", nameof(rgba));
        }

        // ARGB words, as libwebp's picture holds them (WebPPictureImportRGBA): RGBA bytes read as
        // little-endian words with red and blue swapped; unless exact, a transparent pixel is 0
        // (WebPReplaceTransparentPixels(pic, 0)). Whether any pixel isn't opaque, too.
        var argb = GC.AllocateUninitializedArray<uint>(width * height);
        var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba);
        uint alphaAnd = 0xffffffff;
        bool exact = options.Exact;
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = source[i];
            uint pixel = (p & 0xff00ff00u) | ((p >> 16) & 0xff) | ((p & 0xff) << 16);
            if (!exact && ((pixel >> 24) == 0))
            {
                pixel = 0;
            }

            alphaAnd &= pixel;
            argb[i] = pixel;
        }

        bool hasAlpha = (alphaAnd >> 24) != 0xff;

        var bw = new BitWriter(width * height * 2);
        bw.PutBits((uint)(width - 1), 14);
        bw.PutBits((uint)(height - 1), 14);
        bw.PutBits(hasAlpha ? 1u : 0u, 1);
        bw.PutBits(0, 3);
        EncodeStream(options, width, height, argb, bw);
        var data = bw.Finish();

        // RIFF, VP8L and its signature byte, the bits, a byte of padding to an even size
        int vp8lSize = 1 + data.Length;
        int pad = vp8lSize & 1;
        int riffSize = 4 + 8 + vp8lSize + pad;
        var header = writer.GetSpan(21);
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)riffSize);
        "WEBPVP8L"u8.CopyTo(header.Slice(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16), (uint)vp8lSize);
        header[20] = 0x2f;
        writer.Advance(21);
        writer.Write(data);
        if (pad != 0)
        {
            writer.GetSpan(1)[0] = 0;
            writer.Advance(1);
        }
    }

    /// <summary>
    /// The image stream alone (VP8LEncodeStream), with no size header: what a lossy image's alpha
    /// holds when it's stored losslessly (ALPH method 1). The pixels are ARGB words.
    /// </summary>
    public static byte[] EncodeImageStream(
        int width,
        int height,
        ReadOnlySpan<uint> argb,
        Vp8LEncoderOptions options)
    {
        CheckOptions(options);
        var bw = new BitWriter(width * height);
        EncodeStream(options, width, height, argb.Slice(0, width * height).ToArray(), bw);
        return bw.Finish().ToArray();
    }

    private static void CheckOptions(
        Vp8LEncoderOptions options)
    {
        if (!(options.Quality >= 0) || (options.Quality > 100) || ((uint)options.Method > 6) || ((uint)options.NearLossless > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Quality is 0 to 100, method 0 to 6 and near lossless 0 to 100.");
        }
    }

    //------------------------------------------------------------------------------
    // Analysis

    private static ulong AnalyzeEntropy(
        ReadOnlySpan<uint> argb,
        int width,
        int height,
        bool usePalette,
        int paletteSize,
        int transformBits,
        out int minEntropyIx,
        out bool redAndBlueAlwaysZero)
    {
        if (usePalette && (paletteSize <= 16))
        {
            // Small palettes pack 2, 4 or 8 pixels together, which beats every other transform
            minEntropyIx = EntropyPalette;
            redAndBlueAlwaysZero = true;
            return 0;
        }

        // The histograms, 256 each: alpha, its prediction, green, ..., the palette's hash
        const int HistoAlpha = 0, HistoAlphaPred = 1, HistoGreen = 2, HistoGreenPred = 3, HistoRed = 4, HistoRedPred = 5;
        const int HistoBlue = 6, HistoBluePred = 7, HistoRedSubGreen = 8, HistoRedPredSubGreen = 9;
        const int HistoBlueSubGreen = 10, HistoBluePredSubGreen = 11, HistoPalette = 12, HistoTotal = 13;
        var histo = new uint[HistoTotal * 256];
        ref uint h = ref MemoryMarshal.GetArrayDataReference(histo);
        ref uint pixels = ref MemoryMarshal.GetReference(argb.Slice(0, width * height));
        uint pixPrevious = pixels;
        for (int y = 0; y < height; y++)
        {
            ref uint row = ref Unsafe.Add(ref pixels, (nint)y * width);
            ref uint previousRow = ref (y > 0) ? ref Unsafe.Subtract(ref row, width) : ref row;
            for (nint x = 0; x < width; x++)
            {
                uint pix = Unsafe.Add(ref row, x);
                uint pixDiff = SubPixels(pix, pixPrevious);
                pixPrevious = pix;
                if ((pixDiff == 0) || ((y > 0) && (pix == Unsafe.Add(ref previousRow, x))))
                {
                    continue;
                }

                Unsafe.Add(ref h, (HistoAlpha * 256) + (nint)(pix >> 24))++;
                Unsafe.Add(ref h, (HistoRed * 256) + (nint)((pix >> 16) & 0xff))++;
                Unsafe.Add(ref h, (HistoGreen * 256) + (nint)((pix >> 8) & 0xff))++;
                Unsafe.Add(ref h, (HistoBlue * 256) + (nint)(pix & 0xff))++;
                Unsafe.Add(ref h, (HistoAlphaPred * 256) + (nint)(pixDiff >> 24))++;
                Unsafe.Add(ref h, (HistoRedPred * 256) + (nint)((pixDiff >> 16) & 0xff))++;
                Unsafe.Add(ref h, (HistoGreenPred * 256) + (nint)((pixDiff >> 8) & 0xff))++;
                Unsafe.Add(ref h, (HistoBluePred * 256) + (nint)(pixDiff & 0xff))++;
                uint green = pix >> 8;
                Unsafe.Add(ref h, (HistoRedSubGreen * 256) + (nint)(((pix >> 16) - green) & 0xff))++;
                Unsafe.Add(ref h, (HistoBlueSubGreen * 256) + (nint)((pix - green) & 0xff))++;
                green = pixDiff >> 8;
                Unsafe.Add(ref h, (HistoRedPredSubGreen * 256) + (nint)(((pixDiff >> 16) - green) & 0xff))++;
                Unsafe.Add(ref h, (HistoBluePredSubGreen * 256) + (nint)((pixDiff - green) & 0xff))++;
                uint hash = (uint)(((((ulong)pix + (pix >> 19)) * 0x39c5fba7ul) & 0xffffffffu) >> 24);
                Unsafe.Add(ref h, (HistoPalette * 256) + (nint)hash)++;
            }
        }

        // A zero in each predicted histogram, which the pixDiff test removes too well
        histo[(HistoRedPredSubGreen * 256) + 0]++;
        histo[(HistoBluePredSubGreen * 256) + 0]++;
        histo[(HistoRedPred * 256) + 0]++;
        histo[(HistoGreenPred * 256) + 0]++;
        histo[(HistoBluePred * 256) + 0]++;
        histo[(HistoAlphaPred * 256) + 0]++;

        Span<ulong> entropyComp = stackalloc ulong[HistoTotal];
        for (int j = 0; j < HistoTotal; j++)
        {
            entropyComp[j] = BitsEntropy(histo.AsSpan(j * 256, 256));
        }

        Span<ulong> entropy = stackalloc ulong[NumEntropyIx];
        entropy[EntropyDirect] = entropyComp[HistoAlpha] + entropyComp[HistoRed] + entropyComp[HistoGreen] + entropyComp[HistoBlue];
        entropy[EntropySpatial] = entropyComp[HistoAlphaPred] + entropyComp[HistoRedPred] + entropyComp[HistoGreenPred] + entropyComp[HistoBluePred];
        entropy[EntropySubGreen] = entropyComp[HistoAlpha] + entropyComp[HistoRedSubGreen] + entropyComp[HistoGreen] + entropyComp[HistoBlueSubGreen];
        entropy[EntropySpatialSubGreen] = entropyComp[HistoAlphaPred] + entropyComp[HistoRedPredSubGreen] + entropyComp[HistoGreenPred] + entropyComp[HistoBluePredSubGreen];
        entropy[EntropyPalette] = entropyComp[HistoPalette];

        // The transforms' own cost: 14 spatial predictors, 24 for the color transform, and about 8
        // bits a palette entry
        ulong tiles = (ulong)SubSampleSize(width, transformBits) * (ulong)SubSampleSize(height, transformBits);
        entropy[EntropySpatial] += tiles * FastLog2(14);
        entropy[EntropySpatialSubGreen] += tiles * FastLog2(24);
        entropy[EntropyPalette] += ((ulong)paletteSize * 8) << Log2PrecisionBits;

        int lastModeToAnalyze = usePalette ? EntropyPalette : EntropySpatialSubGreen;
        minEntropyIx = EntropyDirect;
        for (int k = EntropyDirect + 1; k <= lastModeToAnalyze; k++)
        {
            if (entropy[minEntropyIx] > entropy[k])
            {
                minEntropyIx = k;
            }
        }

        // Whether the chosen mode's red and blue are all zero, so the cross color transform can go
        ReadOnlySpan<byte> pairs = [HistoRed, HistoBlue, HistoRedPred, HistoBluePred, HistoRedSubGreen, HistoBlueSubGreen, HistoRedPredSubGreen, HistoBluePredSubGreen, HistoRed, HistoBlue];
        var redHisto = histo.AsSpan(pairs[minEntropyIx * 2] * 256, 256);
        var blueHisto = histo.AsSpan(pairs[(minEntropyIx * 2) + 1] * 256, 256);
        redAndBlueAlwaysZero = true;
        for (int i = 1; i < 256; i++)
        {
            if ((redHisto[i] | blueHisto[i]) != 0)
            {
                redAndBlueAlwaysZero = false;
                break;
            }
        }

        return 0;
    }

    private static int ClampBits(
        int width,
        int height,
        int bits,
        int minBits,
        int maxBits,
        int imageSizeMax)
    {
        bits = (bits < minBits) ? minBits : (bits > maxBits) ? maxBits : bits;
        int imageSize = SubSampleSize(width, bits) * SubSampleSize(height, bits);
        while ((bits < maxBits) && (imageSize > imageSizeMax))
        {
            ++bits;
            imageSize = SubSampleSize(width, bits) * SubSampleSize(height, bits);
        }

        // Not so few that the image is one tile
        while ((bits > minBits) && (imageSize == 1))
        {
            imageSize = SubSampleSize(width, bits - 1) * SubSampleSize(height, bits - 1);
            if (imageSize != 1)
            {
                break;
            }

            --bits;
        }

        return bits;
    }

    // A set of choices the cruncher tries (CrunchConfig): an entropy mode, a palette sorting, and
    // one or two LZ77s, each with and without a color cache
    private struct CrunchConfig
    {
        public int EntropyIdx;
        public int PaletteSortingType;
        public int SubConfigsSize;
        public int Lz77First;
        public int Lz77Second;
        public bool DoNoCache;
    }

    // The encoder's state (VP8LEncoder)
    private sealed class Encoder
    {
        public required Vp8LEncoderOptions Config;
        public required int Width;
        public required int Height;
        public required uint[] Picture;
        public uint[] Argb = [];
        public int ArgbContent;
        public uint[] ArgbScratch = [];
        public uint[] TransformData = [];
        public long TransformMemSize;
        public int CurrentWidth;
        public int HistoBits;
        public int PredictorTransformBits;
        public int CrossColorTransformBits;
        public int CacheBits;
        public int LastPredictorBits;
        public int LastCrossColorBits;
        public bool UseCrossColor;
        public bool UseSubtractGreen;
        public bool UsePredict;
        public bool UsePalette;
        public int PaletteSize;
        public readonly uint[] Palette = new uint[MaxPaletteSize];
        public readonly uint[] PaletteSorted = new uint[MaxPaletteSize];
        public readonly BackwardRefs[] Refs = [new(), new(), new(), new()];
        public readonly HashChain HashChain = new();
        public readonly Scratch Scratch = new();
    }

    // Scratch memory reused across an encode
    private sealed class Scratch
    {
        private Histogram? histogram;

        public readonly HashChain HashChainBox = new();
        public readonly HashChain HashChainHistogram = new();

        public Histogram GetHistogram(
            int cacheBits)
        {
            if ((this.histogram is null) || (this.histogram.Literal.Length < HistogramNumCodes(cacheBits)))
            {
                this.histogram = new Histogram(cacheBits);
            }

            this.histogram.PaletteCodeBits = cacheBits;
            return this.histogram;
        }
    }

    private static int EncoderAnalyze(
        Encoder enc,
        Span<CrunchConfig> crunchConfigs,
        out bool redAndBlueAlwaysZero)
    {
        int width = enc.Width;
        int height = enc.Height;
        var config = enc.Config;
        int method = config.Method;
        bool lowEffort = method == 0;
        bool doNoCache = false;
        int numCrunchConfigs;
        int numLz77s;
        redAndBlueAlwaysZero = false;

        enc.PaletteSize = GetColorPalette(enc.Picture, enc.PaletteSorted);
        bool usePalette = enc.PaletteSize <= MaxPaletteSize;
        if (!usePalette)
        {
            enc.PaletteSize = 0;
        }

        // Empirical bit sizes
        enc.HistoBits = ClampBits(width, height, (usePalette ? 9 : 7) - method, MinHuffmanBits, MaxHuffmanBits, MaxHuffImageSize);
        int maxTransformBits = (method < 4) ? 6 : (method > 4) ? 4 : 5;
        int transformBits = (enc.HistoBits > maxTransformBits) ? maxTransformBits : enc.HistoBits;
        enc.PredictorTransformBits = transformBits;
        enc.CrossColorTransformBits = transformBits;
        if (lowEffort)
        {
            crunchConfigs[0].EntropyIdx = usePalette ? EntropyPalette : EntropySpatialSubGreen;
            crunchConfigs[0].PaletteSortingType = usePalette ? SortedDefault : UnusedPalette;
            numLz77s = 1;
            numCrunchConfigs = 1;
        }
        else
        {
            // More than one LZ77 on images of few colors
            numLz77s = ((enc.PaletteSize > 0) && (enc.PaletteSize <= 16)) ? 2 : 1;
            AnalyzeEntropy(enc.Picture, width, height, usePalette, enc.PaletteSize, transformBits, out int minEntropyIx, out redAndBlueAlwaysZero);
            if ((method == 6) && (config.Quality == 100))
            {
                // Every transform
                doNoCache = true;
                numCrunchConfigs = 0;
                for (int i = 0; i < NumEntropyIx; i++)
                {
                    if (((i != EntropyPalette) && (i != EntropyPaletteAndSpatial)) || usePalette)
                    {
                        if (usePalette && ((i == EntropyPalette) || (i == EntropyPaletteAndSpatial)))
                        {
                            for (int sorting = 0; sorting < PaletteSortingNum; sorting++)
                            {
                                // The default sorting is left out, as libwebp leaves it out
                                if ((sorting == UnusedPalette) || (sorting == SortedDefault))
                                {
                                    continue;
                                }

                                crunchConfigs[numCrunchConfigs].EntropyIdx = i;
                                crunchConfigs[numCrunchConfigs].PaletteSortingType = sorting;
                                ++numCrunchConfigs;
                            }
                        }
                        else
                        {
                            crunchConfigs[numCrunchConfigs].EntropyIdx = i;
                            crunchConfigs[numCrunchConfigs].PaletteSortingType = UnusedPalette;
                            ++numCrunchConfigs;
                        }
                    }
                }
            }
            else
            {
                // The transform guessed best
                numCrunchConfigs = 1;
                crunchConfigs[0].EntropyIdx = minEntropyIx;
                crunchConfigs[0].PaletteSortingType = usePalette ? MinimizeDelta : UnusedPalette;
                if ((config.Quality >= 75) && (method == 5))
                {
                    doNoCache = true;
                    if (minEntropyIx == EntropyPalette)
                    {
                        numCrunchConfigs = 2;
                        crunchConfigs[1].EntropyIdx = EntropyPaletteAndSpatial;
                        crunchConfigs[1].PaletteSortingType = MinimizeDelta;
                    }
                }
            }
        }

        for (int i = 0; i < numCrunchConfigs; i++)
        {
            crunchConfigs[i].Lz77First = Lz77Standard | Lz77Rle;
            crunchConfigs[i].Lz77Second = Lz77Box;
            crunchConfigs[i].DoNoCache = doNoCache;
            crunchConfigs[i].SubConfigsSize = numLz77s;
        }

        return numCrunchConfigs;
    }

    //------------------------------------------------------------------------------
    // Huffman codes
}
