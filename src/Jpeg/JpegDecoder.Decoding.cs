// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Part of the JPEG decoder (JpegDecoder.cs). The decoder's port notes and notices:
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, a progressive or
// multiscan image decoded whole before it's output, and the Huffman decoding, vector forms and buffer
// handling new.
//
// The inverse DCT's arithmetic, the upsampling, the color conversion, the range limiting, the AC
// refinement, the color space guess, the block smoothing rule and the context rows follow jidctint.c,
// jdsample.c, jdcolor.c, jdcolext.c, jdmaster.c, jdphuff.c, jdhuff.c, jdmainct.c, jdcoefct.c and
// jdapimin.c, which carry these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1998, Thomas G. Lane.
//   Modified 2002-2018 by Guido Vollbeding.
//   Lossless JPEG Modifications: Copyright (C) 1999, Ken Murchison.
//   libjpeg-turbo Modifications:
//   Copyright 2009 Pierre Ossman <ossman@cendio.se> for Cendio AB
//   Copyright (C) 2009-2026, D. R. Commander.
//   Copyright (C) 2013, Linaro Limited.
//   Copyright (C) 2015, 2020, Google, Inc.
//   Copyright (C) 2018, Matthias Räncker.
//   Copyright (C) 2019-2020, Arm Limited.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.
//
// The Arm64 inverse DCT is ported from simd/arm/jidctint-neon.c, which carries this notice:
//   Copyright (C) 2020, Arm Limited.
//   Copyright (C) 2020, 2024-2025, D. R. Commander.
//
//   This software is provided 'as-is', without any express or implied
//   warranty.  In no event will the authors be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//   3. This notice may not be removed or altered from any source distribution.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;

namespace DotGame.Imaging.Jpeg;

internal static partial class JpegDecoder
{
    private sealed partial class Decoding
    {
        private readonly byte[] data;
        private readonly int bytesPerPixel;
        private readonly int outputLength;
        private readonly ushort[]?[] quantTables = new ushort[4][];
        private readonly HuffmanTable?[] dcTables = new HuffmanTable[4];
        private readonly HuffmanTable?[] acTables = new HuffmanTable[4];
        private readonly BitReader reader;
        private Component[] components = [];
        private int width;
        private int height;
        private int maxH;
        private int maxV;
        private int mcusPerLine;
        private int mcusPerColumn;
        private bool progressive;
        private int restartInterval;
        private bool sawJfif;
        private bool sawAdobe;
        private int adobeTransform;
        private int eobRun;
        private bool streaming;         // the planes are rings, and rows are output as the scan goes
        private bool streamed;          // that scan is done
        private int rowsOut;            // the rows output so far
        private byte[][]? rowBuffers;   // each component's upsampled row
        private bool rgb;

        public Decoding(
            byte[] data,
            int bytesPerPixel,
            int outputLength)
        {
            this.data = data;
            this.bytesPerPixel = bytesPerPixel;
            this.outputLength = outputLength;
            this.reader = new BitReader(data);
        }

