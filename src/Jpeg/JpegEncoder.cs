// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, the whole image encoded
// at once from RGBA pixels, an iMCU row at a time, and the buffer handling, bit writer and vector forms
// new.
//
// The quality scaling, the default settings, the simple progression, the component geometry, the edge
// padding, the dummy blocks, the markers and the pass order follow jcparam.c, jcmaster.c, jcprepct.c,
// jccoefct.c, jcmarker.c and cjpeg.c, which carry these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1998, Thomas G. Lane.
//   Modified 2003-2010 by Guido Vollbeding.
//   Lossless JPEG Modifications: Copyright (C) 1999, Ken Murchison.
//   libjpeg-turbo Modifications:
//   Copyright (C) 2009-2011, 2013-2026, D. R. Commander.
//   Copyright (C) 2010, 2016, 2018, Guido Vollbeding.
//   Copyright (C) 2020, Arm Limited.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.

using System;
using System.Buffers;

namespace DotGame.Imaging.Jpeg;

/// <summary>
/// The JPEG encoder: RGBA pixels to a baseline, extended or progressive Huffman-coded JPEG file, byte for
/// byte what libjpeg-turbo 3.2.1's cjpeg writes from the same pixels with the same settings.
/// </summary>
internal sealed partial class JpegEncoder
{
    // The most pixels an image may have, as the decoder reads
    private const long MaxPixels = 8192L * 8192L;

    // The most pixels a side may have: the frame header holds each in 16 bits
    private const int MaxSide = 65535;

    // The output is written in chunks of this size; past it, the chunk goes to the writer before the
    // next MCU, which the slack has room for (an MCU's worst case is under 5 KB)
    private const int OutputChunk = 64 * 1024;
    private const int OutputSlack = 16 * 1024;

    // The natural (row-major) index of each zigzag position (jpeg_natural_order)
    private static ReadOnlySpan<byte> NaturalOrder =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // libjpeg's standard quantization tables (jcparam.c), in natural order
    private static ReadOnlySpan<byte> LuminanceQuantization =>
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static ReadOnlySpan<byte> ChrominanceQuantization =>
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    private static readonly JpegEncoderOptions Defaults = new();

    private readonly IBufferWriter<byte> writer;
    private readonly int width;
    private readonly int height;
    private readonly JpegEncoderOptions options;
    private readonly Component[] components;
    private readonly int maxH;
    private readonly int maxV;
    private readonly int mcusPerRow;
    private readonly int imcuRows;
    private readonly bool fullBuffer;

    // An iMCU row of the samples at full size, a row of sampleStride bytes each: the first component's
    // plane, and the chroma's own planes or, when they're downsampled, these
    private readonly int sampleStride;
    private readonly byte[]? fullCb;
    private readonly byte[]? fullCr;

    // The quantization tables (natural order) and the divisors made from them, per table: 64
    // reciprocals, 64 corrections and 64 shifts (jcdctmgr.c's, without the scales)
    private readonly ushort[][] quantization = new ushort[2][];
    private readonly ushort[][] divisors = new ushort[2][];
    private readonly bool[] scalarQuantize = new bool[2];
    private readonly bool[] quantizationSent = new bool[2];

    private readonly HuffmanTable[] dcTables = new HuffmanTable[2];
    private readonly HuffmanTable[] acTables = new HuffmanTable[2];

    private readonly byte[] output = ArrayPool<byte>.Shared.Rent(OutputChunk + OutputSlack);
    private int outputPosition;
    private int lastRestartInterval;

    private JpegEncoder(
        IBufferWriter<byte> writer,
        int width,
        int height,
        JpegEncoderOptions options)
    {
        this.writer = writer;
        this.width = width;
        this.height = height;
        this.options = options;

        // jpeg_set_colorspace: YCbCr (ids 1, 2, 3; tables 0, 1, 1) or grayscale (id 1, tables 0), and
        // cjpeg's -sample setting the first component's factors
        if (options.Grayscale)
        {
            this.components = [new Component(1, 1, 1, 0)];
        }
        else
        {
            (int h, int v) = options.Subsampling switch
            {
                JpegSubsampling.YCbCr444 => (1, 1),
                JpegSubsampling.YCbCr422 => (2, 1),
                _ => (2, 2),
            };
            this.components = [new Component(1, h, v, 0), new Component(2, 1, 1, 1), new Component(3, 1, 1, 1)];
        }

        // initial_setup
        this.maxH = 1;
        this.maxV = 1;
        foreach (var c in this.components)
        {
            this.maxH = Math.Max(this.maxH, c.H);
            this.maxV = Math.Max(this.maxV, c.V);
        }

        this.mcusPerRow = DivideRoundUp(width, this.maxH * 8);
        this.imcuRows = DivideRoundUp(height, this.maxV * 8);
        this.fullBuffer = options.Progressive || options.OptimizeHuffman;
        this.sampleStride = this.mcusPerRow * this.maxH * 8;
        for (int ci = 0; ci < this.components.Length; ci++)
        {
            var c = this.components[ci];
            c.WidthInBlocks = DivideRoundUp(width * c.H, this.maxH * 8);
            c.HeightInBlocks = DivideRoundUp(height * c.V, this.maxV * 8);
            c.BlocksPerRow = this.mcusPerRow * c.H;
            c.PlaneStride = this.sampleStride / this.maxH * c.H;
            c.Plane = ArrayPool<byte>.Shared.Rent((c.PlaneStride * c.V * 8) + 64);
            int blockRows = this.fullBuffer ? (this.imcuRows * c.V) : c.V;
            c.Coefficients = ArrayPool<short>.Shared.Rent(blockRows * c.BlocksPerRow * 64);
        }

        if ((this.components.Length == 3) && ((this.maxH > 1) || (this.maxV > 1)))
        {
            this.fullCb = ArrayPool<byte>.Shared.Rent((this.sampleStride * this.maxV * 8) + 64);
            this.fullCr = ArrayPool<byte>.Shared.Rent((this.sampleStride * this.maxV * 8) + 64);
        }

        // jpeg_set_quality: the standard tables scaled for the quality
        int scale = QualityScaling(options.Quality);
        this.quantization[0] = ScaleTable(LuminanceQuantization, scale, options.ForceBaseline);
        this.divisors[0] = MakeDivisors(this.quantization[0], options.Dct, out this.scalarQuantize[0]);
        if (!options.Grayscale)
        {
            this.quantization[1] = ScaleTable(ChrominanceQuantization, scale, options.ForceBaseline);
            this.divisors[1] = MakeDivisors(this.quantization[1], options.Dct, out this.scalarQuantize[1]);
        }
    }

