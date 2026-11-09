// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/vp8l_dec.c and the files it uses), modified by Hot Chai Games LLC:
// written in C#, an image decoded whole (not incrementally), with no scaling or cropping. libwebp
// carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

/// <summary>
/// The lossless WebP decoder: a VP8L bitstream to RGBA, and the alpha of a lossy image when it's
/// stored as one (its green channel).
/// </summary>
internal static partial class Vp8LDecoder
{
    private const byte Signature = 0x2f;

    private const int NumArgbCacheRows = 16;
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int MaxCacheBits = 11;
    private const int CodesPerMetaCode = 5;
    private const int DefaultCodeLength = 8;
    private const int CodeLengthLiterals = 16;
    private const int CodeLengthRepeatCode = 16;
    private const int NumCodeLengthCodes = 19;
    private const int PackedBits = 6;
    private const int PackedTableSize = 1 << PackedBits;
    private const int BitsSpecialMarker = 0x100;
    private const uint HashMultiplier = 0x1e35a7bd;

    private const int Green = 0;
    private const int Red = 1;
    private const int Blue = 2;
    private const int AlphaCode = 3;
    private const int Dist = 4;

    // The transforms' types
    private const int PredictorTransform = 0;
    private const int CrossColorTransform = 1;
    private const int SubtractGreenTransform = 2;
    private const int ColorIndexingTransform = 3;

    private static ReadOnlySpan<ushort> AlphabetSize => [NumLiteralCodes + NumLengthCodes, NumLiteralCodes, NumLiteralCodes, NumLiteralCodes, NumDistanceCodes];

    private static ReadOnlySpan<byte> LiteralMap => [0, 1, 1, 1, 0];

    private static ReadOnlySpan<byte> CodeLengthExtraBits => [2, 3, 7];

    private static ReadOnlySpan<byte> CodeLengthRepeatOffsets => [3, 3, 11];

    private static ReadOnlySpan<byte> CodeLengthCodeOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // Distance codes 1 to 120 as (y, 8 - x) offsets of the pixels nearest
    private static ReadOnlySpan<byte> CodeToPlane =>
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a,
        0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
        0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b,
        0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
        0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c,
        0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b,
        0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b,
        0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
        0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f,
        0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    // The tables' sizes for a color cache of 0 to 11 bits (an estimate: the tables grow if it's short)
    private static ReadOnlySpan<ushort> TableSize =>
    [
        2300 + 654, 2300 + 656, 2300 + 658, 2300 + 662, 2300 + 670, 2300 + 686,
        2300 + 718, 2300 + 782, 2300 + 912, 2300 + 1168, 2300 + 1680, 2300 + 2704,
    ];

    /// <summary>
    /// The image's size from its header (libwebp's VP8LGetInfo): false unless it has the signature and
    /// version 0.
    /// </summary>
    public static bool TryReadSize(
        ReadOnlySpan<byte> vp8l,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if ((vp8l.Length < 5) || (vp8l[0] != Signature) || ((vp8l[4] >> 5) != 0))
        {
            return false;
        }

        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(vp8l.Slice(1));
        width = (int)(bits & 0x3fff) + 1;
        height = (int)((bits >> 14) & 0x3fff) + 1;
        return true;
    }

