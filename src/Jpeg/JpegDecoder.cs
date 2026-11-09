// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, the whole image decoded
// at once and output a row at a time, and the Huffman decoding, vector forms and buffer handling new.
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

namespace DotGame.Imaging.Jpeg;

/// <summary>
/// The JPEG decoder (<see cref="JpegCodec.TryGetDecodedSize"/> and <see cref="JpegCodec.Decode"/>).
/// </summary>
internal static partial class JpegDecoder
{
    // Natural (row-major) index of each zigzag position, with 16 extra entries so a damaged
    // block's run past the end lands on the last coefficient (libjpeg's jpeg_natural_order)
    private static readonly int[] NaturalOrder =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    ];

    // The most pixels an image may have: 8192 x 8192, which is 256 MB of pixels
    private const long MaxPixels = 8192L * 8192L;

    /// <summary>Whether the bytes start as a JPEG file does (SOI, then a marker).</summary>
    public static bool HasSignature(
        ReadOnlySpan<byte> data) =>
        (data.Length >= 3) && (data[0] == 0xFF) && (data[1] == 0xD8) && (data[2] == 0xFF);

    /// <summary>
    /// The image's size from its frame header (SOF0 to SOF15), found by walking the segments before
    /// it: false if the bytes aren't a JPEG image, if it's damaged or cut short before the frame
    /// header, or if the height is 0 (it comes in a DNL marker).
    /// </summary>
    public static bool TryReadSize(
        ReadOnlySpan<byte> data,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (!HasSignature(data))
        {
            return false;
        }

        int position = 2;
        while (true)
        {
            // The next marker: 0xFF, any fill 0xFFs, then its code; before the frame header, every
            // marker but SOI's has a segment
            if ((position >= data.Length) || (data[position] != 0xFF))
            {
                return false;
            }

            while ((position < data.Length) && (data[position] == 0xFF))
            {
                position++;
            }

            if (position + 3 > data.Length)
            {
                return false;
            }

            int marker = data[position++];
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position));
            if ((length < 2) || (position + length > data.Length))
            {
                return false;
            }

            // A frame header: SOF0 to SOF15, which aren't DHT (C4), JPG (C8) or DAC (CC)
            if ((marker >= 0xC0) && (marker <= 0xCF) && (marker != 0xC4) && (marker != 0xC8) && (marker != 0xCC))
            {
                var segment = data.Slice(position + 2, length - 2);
                if (segment.Length < 6)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1));
                width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3));
                if ((width == 0) || (height == 0))
                {
                    width = 0;
                    height = 0;
                    return false;
                }

                return true;
            }

            if ((marker == 0xD9) || (marker == 0xDA) || (marker == 0xD8) || ((marker >= 0xD0) && (marker <= 0xD7)) || (marker == 0x01))
            {
                // The end, a scan or a marker with no segment before any frame: not a JPEG this reads
                return false;
            }

            position += length;
        }
    }

    /// <summary>
    /// Decodes a JPEG file to 8-bit RGBA, alpha 255, into <paramref name="pixels"/> (the image's byte
    /// count in a layout of <paramref name="bytesPerPixel"/>, 4: RGBA).
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes aren't a JPEG image, or it's damaged.</exception>
    /// <exception cref="NotSupportedException">The image is of a kind this doesn't decode.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> isn't the image's byte count.</exception>
    public static void Decode(
        ReadOnlySpan<byte> data,
        Span<byte> pixels,
        int bytesPerPixel)
    {
        if (!HasSignature(data))
        {
            throw new InvalidDataException("The bytes aren't a JPEG image.");
        }

        // The decoder keeps the bytes for its whole run, so they're copied into an array: a few
        // percent of a decode's time
        var decoder = new Decoding(data.ToArray(), bytesPerPixel, pixels.Length);
        decoder.Run();
        decoder.Output(pixels);
    }

    private sealed class Component
    {
        public int Id;
        public int H;
        public int V;
        public int QuantTable;
        public short[]? Quant;            // latched at the component's first scan, natural order (16 bits, as libjpeg-turbo's SIMD dequantizes)
        public int WidthInBlocks;
        public int HeightInBlocks;
        public int BlocksPerLine;         // padded to whole MCUs
        public int BlocksPerColumn;
        public int DownsampledWidth;
        public int DownsampledHeight;
        public short[] Coefficients = [];   // progressive images only
        public byte[] Plane = [];           // the samples, BlocksPerLine * 8 wide
        public int Stride;
        public int DcPredictor;
        public int DcTable;
        public int AcTable;
        public int[] CoefficientBits = new int[64];   // progressive: the bits still unknown, -1 none yet
    }
}
