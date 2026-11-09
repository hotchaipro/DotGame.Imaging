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

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    private static HuffmanTreeCode[] GetHuffBitLengthsAndCodes(
        HistogramSet histogramImage)
    {
        int histogramImageSize = histogramImage.Size;
        var codes = new HuffmanTreeCode[5 * histogramImageSize];
        int maxNumSymbols = 0;
        for (int i = 0; i < histogramImageSize; i++)
        {
            var histo = histogramImage.Histograms[i];
            for (int k = 0; k < 5; k++)
            {
                int numSymbols = (k == 0) ? HistogramNumCodes(histo.PaletteCodeBits) : (k == 4) ? NumDistanceCodes : 256;
                var code = new HuffmanTreeCode { NumSymbols = numSymbols, CodeLengths = new byte[numSymbols], Codes = new ushort[numSymbols] };
                codes[(5 * i) + k] = code;
                maxNumSymbols = Math.Max(maxNumSymbols, numSymbols);
            }
        }

        var bufferRle = new byte[maxNumSymbols];
        var huffTree = new HuffmanTree[3 * maxNumSymbols];
        for (int i = 0; i < histogramImageSize; i++)
        {
            var histo = histogramImage.Histograms[i];

            CreateHuffmanTree(histo.Literal.AsSpan(0, codes[5 * i].NumSymbols), 15, bufferRle, huffTree, codes[(5 * i) + 0]);
            CreateHuffmanTree(histo.Red, 15, bufferRle, huffTree, codes[(5 * i) + 1]);
            CreateHuffmanTree(histo.Blue, 15, bufferRle, huffTree, codes[(5 * i) + 2]);
            CreateHuffmanTree(histo.Alpha, 15, bufferRle, huffTree, codes[(5 * i) + 3]);
            CreateHuffmanTree(histo.Distance, 15, bufferRle, huffTree, codes[(5 * i) + 4]);
        }

        return codes;
    }

    private static void StoreHuffmanTreeOfHuffmanTreeToBitMask(
        BitWriter bw,
        ReadOnlySpan<byte> codeLengthBitDepth)
    {
        ReadOnlySpan<byte> storageOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        int codesToStore = CodeLengthCodes;
        for (; codesToStore > 4; --codesToStore)
        {
            if (codeLengthBitDepth[storageOrder[codesToStore - 1]] != 0)
            {
                break;
            }
        }

        bw.PutBits((uint)(codesToStore - 4), 4);
        for (int i = 0; i < codesToStore; i++)
        {
            bw.PutBits(codeLengthBitDepth[storageOrder[i]], 3);
        }
    }

    private static void ClearHuffmanTreeIfOnlyOneSymbol(
        HuffmanTreeCode huffmanCode)
    {
        int count = 0;
        for (int k = 0; k < huffmanCode.NumSymbols; k++)
        {
            if (huffmanCode.CodeLengths[k] != 0)
            {
                ++count;
                if (count > 1)
                {
                    return;
                }
            }
        }

        Array.Clear(huffmanCode.CodeLengths, 0, huffmanCode.NumSymbols);
        Array.Clear(huffmanCode.Codes, 0, huffmanCode.NumSymbols);
    }

    private static void StoreFullHuffmanCode(
        BitWriter bw,
        Span<HuffmanTree> huffTree,
        Span<HuffmanTreeToken> tokens,
        HuffmanTreeCode tree)
    {
        var huffmanCode = new HuffmanTreeCode { NumSymbols = CodeLengthCodes, CodeLengths = new byte[CodeLengthCodes], Codes = new ushort[CodeLengthCodes] };
        bw.PutBits(0, 1);
        int numTokens = CreateCompressedHuffmanTree(tree, tokens);
        {
            Span<uint> histogram = stackalloc uint[CodeLengthCodes];
            Span<byte> bufferRle = stackalloc byte[CodeLengthCodes];
            histogram.Clear();
            for (int i = 0; i < numTokens; i++)
            {
                histogram[tokens[i].Code]++;
            }

            CreateHuffmanTree(histogram, 7, bufferRle, huffTree, huffmanCode);
        }

        StoreHuffmanTreeOfHuffmanTreeToBitMask(bw, huffmanCode.CodeLengths);
        ClearHuffmanTreeIfOnlyOneSymbol(huffmanCode);
        {
            int trailingZeroBits = 0;
            int trimmedLength = numTokens;
            int i = numTokens;
            while (i-- > 0)
            {
                int ix = tokens[i].Code;
                if ((ix == 0) || (ix == 17) || (ix == 18))
                {
                    --trimmedLength;
                    trailingZeroBits += huffmanCode.CodeLengths[ix];
                    if (ix == 17)
                    {
                        trailingZeroBits += 3;
                    }
                    else if (ix == 18)
                    {
                        trailingZeroBits += 7;
                    }
                }
                else
                {
                    break;
                }
            }

            bool writeTrimmedLength = (trimmedLength > 1) && (trailingZeroBits > 12);
            int length = writeTrimmedLength ? trimmedLength : numTokens;
            bw.PutBits(writeTrimmedLength ? 1u : 0u, 1);
            if (writeTrimmedLength)
            {
                if (trimmedLength == 2)
                {
                    bw.PutBits(0, 3 + 2);
                }
                else
                {
                    int nbits = System.Numerics.BitOperations.Log2((uint)(trimmedLength - 2));
                    int nbitpairs = (nbits / 2) + 1;
                    bw.PutBits((uint)(nbitpairs - 1), 3);
                    bw.PutBits((uint)(trimmedLength - 2), nbitpairs * 2);
                }
            }

            // StoreHuffmanTreeToBitMask
            for (int t = 0; t < length; t++)
            {
                int ix = tokens[t].Code;
                int extraBits = tokens[t].ExtraBits;
                bw.PutBits(huffmanCode.Codes[ix], huffmanCode.CodeLengths[ix]);
                switch (ix)
                {
                    case 16:
                        bw.PutBits((uint)extraBits, 2);
                        break;
                    case 17:
                        bw.PutBits((uint)extraBits, 3);
                        break;
                    case 18:
                        bw.PutBits((uint)extraBits, 7);
                        break;
                }
            }
        }
    }

    private static void StoreHuffmanCode(
        BitWriter bw,
        Span<HuffmanTree> huffTree,
        Span<HuffmanTreeToken> tokens,
        HuffmanTreeCode huffmanCode)
    {
        int count = 0;
        int symbol0 = 0, symbol1 = 0;
        const int MaxSymbol = 1 << 8;
        for (int i = 0; (i < huffmanCode.NumSymbols) && (count < 3); i++)
        {
            if (huffmanCode.CodeLengths[i] != 0)
            {
                if (count == 0)
                {
                    symbol0 = i;
                }
                else if (count == 1)
                {
                    symbol1 = i;
                }

                ++count;
            }
        }

        if (count == 0)
        {
            // The smallest tree, for nothing: a small tree of one 1-bit symbol, 0
            bw.PutBits(0x01, 4);
        }
        else if ((count <= 2) && (symbol0 < MaxSymbol) && (symbol1 < MaxSymbol))
        {
            bw.PutBits(1, 1);
            bw.PutBits((uint)(count - 1), 1);
            if (symbol0 <= 1)
            {
                bw.PutBits(0, 1);
                bw.PutBits((uint)symbol0, 1);
            }
            else
            {
                bw.PutBits(1, 1);
                bw.PutBits((uint)symbol0, 8);
            }

            if (count == 2)
            {
                bw.PutBits((uint)symbol1, 8);
            }
        }
        else
        {
            StoreFullHuffmanCode(bw, huffTree, tokens, huffmanCode);
        }
    }

    private static void StoreImageToBitMask(
        BitWriter bw,
        int width,
        int histoBits,
        BackwardRefs refs,
        ReadOnlySpan<uint> histogramSymbols,
        HuffmanTreeCode[] huffmanCodes)
    {
        int histoXsize = (histoBits != 0) ? SubSampleSize(width, histoBits) : 1;
        int tileMask = (histoBits == 0) ? 0 : -(1 << histoBits);
        int x = 0;
        int y = 0;
        int tileX = x & tileMask;
        int tileY = y & tileMask;
        int histogramIx = (int)((histogramSymbols[0] >> 8) & 0xffff);
        var green = huffmanCodes[5 * histogramIx];
        var red = huffmanCodes[(5 * histogramIx) + 1];
        var blue = huffmanCodes[(5 * histogramIx) + 2];
        var alpha = huffmanCodes[(5 * histogramIx) + 3];
        var distance = huffmanCodes[(5 * histogramIx) + 4];
        var sink = new BitWriter.Sink(bw);
        foreach (ref readonly var v in refs.Span)
        {
            if ((tileX != (x & tileMask)) || (tileY != (y & tileMask)))
            {
                tileX = x & tileMask;
                tileY = y & tileMask;
                histogramIx = (int)((histogramSymbols[((y >> histoBits) * histoXsize) + (x >> histoBits)] >> 8) & 0xffff);
                green = huffmanCodes[5 * histogramIx];
                red = huffmanCodes[(5 * histogramIx) + 1];
                blue = huffmanCodes[(5 * histogramIx) + 2];
                alpha = huffmanCodes[(5 * histogramIx) + 3];
                distance = huffmanCodes[(5 * histogramIx) + 4];
            }

            if (v.Mode == ModeLiteral)
            {
                // Green, red, blue, alpha
                uint argb = v.ArgbOrDistance;
                int g = (int)((argb >> 8) & 0xff);
                sink.Put(green.Codes[g], green.CodeLengths[g]);
                int r = (int)((argb >> 16) & 0xff);
                sink.Put(red.Codes[r], red.CodeLengths[r]);
                int b = (int)(argb & 0xff);
                sink.Put(blue.Codes[b], blue.CodeLengths[b]);
                int a = (int)(argb >> 24);
                sink.Put(alpha.Codes[a], alpha.CodeLengths[a]);
            }
            else if (v.Mode == ModeCacheIndex)
            {
                int literalIx = NumLiteralCodes + NumLengthCodes + (int)v.ArgbOrDistance;
                sink.Put(green.Codes[literalIx], green.CodeLengths[literalIx]);
            }
            else
            {
                PrefixEncode(v.Length, out int code, out int numBits, out int bits);
                int depth = green.CodeLengths[256 + code];
                sink.Put(((uint)bits << depth) | green.Codes[256 + code], depth + numBits);

                // The distance's extra bits apart: they and its code could be more than 32 bits
                PrefixEncode((int)v.ArgbOrDistance, out code, out numBits, out bits);
                sink.Put(distance.Codes[code], distance.CodeLengths[code]);
                sink.Put((uint)bits, numBits);
            }

            x += v.Length;
            while (x >= width)
            {
                x -= width;
                ++y;
            }
        }

        sink.End();
    }

    // EncodeImageInternal with no color cache and a single Huffman tree set: a transform's data
    private static void EncodeImageNoHuffman(
        BitWriter bw,
        ReadOnlySpan<uint> argb,
        HashChain hashChain,
        BackwardRefs[] refsArray,
        int refsOffset,
        int width,
        int height,
        int quality,
        bool lowEffort,
        Scratch scratch)
    {
        HashChainFill(hashChain, quality, argb, width, height, lowEffort);
        GetBackwardReferences(width, height, argb, quality, false, Lz77Standard | Lz77Rle, 0, false, hashChain, refsArray, refsOffset, out int cacheBits, scratch);
        var refs = refsArray[refsOffset];
        var histogramImage = new HistogramSet(1, cacheBits);
        histogramImage.Clear();
        HistogramStoreRefs(refs, 0, histogramImage.Histograms[0]);
        var huffmanCodes = GetHuffBitLengthsAndCodes(histogramImage);

        // No color cache, no Huffman image
        bw.PutBits(0, 1);
        int maxTokens = 0;
        for (int i = 0; i < 5; i++)
        {
            maxTokens = Math.Max(maxTokens, huffmanCodes[i].NumSymbols);
        }

        var tokens = new HuffmanTreeToken[maxTokens];
        var huffTree = new HuffmanTree[3 * CodeLengthCodes];
        for (int i = 0; i < 5; i++)
        {
            StoreHuffmanCode(bw, huffTree, tokens, huffmanCodes[i]);
            ClearHuffmanTreeIfOnlyOneSymbol(huffmanCodes[i]);
        }

        StoreImageToBitMask(bw, width, 0, refs, [0u], huffmanCodes);
    }
}
