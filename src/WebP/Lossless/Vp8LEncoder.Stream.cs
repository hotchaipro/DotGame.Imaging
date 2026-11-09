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
using System.Threading.Tasks;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private static void EncodeImageInternal(
        BitWriter bw,
        ReadOnlySpan<uint> argb,
        HashChain hashChain,
        BackwardRefs[] refsArray,
        int width,
        int height,
        int quality,
        bool lowEffort,
        in CrunchConfig config,
        ref int cacheBits,
        int histogramBitsIn,
        Scratch scratch)
    {
        int histogramImageXysize = SubSampleSize(width, histogramBitsIn) * SubSampleSize(height, histogramBitsIn);
        var histogramArgb = new uint[histogramImageXysize];
        var bwInit = bw.Mark;
        var bwBest = new BitWriter(0);
        long bwSizeBest = long.MaxValue;
        var hashChainHistogram = scratch.HashChainHistogram;
        hashChainHistogram.Init(histogramImageXysize);

        HashChainFill(hashChain, quality, argb, width, height, lowEffort);

        // Set by the palette analysis if it's not 0
        int cacheBitsInit = (cacheBits == 0) ? MaxColorCacheBits : cacheBits;
        if ((config.SubConfigsSize > 1) || config.DoNoCache)
        {
            bw.CloneTo(bwBest);
        }

        for (int subConfigsIdx = 0; subConfigsIdx < config.SubConfigsSize; subConfigsIdx++)
        {
            int lz77 = (subConfigsIdx == 0) ? config.Lz77First : config.Lz77Second;
            GetBackwardReferences(width, height, argb, quality, lowEffort, lz77, cacheBitsInit, config.DoNoCache, hashChain, refsArray, 0, out int cacheBitsBest, scratch);
            for (int iCache = 0; iCache < (config.DoNoCache ? 2 : 1); iCache++)
            {
                int cacheBitsTmp = (iCache == 0) ? cacheBitsBest : 0;
                int histogramBits = histogramBitsIn;

                // The case with no cache was studied if the cache was found to be 0
                if ((iCache == 1) && (cacheBitsBest == 0))
                {
                    break;
                }

                bw.Reset(bwInit);

                // The histogram image and its symbols, from the references
                var histogramImage = new HistogramSet(histogramImageXysize, cacheBitsTmp);
                var tmpHisto = new Histogram(cacheBitsTmp);
                GetHistoImageSymbols(width, height, refsArray[iCache], quality, lowEffort, histogramBits, cacheBitsTmp, histogramImage, tmpHisto, histogramArgb);
                var huffmanCodes = GetHuffBitLengthsAndCodes(histogramImage);

                // Color cache
                if (cacheBitsTmp > 0)
                {
                    bw.PutBits(1, 1);
                    bw.PutBits((uint)cacheBitsTmp, 4);
                }
                else
                {
                    bw.PutBits(0, 1);
                }

                // The Huffman image, and the meta Huffman codes
                int histogramImageSize = 0;
                for (int i = 0; i < histogramImageXysize; i++)
                {
                    if (histogramArgb[i] >= histogramImageSize)
                    {
                        histogramImageSize = (int)histogramArgb[i] + 1;
                    }

                    histogramArgb[i] <<= 8;
                }

                bool writeHistogramImage = histogramImageSize > 1;
                bw.PutBits(writeHistogramImage ? 1u : 0u, 1);
                if (writeHistogramImage)
                {
                    histogramBits = OptimizeSampling(histogramArgb, width, height, histogramBitsIn, MaxHuffmanBits);
                    bw.PutBits((uint)(histogramBits - 2), 3);
                    EncodeImageNoHuffman(bw, histogramArgb, hashChainHistogram, refsArray, 2, SubSampleSize(width, histogramBits), SubSampleSize(height, histogramBits), quality, lowEffort, scratch);
                }

                // The Huffman codes
                int maxTokens = 0;
                for (int i = 0; i < 5 * histogramImageSize; i++)
                {
                    maxTokens = Math.Max(maxTokens, huffmanCodes[i].NumSymbols);
                }

                var tokens = new HuffmanTreeToken[maxTokens];
                var huffTree = new HuffmanTree[3 * CodeLengthCodes];
                for (int i = 0; i < 5 * histogramImageSize; i++)
                {
                    StoreHuffmanCode(bw, huffTree, tokens, huffmanCodes[i]);
                    ClearHuffmanTreeIfOnlyOneSymbol(huffmanCodes[i]);
                }

                // The pixels
                StoreImageToBitMask(bw, width, histogramBits, refsArray[iCache], histogramArgb, huffmanCodes);

                // The smallest so far
                if (bw.NumBytes < bwSizeBest)
                {
                    bwSizeBest = bw.NumBytes;
                    cacheBits = cacheBitsTmp;
                    BitWriter.Swap(bw, bwBest);
                }
            }
        }

        BitWriter.Swap(bw, bwBest);
    }

    //------------------------------------------------------------------------------
    // Transforms

    // AllocateTransformBuffer: the buffers for an image of width x height
    private static void AllocateTransformBuffer(
        Encoder enc,
        int width,
        int height)
    {
        long imageSize = (long)width * height;
        long argbScratchSize = enc.UsePredict ? (((width + 1) * 2) + (((width * 2) + 4 - 1) / 4)) : 0;
        long transformDataSize = (enc.UsePredict || enc.UseCrossColor) ? (long)SubSampleSize(width, MinTransformBits) * SubSampleSize(height, MinTransformBits) : 0;
        const long MaxAlignmentInWords = (31 + 4 - 1) / 4;
        long memSize = imageSize + MaxAlignmentInWords + argbScratchSize + MaxAlignmentInWords + transformDataSize;
        if ((enc.TransformMemSize == 0) || (memSize > enc.TransformMemSize))
        {
            enc.TransformMemSize = memSize;
            enc.ArgbContent = ContentNone;
            enc.Argb = new uint[Math.Max(imageSize, (long)enc.Width * enc.Height)];
            enc.ArgbScratch = new uint[(((enc.Width + 1) * 2) + (((enc.Width * 2) + 3) / 4)) + 8];
            enc.TransformData = new uint[SubSampleSize(enc.Width, MinTransformBits) * SubSampleSize(enc.Height, MinTransformBits)];
        }

        enc.CurrentWidth = width;
    }

    private static void MakeInputImageCopy(
        Encoder enc)
    {
        AllocateTransformBuffer(enc, enc.Width, enc.Height);
        if (enc.ArgbContent == ContentArgb)
        {
            return;
        }

        enc.Picture.AsSpan().CopyTo(enc.Argb);
        enc.ArgbContent = ContentArgb;
    }

    private static void MapImageFromPalette(
        Encoder enc)
    {
        int width = enc.Width;
        int height = enc.Height;
        int paletteSize = enc.PaletteSize;
        int xbits = (paletteSize <= 4) ? ((paletteSize <= 2) ? 3 : 2) : ((paletteSize <= 16) ? 1 : 0);
        AllocateTransformBuffer(enc, SubSampleSize(width, xbits), height);
        ApplyPalette(enc.Picture, width, enc.Argb, enc.CurrentWidth, enc.Palette, paletteSize, width, height, xbits);
        enc.ArgbContent = ContentPalette;
    }

    private static void EncodePalette(
        BitWriter bw,
        bool lowEffort,
        Encoder enc)
    {
        Span<uint> tmpPalette = stackalloc uint[MaxPaletteSize];
        int paletteSize = enc.PaletteSize;
        var palette = enc.Palette;

        // A last entry of 0 needn't be stored when no pixels are packed (more than 16 colors)
        int encodedPaletteSize = ((palette[paletteSize - 1] == 0) && (paletteSize > 17)) ? paletteSize - 1 : paletteSize;
        bw.PutBits(1, 1);
        bw.PutBits(ColorIndexingTransform, 2);
        bw.PutBits((uint)(encodedPaletteSize - 1), 8);
        for (int i = encodedPaletteSize - 1; i >= 1; --i)
        {
            tmpPalette[i] = SubPixels(palette[i], palette[i - 1]);
        }

        tmpPalette[0] = palette[0];
        EncodeImageNoHuffman(bw, tmpPalette.Slice(0, encodedPaletteSize).ToArray(), enc.HashChain, enc.Refs, 0, encodedPaletteSize, 1, 20, lowEffort, enc.Scratch);
    }

    private static void ApplyPredictFilter(
        Encoder enc,
        int width,
        int height,
        int quality,
        bool lowEffort,
        bool usedSubtractGreen,
        BitWriter bw)
    {
        int nearLosslessStrength = enc.UsePalette ? 100 : enc.Config.NearLossless;
        int maxBits = ClampBits(width, height, enc.PredictorTransformBits, MinTransformBits, MaxTransformBits, MaxPredictorImageSize);
        int minBits = ClampBits(width, height, maxBits - (2 * ((enc.Config.Method > 4) ? enc.Config.Method - 4 : 0)), MinTransformBits, MaxTransformBits, MaxPredictorImageSize);
        int bestBits = ResidualImage(width, height, minBits, maxBits, lowEffort, enc.Argb.AsSpan(0, width * height), enc.ArgbScratch, enc.TransformData, nearLosslessStrength, enc.Config.Exact, usedSubtractGreen);
        bw.PutBits(1, 1);
        bw.PutBits(PredictorTransform, 2);
        bw.PutBits((uint)(bestBits - MinTransformBits), NumTransformBits);
        enc.LastPredictorBits = bestBits;
        EncodeImageNoHuffman(bw, enc.TransformData, enc.HashChain, enc.Refs, 0, SubSampleSize(width, bestBits), SubSampleSize(height, bestBits), quality, lowEffort, enc.Scratch);
    }

    private static void ApplyCrossColorFilter(
        Encoder enc,
        int width,
        int height,
        int quality,
        bool lowEffort,
        BitWriter bw)
    {
        int bestBits = ColorSpaceTransform(width, height, enc.CrossColorTransformBits, quality, enc.Argb.AsSpan(0, width * height), enc.TransformData);
        bw.PutBits(1, 1);
        bw.PutBits(CrossColorTransform, 2);
        bw.PutBits((uint)(bestBits - MinTransformBits), NumTransformBits);
        enc.LastCrossColorBits = bestBits;
        EncodeImageNoHuffman(bw, enc.TransformData, enc.HashChain, enc.Refs, 0, SubSampleSize(width, bestBits), SubSampleSize(height, bestBits), quality, lowEffort, enc.Scratch);
    }

    //------------------------------------------------------------------------------
    // The stream

    // EncodeStreamHook: the configurations tried in order, the smallest kept in bw
    private static void EncodeConfigs(
        Encoder enc,
        BitWriter bw,
        ReadOnlySpan<CrunchConfig> crunchConfigs,
        bool redAndBlueAlwaysZero)
    {
        var config = enc.Config;
        int quality = (int)config.Quality;
        bool lowEffort = config.Method == 0;
        int width = enc.Width;
        int height = enc.Height;
        long bestSize = long.MaxValue;
        var bwInit = bw.Mark;
        var bwBest = new BitWriter(0);
        if (crunchConfigs.Length > 1)
        {
            bw.CloneTo(bwBest);
        }

        for (int idx = 0; idx < crunchConfigs.Length; idx++)
        {
            int entropyIdx = crunchConfigs[idx].EntropyIdx;
            enc.UsePalette = (entropyIdx == EntropyPalette) || (entropyIdx == EntropyPaletteAndSpatial);
            enc.UseSubtractGreen = (entropyIdx == EntropySubGreen) || (entropyIdx == EntropySpatialSubGreen);
            enc.UsePredict = (entropyIdx == EntropySpatial) || (entropyIdx == EntropySpatialSubGreen) || (entropyIdx == EntropyPaletteAndSpatial);

            // A palette's red and blue are 0, so there's no cross color to try
            enc.UseCrossColor = !(lowEffort || enc.UsePalette) && !redAndBlueAlwaysZero && enc.UsePredict;
            enc.CacheBits = 0;
            enc.LastPredictorBits = 0;
            enc.LastCrossColorBits = 0;
            enc.Refs[0].Clear();
            enc.Refs[1].Clear();

            // Near lossless preprocessing
            bool useNearLossless = (config.NearLossless < 100) && !enc.UsePalette && !enc.UsePredict;
            if (useNearLossless)
            {
                AllocateTransformBuffer(enc, width, height);
                if (enc.ArgbContent != ContentNearLossless)
                {
                    ApplyNearLossless(width, height, enc.Picture, config.NearLossless, enc.Argb);
                }

                enc.ArgbContent = ContentNearLossless;
            }
            else
            {
                enc.ArgbContent = ContentNone;
            }

            // The palette
            if (enc.UsePalette)
            {
                PaletteSort(crunchConfigs[idx].PaletteSortingType, enc.Picture, width, height, enc.PaletteSorted, enc.PaletteSize, enc.Palette);
                EncodePalette(bw, lowEffort, enc);
                MapImageFromPalette(enc);

                // No bigger a color cache than there are colors
                if (enc.PaletteSize < (1 << MaxColorCacheBits))
                {
                    enc.CacheBits = System.Numerics.BitOperations.Log2((uint)enc.PaletteSize) + 1;
                }
            }

            if ((enc.ArgbContent != ContentNearLossless) && (enc.ArgbContent != ContentPalette))
            {
                MakeInputImageCopy(enc);
            }

            // The transforms and their data
            if (enc.UseSubtractGreen)
            {
                bw.PutBits(1, 1);
                bw.PutBits(SubtractGreenTransform, 2);
                SubtractGreenFromBlueAndRed(enc.Argb.AsSpan(0, enc.CurrentWidth * height));
            }

            if (enc.UsePredict)
            {
                ApplyPredictFilter(enc, enc.CurrentWidth, height, quality, lowEffort, enc.UseSubtractGreen, bw);
            }

            if (enc.UseCrossColor)
            {
                ApplyCrossColorFilter(enc, enc.CurrentWidth, height, quality, lowEffort, bw);
            }

            bw.PutBits(0, 1);

            // The transformed image
            EncodeImageInternal(bw, enc.Argb.AsSpan(0, enc.CurrentWidth * height), enc.HashChain, enc.Refs, enc.CurrentWidth, height, quality, lowEffort, crunchConfigs[idx], ref enc.CacheBits, enc.HistoBits, enc.Scratch);

            if (bw.NumBytes < bestSize)
            {
                bestSize = bw.NumBytes;
                BitWriter.Swap(bw, bwBest);
            }

            if (crunchConfigs.Length > 1)
            {
                bw.Reset(bwInit);
            }
        }

        BitWriter.Swap(bwBest, bw);
    }

    private static Encoder NewEncoder(
        Vp8LEncoderOptions options,
        int width,
        int height,
        uint[] picture)
    {
        var enc = new Encoder { Config = options, Width = width, Height = height, Picture = picture };
        enc.HashChain.Init(width * height);
        return enc;
    }

    // VP8LEncodeStream
    private static void EncodeStream(
        Vp8LEncoderOptions options,
        int width,
        int height,
        uint[] picture,
        BitWriter bw)
    {
        var encMain = NewEncoder(options, width, height, picture);
        Span<CrunchConfig> crunchConfigs = stackalloc CrunchConfig[NumEntropyIx + (2 * PaletteSortingNum)];
        int numCrunchConfigs = EncoderAnalyze(encMain, crunchConfigs, out bool redAndBlueAlwaysZero);
        var configs = crunchConfigs.Slice(0, numCrunchConfigs).ToArray();
        if (!options.UseThreads || (configs.Length == 1))
        {
            EncodeConfigs(encMain, bw, configs, redAndBlueAlwaysZero);
            return;
        }

        // As libwebp's -mt: the last half of the configurations (rounded down) on a second encoder with
        // the first's analysis, each half in order on its encoder (a configuration starts from the
        // state the one before it left), and the second's file taken only if it's smaller
        int sideCount = configs.Length / 2;
        int mainCount = configs.Length - sideCount;
        var encSide = NewEncoder(options, width, height, picture);
        CopyAnalysis(encMain, encSide);
        var bwSide = new BitWriter(0);
        bw.CloneTo(bwSide);
        var side = Task.Run(() => EncodeConfigs(encSide, bwSide, configs.AsSpan(mainCount, sideCount), redAndBlueAlwaysZero));
        EncodeConfigs(encMain, bw, configs.AsSpan(0, mainCount), redAndBlueAlwaysZero);
        side.GetAwaiter().GetResult();
        if (bwSide.NumBytes < bw.NumBytes)
        {
            BitWriter.Swap(bw, bwSide);
        }
    }

    private static void CopyAnalysis(
        Encoder from,
        Encoder to)
    {
        to.HistoBits = from.HistoBits;
        to.PredictorTransformBits = from.PredictorTransformBits;
        to.CrossColorTransformBits = from.CrossColorTransformBits;
        to.PaletteSize = from.PaletteSize;
        from.Palette.AsSpan().CopyTo(to.Palette);
        from.PaletteSorted.AsSpan().CopyTo(to.PaletteSorted);
    }
}