    /// <summary>
    /// Encodes width x height RGBA pixels (alpha is ignored) as a JPEG file, with cjpeg's defaults when
    /// <paramref name="options"/> is null.
    /// </summary>
    public static void Encode(
        IBufferWriter<byte> writer,
        int width,
        int height,
        ReadOnlySpan<byte> pixels,
        JpegEncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        options ??= Defaults;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxSide);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxSide);
        if ((long)width * height > MaxPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"The image ({width} x {height}) has more than 8192 x 8192 pixels in all.");
        }

        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"A {width} x {height} image is {width * height * 4} bytes of RGBA, but {pixels.Length} were given.", nameof(pixels));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.Quality, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Quality, 100, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.RestartRows, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.RestartRows, 65535, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.RestartBlocks, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.RestartBlocks, 65535, nameof(options));
        if ((uint)options.Subsampling > (uint)JpegSubsampling.YCbCr444)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Subsampling, "That isn't a subsampling.");
        }

        if ((uint)options.Dct > (uint)JpegEncoderDct.Fast)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Dct, "That isn't a DCT.");
        }

        var encoder = new JpegEncoder(writer, width, height, options);
        try
        {
            encoder.Run(pixels);
        }
        finally
        {
            encoder.ReturnBuffers();
        }
    }

    // The pooled buffers back to the pool
    private void ReturnBuffers()
    {
        ArrayPool<byte>.Shared.Return(this.output);
        foreach (var c in this.components)
        {
            ArrayPool<byte>.Shared.Return(c.Plane);
            ArrayPool<short>.Shared.Return(c.Coefficients);
        }

        if (this.fullCb != null)
        {
            ArrayPool<byte>.Shared.Return(this.fullCb);
        }

        if (this.fullCr != null)
        {
            ArrayPool<byte>.Shared.Return(this.fullCr);
        }
    }

    private static int DivideRoundUp(
        int a,
        int b) =>
        (a + b - 1) / b;

    // jpeg_quality_scaling
    private static int QualityScaling(
        int quality)
    {
        if (quality <= 0)
        {
            quality = 1;
        }

        return (quality < 50) ? (5000 / quality) : (200 - (quality * 2));
    }

    // jpeg_add_quant_table
    private static ushort[] ScaleTable(
        ReadOnlySpan<byte> basic,
        int scale,
        bool forceBaseline)
    {
        var table = new ushort[64];
        for (int i = 0; i < 64; i++)
        {
            long temp = ((basic[i] * (long)scale) + 50) / 100;
            temp = Math.Clamp(temp, 1, 32767);
            if (forceBaseline && (temp > 255))
            {
                temp = 255;
            }

            table[i] = (ushort)temp;
        }

        return table;
    }

    private void Run(
        ReadOnlySpan<byte> rgba)
    {
        this.WriteFileHeader();
        if (!this.fullBuffer)
        {
            // One sequential scan of every component, with the standard tables, coded as each iMCU
            // row is transformed
            var scan = new Scan(this.components, 0, 63, 0, 0);
            this.StandardHuffmanTables();
            this.StartScan(scan);
            this.WriteFrameHeader();
            this.WriteScanHeader(scan);
            this.StartHuffman(scan);
            for (int row = 0; row < this.imcuRows; row++)
            {
                this.PrepareRow(rgba, row);
                foreach (var c in this.components)
                {
                    this.TransformRow(c, row, 0);
                }

                // An iMCU row is an MCU row of the scan: interleaved, or the one component's one block row
                this.EncodeSequential(scan, row, 1, row);
            }

            this.FinishHuffman();
        }
        else
        {
            for (int row = 0; row < this.imcuRows; row++)
            {
                this.PrepareRow(rgba, row);
                foreach (var c in this.components)
                {
                    this.TransformRow(c, row, row * c.V);
                }
            }

            var scans = this.options.Progressive ? this.SimpleProgression() : [new Scan(this.components, 0, 63, 0, 0)];
            for (int i = 0; i < scans.Length; i++)
            {
                var scan = scans[i];
                if (this.options.Progressive)
                {
                    this.EncodeProgressiveScan(scan, i == 0);
                }
                else
                {
                    this.StartScan(scan);
                    this.GatherSequential(scan);
                    this.WriteFrameHeader();
                    this.WriteScanHeader(scan);
                    this.StartHuffman(scan);
                    this.EncodeSequential(scan, 0, this.imcuRows, 0);
                    this.FinishHuffman();
                }
            }
        }

        this.WriteMarker(0xD9);
        this.FlushOutput();
    }
}