        public void Run(
            Span<byte> pixels)
        {
            int position = 2;
            bool frame = false;
            while (true)
            {
                // The next marker: 0xFF, any fill 0xFFs, then its code
                while ((position < this.data.Length) && (this.data[position] != 0xFF))
                {
                    position++;
                }

                while ((position < this.data.Length) && (this.data[position] == 0xFF))
                {
                    position++;
                }

                if (position >= this.data.Length)
                {
                    break; // no EOI: libjpeg warns and uses what it has
                }

                int marker = this.data[position++];
                if ((marker == 0xD9) || (marker == 0x00))
                {
                    if (marker == 0xD9)
                    {
                        break;
                    }

                    continue;
                }

                if ((marker >= 0xD0) && (marker <= 0xD7))
                {
                    continue; // a stray restart marker
                }

                if (position + 2 > this.data.Length)
                {
                    throw new InvalidDataException("The JPEG image is cut short.");
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(this.data.AsSpan(position));
                if ((length < 2) || (position + length > this.data.Length))
                {
                    throw new InvalidDataException("The JPEG image is cut short.");
                }

                var segment = this.data.AsSpan(position + 2, length - 2);
                switch (marker)
                {
                    case 0xC0:
                    case 0xC1:
                    case 0xC2:
                        if (frame)
                        {
                            throw new InvalidDataException("The JPEG image has two frames.");
                        }

                        this.ReadFrame(segment, marker == 0xC2);
                        frame = true;
                        break;

                    case 0xC3:
                        throw new NotSupportedException("Lossless JPEG images aren't supported.");

                    case 0xC5:
                    case 0xC6:
                    case 0xC7:
                    case 0xDE:
                        throw new NotSupportedException("Hierarchical JPEG images aren't supported.");

                    case 0xC9:
                    case 0xCA:
                    case 0xCB:
                    case 0xCD:
                    case 0xCE:
                    case 0xCF:
                        throw new NotSupportedException("JPEG images with arithmetic coding aren't supported.");

                    case 0xC4:
                        this.ReadHuffmanTables(segment);
                        break;

                    case 0xDB:
                        this.ReadQuantTables(segment);
                        break;

                    case 0xDD:
                        this.restartInterval = (segment.Length >= 2) ? BinaryPrimitives.ReadUInt16BigEndian(segment) : 0;
                        break;

                    case 0xDA:
                        if (!frame)
                        {
                            throw new InvalidDataException("The JPEG image has a scan before its frame.");
                        }

                        position = this.ReadScan(segment, position + length, pixels);
                        continue;

                    case 0xE0:
                        if ((segment.Length >= 5) && segment.Slice(0, 5).SequenceEqual("JFIF\0"u8))
                        {
                            this.sawJfif = true;
                        }

                        break;

                    case 0xEE:
                        if ((segment.Length >= 12) && segment.Slice(0, 5).SequenceEqual("Adobe"u8))
                        {
                            this.sawAdobe = true;
                            this.adobeTransform = segment[11];
                        }

                        break;

                    case 0xDC:
                        throw new NotSupportedException("JPEG images with a DNL marker aren't supported.");
                }

                position += length;
            }

            if (!frame)
            {
                throw new InvalidDataException("The JPEG image has no frame.");
            }
        }

        private void ReadFrame(
            ReadOnlySpan<byte> segment,
            bool isProgressive)
        {
            if (segment.Length < 6)
            {
                throw new InvalidDataException("The JPEG frame header is malformed.");
            }

            if (segment[0] != 8)
            {
                throw new NotSupportedException($"JPEG images of {segment[0]}-bit samples aren't supported.");
            }

            this.progressive = isProgressive;
            this.height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1));
            this.width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3));
            int count = segment[5];
            if (this.width == 0)
            {
                throw new InvalidDataException("The JPEG image has no pixels.");
            }

            if (this.height == 0)
            {
                throw new NotSupportedException("JPEG images whose height comes in a DNL marker aren't supported.");
            }

            if ((long)this.width * this.height > MaxPixels)
            {
                throw new NotSupportedException($"The JPEG image ({this.width} x {this.height}) is larger than 8192 x 8192 pixels.");
            }

            PixelLayouts.CheckBuffer(this.width, this.height, this.bytesPerPixel, this.outputLength, "pixels");

            if (count == 4)
            {
                throw new NotSupportedException("CMYK and YCCK JPEG images aren't supported.");
            }

            if ((count != 1) && (count != 3))
            {
                throw new InvalidDataException($"The JPEG image has {count} components (a JPEG image has 1, 3 or 4).");
            }

            if (segment.Length < 6 + (3 * count))
            {
                throw new InvalidDataException("The JPEG frame header is malformed.");
            }

            this.components = new Component[count];
            for (int i = 0; i < count; i++)
            {
                var c = new Component
                {
                    Id = UniqueId(segment[6 + (3 * i)], this.components.AsSpan(0, i)),
                    H = segment[7 + (3 * i)] >> 4,
                    V = segment[7 + (3 * i)] & 15,
                    QuantTable = segment[8 + (3 * i)] & 3,
                };
                if ((c.H < 1) || (c.H > 4) || (c.V < 1) || (c.V > 4))
                {
                    throw new InvalidDataException("A JPEG component's sampling factors are out of range.");
                }

                Array.Fill(c.CoefficientBits, -1);
                this.components[i] = c;
                this.maxH = Math.Max(this.maxH, c.H);
                this.maxV = Math.Max(this.maxV, c.V);
            }

            this.mcusPerLine = DivideUp(this.width, 8 * this.maxH);
            this.mcusPerColumn = DivideUp(this.height, 8 * this.maxV);
            foreach (var c in this.components)
            {
                c.DownsampledWidth = DivideUp(this.width * c.H, this.maxH);
                c.DownsampledHeight = DivideUp(this.height * c.V, this.maxV);
                c.WidthInBlocks = DivideUp(this.width * c.H, this.maxH * 8);
                c.HeightInBlocks = DivideUp(this.height * c.V, this.maxV * 8);
                c.BlocksPerLine = this.mcusPerLine * c.H;
                c.BlocksPerColumn = this.mcusPerColumn * c.V;
                c.Stride = c.BlocksPerLine * 8;
                if (isProgressive)
                {
                    c.Coefficients = new short[c.BlocksPerLine * c.BlocksPerColumn * 64];
                }
            }
        }

        // A component id that repeats one before it in the same frame or scan header is read as the
        // largest id before it plus one, as libjpeg-turbo reads it (jdmarker.c), so a frame's
        // components each have their own id and a scan never names one twice: a scan that named a
        // component twice left another undecoded, its samples whatever was in memory
        private static int UniqueId(
            int id,
            ReadOnlySpan<Component> before)
        {
            foreach (var other in before)
            {
                if (other.Id == id)
                {
                    int largest = before[0].Id;
                    foreach (var c in before)
                    {
                        largest = Math.Max(largest, c.Id);
                    }

                    return largest + 1;
                }
            }

            return id;
        }

        private void ReadHuffmanTables(
            ReadOnlySpan<byte> segment)
        {
            while (segment.Length >= 17)
            {
                int tableClass = segment[0] >> 4;
                int id = segment[0] & 15;
                var counts = segment.Slice(1, 16);
                int total = 0;
                foreach (byte n in counts)
                {
                    total += n;
                }

                if ((id > 3) || (tableClass > 1) || (total > 256) || (segment.Length < 17 + total))
                {
                    throw new InvalidDataException("A JPEG Huffman table is malformed.");
                }

                var table = new HuffmanTable(counts, segment.Slice(17, total));
                if (tableClass == 0)
                {
                    this.dcTables[id] = table;
                }
                else
                {
                    this.acTables[id] = table;
                }

                segment = segment.Slice(17 + total);
            }
        }

        private void ReadQuantTables(
            ReadOnlySpan<byte> segment)
        {
            while (segment.Length >= 1)
            {
                int precision = segment[0] >> 4;
                int id = segment[0] & 15;
                int size = 1 + (precision == 0 ? 64 : 128);
                if ((id > 3) || (precision > 1) || (segment.Length < size))
                {
                    throw new InvalidDataException("A JPEG quantization table is malformed.");
                }

                // Stored in zigzag order; kept in natural order
                var table = new ushort[64];
                for (int k = 0; k < 64; k++)
                {
                    table[NaturalOrder[k]] = (precision == 0) ? segment[1 + k] : BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1 + (2 * k)));
                }

                this.quantTables[id] = table;
                segment = segment.Slice(size);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Output(
            Span<byte> rgba)
        {
            if (this.progressive)
            {
                this.CheckSmoothing();
            }

            // A progressive image's blocks through the IDCT (a sequential one's went as they came)
            foreach (var c in this.components)
            {
                if (c.Quant is null)
                {
                    throw new InvalidDataException("A JPEG component has no scan.");
                }

                if (!this.progressive)
                {
                    continue;
                }

                for (int by = 0; by < c.BlocksPerColumn; by++)
                {
                    for (int bx = 0; bx < c.BlocksPerLine; bx++)
                    {
                        Idct.Islow(c.Coefficients.AsSpan(((by * c.BlocksPerLine) + bx) * 64, 64), c.Quant, c.Plane.AsSpan((by * 8 * c.Stride) + (bx * 8)), c.Stride);
                    }
                }
            }

            this.OutputRows(rgba, this.height);
        }

        // Each component's plane: every row, or for a streamed image a ring of three MCU rows
        private void AllocatePlanes(
            bool stream)
        {
            this.streaming = stream;
            foreach (var c in this.components)
            {
                int rows = c.BlocksPerColumn * 8;
                c.RingRows = 0;
                if (stream)
                {
                    int ring = 3 * 8 * ((this.components.Length == 1) ? 1 : c.V);
                    if (ring < rows)
                    {
                        c.RingRows = ring;
                        rows = ring;
                    }
                }

                c.Plane = GC.AllocateUninitializedArray<byte>(c.Stride * rows);
            }
        }

        // A streamed image's rows that can be output before MCU row mcuRow is decoded: those whose
        // samples, and the rows above and below them the upsampling reads, every component has
        private int StreamedRows(
            int mcuRow)
        {
            int rows = this.height;
            foreach (var c in this.components)
            {
                int decoded = mcuRow * 8 * ((this.components.Length == 1) ? 1 : c.V);
                rows = Math.Min(rows, Math.Max(0, ((decoded - 1) * this.maxV) / c.V));
            }

            return rows;
        }

        // Output's rows from the last output up to (not including) row end: each component's row at
        // full size (upsampled into a buffer, or its plane's own row), then to RGBA
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void OutputRows(
            Span<byte> rgba,
            int end)
        {
            if (this.rowBuffers is null)
            {
                this.rowBuffers = new byte[this.components.Length][];
                for (int i = 0; i < this.rowBuffers.Length; i++)
                {
                    this.rowBuffers[i] = new byte[(this.components[i].DownsampledWidth * 4) + 16];
                }

                this.rgb = (this.components.Length == 3) && this.IsRgb();
            }

            var buffers = this.rowBuffers;
            for (int y = this.rowsOut; y < end; y++)
            {
                var output = rgba.Slice(y * this.width * 4, this.width * 4);
                var first = Upsampling.Row(this.components[0], y, this.width, this.maxH, this.maxV, buffers[0]);
                if (this.components.Length == 1)
                {
                    ColorConversion.Gray(first, output);
                    continue;
                }

                var second = Upsampling.Row(this.components[1], y, this.width, this.maxH, this.maxV, buffers[1]);
                var third = Upsampling.Row(this.components[2], y, this.width, this.maxH, this.maxV, buffers[2]);
                if (this.rgb)
                {
                    ColorConversion.Rgb(first, second, third, output);
                }
                else
                {
                    ColorConversion.YccRgb(first, second, third, output);
                }
            }

            this.rowsOut = Math.Max(this.rowsOut, end);
        }

        // libjpeg's guess of a three-component image's color space (jdapimin.c)
        private bool IsRgb()
        {
            if (this.sawJfif)
            {
                return false;
            }

            if (this.sawAdobe)
            {
                return this.adobeTransform == 0;
            }

            return (this.components[0].Id == 82) && (this.components[1].Id == 71) && (this.components[2].Id == 66);
        }

        // libjpeg smooths blocks (jdcoefct.c) when a progressive image's scans leave any of the
        // first nine AC coefficients unrefined; that isn't done here
        private void CheckSmoothing()
        {
            foreach (var c in this.components)
            {
                if (c.CoefficientBits[0] < 0)
                {
                    return;
                }

                for (int k = 0; k < 10; k++)
                {
                    if (c.Quant![NaturalOrder[k]] == 0)
                    {
                        return;
                    }
                }
            }

            foreach (var c in this.components)
            {
                for (int k = 1; k < 10; k++)
                {
                    if (c.CoefficientBits[k] != 0)
                    {
                        throw new NotSupportedException("Progressive JPEG images whose scans leave low coefficients unrefined aren't supported.");
                    }
                }
            }
        }

        private static int DivideUp(
            int a,
            int b) => (a + b - 1) / b;
    }
}