    /// <summary>
    /// Decodes the image into <paramref name="rgba"/>, width x height RGBA pixels, straight alpha.
    /// </summary>
    /// <exception cref="InvalidDataException">The image is damaged.</exception>
    public static void Decode(
        ReadOnlySpan<byte> vp8l,
        int width,
        int height,
        Span<byte> rgba)
    {
        // The bytes and the pixels are pooled: an app decoding images one after another reuses them
        byte[] data = ArrayPool<byte>.Shared.Rent(vp8l.Length);
        vp8l.CopyTo(data);
        var decoder = new Decoding(data, vp8l.Length);
        try
        {
            decoder.DecodeImage(width, height, rgba);
        }
        finally
        {
            decoder.ReturnPixels();
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    /// <summary>
    /// Decodes a lossy image's alpha stored losslessly (an image stream with no header, of which the
    /// green channel is the alpha) into <paramref name="plane"/>, width x height bytes, unfiltering its
    /// rows with <paramref name="filter"/> (0 to 3: none, horizontal, vertical, gradient).
    /// </summary>
    /// <exception cref="InvalidDataException">The alpha is damaged.</exception>
    public static void DecodeAlpha(
        ReadOnlySpan<byte> stream,
        int width,
        int height,
        int filter,
        byte[] plane)
    {
        byte[] data = ArrayPool<byte>.Shared.Rent(stream.Length);
        stream.CopyTo(data);
        var decoder = new Decoding(data, stream.Length);
        try
        {
            decoder.DecodeAlpha(width, height, filter, plane);
        }
        finally
        {
            decoder.ReturnPixels();
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static InvalidDataException Damaged()
    {
        return new InvalidDataException("The lossless WebP image is damaged.");
    }

    private struct HTreeGroup
    {
        public int Green;
        public int Red;
        public int Blue;
        public int Alpha;
        public int Dist;
        public bool IsTrivialLiteral;
        public bool IsTrivialCode;
        public bool UsePackedTable;
        public uint LiteralArb;
    }

    // An image stream's codes: its color cache, its meta-Huffman image and its groups of codes
    private sealed class Metadata
    {
        public int ColorCacheSize;
        public int ColorCacheShift;
        public uint[]? ColorCache;
        public int HuffmanMask;
        public int HuffmanSubsampleBits;
        public int HuffmanXSize;
        public uint[]? HuffmanImage;
        public HTreeGroup[] Groups = [];
        public Vp8LHuffmanCode32[] Packed = [];
        public Vp8LHuffmanTables Tables = null!;
    }

    private struct Transform
    {
        public int Type;
        public int Bits;
        public int XSize;
        public int YSize;
        public uint[] Data;

        // A palette's colors as RGBA, for a palette applied last, which maps to the output
        public uint[]? RgbaColors;

        // A packed palette's tables of each byte's pixels, ARGB and RGBA (built when first used)
        public uint[]? Expanded;
        public uint[]? ExpandedRgba;
    }

    private sealed partial class Decoding(byte[] data, int dataLength)
    {
        private readonly Transform[] transforms = new Transform[4];
        private Vp8LBitReader br = Vp8LBitReader.Create(data, dataLength);
        private Vp8LHuffmanTables? lengthsTable;
        private bool pooledPixels;
        private Metadata hdr = new();
        private int nextTransform;
        private uint transformsSeen;

        // The image as decoded: its width (after a color-indexing transform, its packed width), and the
        // output's
        private int width;
        private int height;
        private int ioWidth;
        private uint[] pixels = [];
        private int argbCache;
        private int lastRow;
        private int lastOutRow;

        // The alpha output: its plane, its filter and the row last unfiltered (-1 for none)
        private byte[]? alphaPlane;
        private int alphaFilter;
        private int alphaPrevLine = -1;

        public void DecodeImage(
            int expectedWidth,
            int expectedHeight,
            Span<byte> rgba)
        {
            // The header: signature, size, alpha hint, version
            if (this.br.ReadBits(data, 8) != Signature)
            {
                throw Damaged();
            }

            int w = (int)this.br.ReadBits(data, 14) + 1;
            int h = (int)this.br.ReadBits(data, 14) + 1;
            this.br.ReadBits(data, 1);
            if ((this.br.ReadBits(data, 3) != 0) || this.br.Eos || (w != expectedWidth) || (h != expectedHeight))
            {
                throw Damaged();
            }

            this.ioWidth = w;
            this.DecodeImageStream(w, h, isLevel0: true);

            // The pixels as decoded, a row before the cache (the predictor's row above), and the cache
            // of rows after the inverse transforms
            long numPixels = (long)this.width * this.height;
            this.RentPixels((int)(numPixels + w + ((long)w * NumArgbCacheRows)));
            this.argbCache = (int)numPixels + w;
            this.DecodeImageData(this.pixels, this.width, this.height, this.height, OutputKind.Rgba, rgba);
        }

        public void ReturnPixels()
        {
            if (this.pooledPixels)
            {
                ArrayPool<uint>.Shared.Return(this.pixels);
                this.pixels = [];
                this.pooledPixels = false;
            }
        }

        private void RentPixels(
            int count)
        {
            this.pixels = ArrayPool<uint>.Shared.Rent(count);
            this.pooledPixels = true;
        }

        public void DecodeAlpha(
            int w,
            int h,
            int filter,
            byte[] plane)
        {
            this.ioWidth = w;
            this.width = w;
            this.height = h;
            this.alphaPlane = plane;
            this.alphaFilter = filter;
            this.DecodeImageStream(w, h, isLevel0: true);

            if ((this.nextTransform == 1) && (this.transforms[0].Type == ColorIndexingTransform) && this.Is8bOptimizable())
            {
                // Indices a byte each, mapped straight to alpha (each decoded before it's read, so a
                // pooled buffer does)
                byte[] indices = ArrayPool<byte>.Shared.Rent(this.width * this.height);
                try
                {
                    this.DecodeAlphaData(indices, this.width, this.height, h);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(indices);
                }
            }
            else
            {
                long numPixels = (long)this.width * this.height;
                this.RentPixels((int)(numPixels + w + ((long)w * NumArgbCacheRows)));
                this.argbCache = (int)numPixels + w;
                this.DecodeImageData(this.pixels, this.width, this.height, h, OutputKind.Alpha, default);
            }
        }

        // libwebp's DecodeImageStream: the level-0 image's transforms and codes (its data decoded
        // after), or a sub-image (a transform's data, the meta-Huffman image) decoded whole
        private uint[]? DecodeImageStream(
            int xsize,
            int ysize,
            bool isLevel0)
        {
            int transformXSize = xsize;
            int transformYSize = ysize;
            if (isLevel0)
            {
                while (this.br.ReadBits(data, 1) != 0)
                {
                    this.ReadTransform(ref transformXSize, transformYSize);
                }
            }

            int colorCacheBits = 0;
            if (this.br.ReadBits(data, 1) != 0)
            {
                colorCacheBits = (int)this.br.ReadBits(data, 4);
                if ((colorCacheBits < 1) || (colorCacheBits > MaxCacheBits))
                {
                    throw Damaged();
                }
            }

            var meta = this.ReadHuffmanCodes(transformXSize, transformYSize, colorCacheBits, isLevel0);
            if (colorCacheBits > 0)
            {
                meta.ColorCacheSize = 1 << colorCacheBits;
                meta.ColorCacheShift = 32 - colorCacheBits;
                meta.ColorCache = new uint[1 << colorCacheBits];
            }

            // libwebp's UpdateDecoder
            this.hdr = meta;
            this.width = transformXSize;
            this.height = transformYSize;
            meta.HuffmanXSize = SubSampleSize(transformXSize, meta.HuffmanSubsampleBits);
            meta.HuffmanMask = (meta.HuffmanSubsampleBits == 0) ? ~0 : (1 << meta.HuffmanSubsampleBits) - 1;
            if (isLevel0)
            {
                return null;
            }

            var image = GC.AllocateUninitializedArray<uint>(transformXSize * transformYSize);
            this.DecodeImageData(image, transformXSize, transformYSize, transformYSize, OutputKind.None, default);
            if (this.br.Eos)
            {
                throw Damaged();
            }

            return image;
        }

        private void ReadTransform(
            ref int xsize,
            int ysize)
        {
            int type = (int)this.br.ReadBits(data, 2);
            if ((this.transformsSeen & (1u << type)) != 0)
            {
                throw Damaged();
            }

            this.transformsSeen |= 1u << type;
            ref var transform = ref this.transforms[this.nextTransform];
            transform.Type = type;
            transform.XSize = xsize;
            transform.YSize = ysize;
            transform.Data = [];
            this.nextTransform++;
            switch (type)
            {
                case PredictorTransform:
                case CrossColorTransform:
                    transform.Bits = 2 + (int)this.br.ReadBits(data, 3);
                    transform.Data = this.DecodeImageStream(SubSampleSize(transform.XSize, transform.Bits), SubSampleSize(transform.YSize, transform.Bits), isLevel0: false)!;
                    break;

                case ColorIndexingTransform:
                    {
                        int numColors = (int)this.br.ReadBits(data, 8) + 1;
                        int bits = (numColors > 16) ? 0 : (numColors > 4) ? 1 : (numColors > 2) ? 2 : 3;
                        xsize = SubSampleSize(transform.XSize, bits);
                        transform.Bits = bits;
                        var colors = this.DecodeImageStream(numColors, 1, isLevel0: false)!;
                        transform.Data = ExpandColorMap(colors, numColors, bits);
                        transform.RgbaColors = new uint[transform.Data.Length];
                        ConvertBgraToRgba(transform.Data, transform.RgbaColors);
                        break;
                    }

                default:
                    break;
            }
        }

        // The palette's colors are stored as differences from the one before; the map is filled to
        // the most an index of its bits can reach, the rest transparent black
        private static uint[] ExpandColorMap(
            uint[] colors,
            int numColors,
            int bits)
        {
            int finalNumColors = 1 << (8 >> bits);
            var map = new uint[finalNumColors];
            var source = MemoryMarshal.AsBytes(colors.AsSpan());
            var target = MemoryMarshal.AsBytes(map.AsSpan());
            map[0] = colors[0];
            for (int i = 4; i < 4 * numColors; i++)
            {
                target[i] = (byte)(source[i] + target[i - 4]);
            }

            return map;
        }

        private Metadata ReadHuffmanCodes(
            int xsize,
            int ysize,
            int colorCacheBits,
            bool allowRecursion)
        {
            var meta = new Metadata();
            int numGroups = 1;
            int numGroupsMax = 1;
            int[]? mapping = null;
            uint[]? huffmanImage = null;
            if (allowRecursion && (this.br.ReadBits(data, 1) != 0))
            {
                int precision = 2 + (int)this.br.ReadBits(data, 3);
                int huffmanXSize = SubSampleSize(xsize, precision);
                int huffmanYSize = SubSampleSize(ysize, precision);
                int huffmanPixels = huffmanXSize * huffmanYSize;
                huffmanImage = this.DecodeImageStream(huffmanXSize, huffmanYSize, isLevel0: false)!;
                meta.HuffmanSubsampleBits = precision;
                for (int i = 0; i < huffmanPixels; i++)
                {
                    int group = (int)((huffmanImage[i] >> 8) & 0xffff);
                    huffmanImage[i] = (uint)group;
                    if (group >= numGroupsMax)
                    {
                        numGroupsMax = group + 1;
                    }
                }

                // Many groups, or more than there are pixels: only those used are kept, numbered as used
                if ((numGroupsMax > 1000) || (numGroupsMax > xsize * ysize))
                {
                    mapping = new int[numGroupsMax];
                    mapping.AsSpan().Fill(-1);
                    numGroups = 0;
                    for (int i = 0; i < huffmanPixels; i++)
                    {
                        ref int mapped = ref mapping[huffmanImage[i]];
                        if (mapped == -1)
                        {
                            mapped = numGroups++;
                        }

                        huffmanImage[i] = (uint)mapped;
                    }
                }
                else
                {
                    numGroups = numGroupsMax;
                }
            }

            if (this.br.Eos)
            {
                throw Damaged();
            }

            meta.HuffmanImage = huffmanImage;
            this.ReadHuffmanCodesHelper(meta, colorCacheBits, numGroups, numGroupsMax, mapping);
            return meta;
        }

        private void ReadHuffmanCodesHelper(
            Metadata meta,
            int colorCacheBits,
            int numGroups,
            int numGroupsMax,
            int[]? mapping)
        {
            int maxAlphabetSize = AlphabetSize[0] + ((colorCacheBits > 0) ? 1 << colorCacheBits : 0);
            var codeLengths = new int[maxAlphabetSize];
            var groups = new HTreeGroup[numGroups];
            var tables = new Vp8LHuffmanTables(numGroups * TableSize[colorCacheBits]);
            var packed = new Vp8LHuffmanCode32[numGroups * PackedTableSize];
            meta.Groups = groups;
            meta.Tables = tables;
            meta.Packed = packed;
            Span<int> starts = stackalloc int[CodesPerMetaCode];
            for (int i = 0; i < numGroupsMax; i++)
            {
                if ((mapping is not null) && (mapping[i] == -1))
                {
                    // A group no pixel uses: its codes are read and checked, and not kept
                    for (int j = 0; j < CodesPerMetaCode; j++)
                    {
                        int alphabetSize = AlphabetSize[j] + (((j == 0) && (colorCacheBits > 0)) ? 1 << colorCacheBits : 0);
                        if (this.ReadHuffmanCode(alphabetSize, codeLengths, tables, build: false, out _) == 0)
                        {
                            throw Damaged();
                        }
                    }

                    continue;
                }

                int index = (mapping is null) ? i : mapping[i];
                ref var group = ref groups[index];
                int totalSize = 0;
                bool isTrivialLiteral = true;
                int maxBits = 0;
                for (int j = 0; j < CodesPerMetaCode; j++)
                {
                    int alphabetSize = AlphabetSize[j] + (((j == 0) && (colorCacheBits > 0)) ? 1 << colorCacheBits : 0);
                    int size = this.ReadHuffmanCode(alphabetSize, codeLengths, tables, build: true, out int start);
                    if (size == 0)
                    {
                        throw Damaged();
                    }

                    starts[j] = start;
                    int rootBits = tables.Codes[start].Bits;
                    if (isTrivialLiteral && (LiteralMap[j] == 1))
                    {
                        isTrivialLiteral = rootBits == 0;
                    }

                    totalSize += rootBits;
                    tables.Used += size;
                    if (j <= AlphaCode)
                    {
                        int localMaxBits = codeLengths[0];
                        for (int k = 1; k < alphabetSize; k++)
                        {
                            if (codeLengths[k] > localMaxBits)
                            {
                                localMaxBits = codeLengths[k];
                            }
                        }

                        maxBits += localMaxBits;
                    }
                }

                group.Green = starts[Green];
                group.Red = starts[Red];
                group.Blue = starts[Blue];
                group.Alpha = starts[AlphaCode];
                group.Dist = starts[Dist];
                group.IsTrivialLiteral = isTrivialLiteral;
                group.IsTrivialCode = false;
                if (isTrivialLiteral)
                {
                    uint red = tables.Codes[group.Red].Value;
                    uint blue = tables.Codes[group.Blue].Value;
                    uint alpha = tables.Codes[group.Alpha].Value;
                    group.LiteralArb = (alpha << 24) | (red << 16) | blue;
                    if ((totalSize == 0) && (tables.Codes[group.Green].Value < NumLiteralCodes))
                    {
                        group.IsTrivialCode = true;
                        group.LiteralArb |= (uint)tables.Codes[group.Green].Value << 8;
                    }
                }

                group.UsePackedTable = !group.IsTrivialCode && (maxBits < PackedBits);
                if (group.UsePackedTable)
                {
                    BuildPackedTable(tables.Codes, ref group, packed.AsSpan(index * PackedTableSize, PackedTableSize));
                }
            }
        }

        // The literals of a code whose four lengths together are under 6 bits, all looked up at once
        private static void BuildPackedTable(
            Vp8LHuffmanCode[] codes,
            ref HTreeGroup group,
            Span<Vp8LHuffmanCode32> table)
        {
            for (uint code = 0; code < PackedTableSize; code++)
            {
                uint bits = code;
                ref var huff = ref table[(int)code];
                var green = codes[group.Green + (int)bits];
                if (green.Value >= NumLiteralCodes)
                {
                    huff.Bits = green.Bits + BitsSpecialMarker;
                    huff.Value = green.Value;
                }
                else
                {
                    huff.Bits = 0;
                    huff.Value = 0;
                    bits >>= Accumulate(green, 8, ref huff);
                    bits >>= Accumulate(codes[group.Red + (int)bits], 16, ref huff);
                    bits >>= Accumulate(codes[group.Blue + (int)bits], 0, ref huff);
                    Accumulate(codes[group.Alpha + (int)bits], 24, ref huff);
                }
            }
        }

        private static int Accumulate(
            Vp8LHuffmanCode code,
            int shift,
            ref Vp8LHuffmanCode32 huff)
        {
            huff.Bits += code.Bits;
            huff.Value |= (uint)code.Value << shift;
            return code.Bits;
        }

        // A code's lengths, simple (one or two symbols) or themselves Huffman coded, and its table: its
        // size, or 0 if it isn't a complete code
        private int ReadHuffmanCode(
            int alphabetSize,
            int[] codeLengths,
            Vp8LHuffmanTables tables,
            bool build,
            out int start)
        {
            start = 0;
            bool ok;
            Array.Clear(codeLengths, 0, alphabetSize);
            if (this.br.ReadBits(data, 1) != 0)
            {
                int numSymbols = (int)this.br.ReadBits(data, 1) + 1;
                int firstSymbolLengthCode = (int)this.br.ReadBits(data, 1);
                int symbol = (int)this.br.ReadBits(data, (firstSymbolLengthCode == 0) ? 1 : 8);
                codeLengths[symbol] = 1;
                if (numSymbols == 2)
                {
                    symbol = (int)this.br.ReadBits(data, 8);
                    codeLengths[symbol] = 1;
                }

                ok = true;
            }
            else
            {
                Span<int> codeLengthCodeLengths = stackalloc int[NumCodeLengthCodes];
                codeLengthCodeLengths.Clear();
                int numCodes = (int)this.br.ReadBits(data, 4) + 4;
                for (int i = 0; i < numCodes; i++)
                {
                    codeLengthCodeLengths[CodeLengthCodeOrder[i]] = (int)this.br.ReadBits(data, 3);
                }

                ok = this.ReadHuffmanCodeLengths(codeLengthCodeLengths, alphabetSize, codeLengths);
            }

            ok = ok && !this.br.Eos;
            if (!ok)
            {
                throw Damaged();
            }

            var lengths = codeLengths.AsSpan(0, alphabetSize);
            int size = build ? tables.Build(Vp8LHuffmanTables.TableBits, lengths, out start) : tables.Validate(Vp8LHuffmanTables.TableBits, lengths);
            if (size == 0)
            {
                throw Damaged();
            }

            return size;
        }

        private bool ReadHuffmanCodeLengths(
            ReadOnlySpan<int> codeLengthCodeLengths,
            int numSymbols,
            int[] codeLengths)
        {
            var lengthsTable = this.lengthsTable ??= new Vp8LHuffmanTables(1 << Vp8LHuffmanTables.LengthsTableBits);
            lengthsTable.Used = 0;
            if (lengthsTable.Build(Vp8LHuffmanTables.LengthsTableBits, codeLengthCodeLengths, out _) == 0)
            {
                return false;
            }

            var table = lengthsTable.Codes;
            int maxSymbol;
            if (this.br.ReadBits(data, 1) != 0)
            {
                int lengthBits = 2 + (2 * (int)this.br.ReadBits(data, 3));
                maxSymbol = 2 + (int)this.br.ReadBits(data, lengthBits);
                if (maxSymbol > numSymbols)
                {
                    return false;
                }
            }
            else
            {
                maxSymbol = numSymbols;
            }

            int previous = DefaultCodeLength;
            int symbol = 0;
            while (symbol < numSymbols)
            {
                if (maxSymbol-- == 0)
                {
                    break;
                }

                this.br.Fill(data);
                var p = table[this.br.Prefetch() & Vp8LHuffmanTables.LengthsTableMask];
                this.br.BitPos += p.Bits;
                int codeLength = p.Value;
                if (codeLength < CodeLengthLiterals)
                {
                    codeLengths[symbol++] = codeLength;
                    if (codeLength != 0)
                    {
                        previous = codeLength;
                    }
                }
                else
                {
                    bool usePrevious = codeLength == CodeLengthRepeatCode;
                    int slot = codeLength - CodeLengthLiterals;
                    int repeat = (int)this.br.ReadBits(data, CodeLengthExtraBits[slot]) + CodeLengthRepeatOffsets[slot];
                    if (symbol + repeat > numSymbols)
                    {
                        return false;
                    }

                    int length = usePrevious ? previous : 0;
                    while (repeat-- > 0)
                    {
                        codeLengths[symbol++] = length;
                    }
                }
            }

            return true;
        }

        private bool Is8bOptimizable()
        {
            var meta = this.hdr;
            if (meta.ColorCacheSize > 0)
            {
                return false;
            }

            var codes = meta.Tables.Codes;
            foreach (var group in meta.Groups)
            {
                if ((codes[group.Red].Bits > 0) || (codes[group.Blue].Bits > 0) || (codes[group.Alpha].Bits > 0))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static int SubSampleSize(
        int size,
        int bits)
    {
        return (size + (1 << bits) - 1) >> bits;
    }

    private enum OutputKind
    {
        None,
        Rgba,
        Alpha,
    }
}
