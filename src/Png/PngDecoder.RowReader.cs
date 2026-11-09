// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.Png;

internal static partial class PngDecoder
{
    // Reads, unfilters and converts rows. Pixels are little-endian uints, R in the low byte: RGBA
    // in memory on every platform this library supports.
    private sealed class RowReader
    {
        // Zeros before each row's bytes: the "pixel to the left" of its first pixel (a pixel
        // is at most 8 bytes)
        private const int Lead = 8;

        // Room after each row for a whole vector at its end
        private const int Tail = 16;

        private readonly ImageInfo image;
        private readonly bool premultiply;

        // A pixel per sample value for palette and grayscale images to 8 bits, already
        // premultiplied if asked; for a palette, the entries it has
        private readonly uint[]? table;
        private readonly int tableCount;

        private byte[] current;
        private byte[] prior;
        private byte[] spare;

        public RowReader(
            in ImageInfo image,
            bool premultiply)
        {
            this.image = image;
            this.premultiply = premultiply;

            int length = Lead + RowLength(image, image.Width) + 1 + Tail;
            this.current = new byte[length];
            this.prior = new byte[length];
            this.spare = new byte[length];

            if (image.ColorType == Indexed)
            {
                var palette = image.Palette!;
                var paletteAlpha = image.PaletteAlpha;
                this.tableCount = palette.Length / 3;
                this.table = new uint[256];
                for (int i = 0; i < this.tableCount; i++)
                {
                    uint a = ((paletteAlpha is not null) && (i < paletteAlpha.Length)) ? paletteAlpha[i] : 255u;
                    uint pixel = palette[i * 3] | ((uint)palette[(i * 3) + 1] << 8) | ((uint)palette[(i * 3) + 2] << 16) | (a << 24);
                    this.table[i] = premultiply ? Premultiply.Pixel(pixel) : pixel;
                }
            }
            else if ((image.ColorType == Grayscale) && (image.BitDepth <= 8))
            {
                this.tableCount = 1 << image.BitDepth;
                this.table = new uint[256];
                for (int i = 0; i < this.tableCount; i++)
                {
                    uint value = ToByte(i, image.BitDepth);
                    uint a = (i == image.TransparentGray) ? 0u : 255u;
                    uint pixel = (value * 0x010101u) | (a << 24);
                    this.table[i] = premultiply ? Premultiply.Pixel(pixel) : pixel;
                }
            }
        }

        // Unfilters a pass's rows and writes their pixels
        public void ReadPass(
            Stream zlib,
            Span<uint> pixels,
            int xStart,
            int yStart,
            int xStep,
            int yStep)
        {
            int width = PassSize(this.image.Width, xStart, xStep);
            int height = PassSize(this.image.Height, yStart, yStep);
            if ((width == 0) || (height == 0))
            {
                return;
            }

            int rowLength = RowLength(this.image, width);

            // Filters work on whole bytes: a pixel's, or 1 for pixels smaller than a byte
            int filterStep = Math.Max(1, (this.image.Channels * this.image.BitDepth) / 8);

            // The row above a pass's first is all zeros
            var above = this.prior;
            var current = this.current;
            var spare = this.spare;
            Array.Clear(above);
            int row = 0;
            while (row < height)
            {
                byte filter = ReadRow(zlib, current, rowLength);

                // Two Paeth rows of small pixels, the most common rows, go together
                if ((filter == 4) && (filterStep >= 2) && (filterStep <= 4) && (row + 1 < height))
                {
                    byte nextFilter = ReadRow(zlib, spare, rowLength);
                    if (nextFilter == 4)
                    {
                        PaethTwoRows(ref current[Lead], ref spare[Lead], ref above[Lead], rowLength, filterStep);
                    }
                    else
                    {
                        Unfilter(filter, ref current[Lead], ref above[Lead], rowLength, filterStep);
                        Unfilter(nextFilter, ref spare[Lead], ref current[Lead], rowLength, filterStep);
                    }

                    this.WriteRow(current.AsSpan(Lead, rowLength), pixels, ((yStart + (row * yStep)) * this.image.Width) + xStart, xStep, width);
                    this.WriteRow(spare.AsSpan(Lead, rowLength), pixels, ((yStart + ((row + 1) * yStep)) * this.image.Width) + xStart, xStep, width);
                    (above, current, spare) = (spare, above, current);
                    row += 2;
                    continue;
                }

                Unfilter(filter, ref current[Lead], ref above[Lead], rowLength, filterStep);
                this.WriteRow(current.AsSpan(Lead, rowLength), pixels, ((yStart + (row * yStep)) * this.image.Width) + xStart, xStep, width);
                (above, current) = (current, above);
                row++;
            }

            this.prior = above;
            this.current = current;
            this.spare = spare;
        }

        // Reads a row after its filter type byte, which goes just before it and then back to
        // zero
        private static byte ReadRow(
            Stream zlib,
            byte[] buffer,
            int rowLength)
        {
            if (zlib.ReadAtLeast(buffer.AsSpan(Lead - 1, rowLength + 1), rowLength + 1, throwOnEndOfStream: false) < rowLength + 1)
            {
                throw new InvalidDataException("The PNG image's pixels are cut short.");
            }

            byte filter = buffer[Lead - 1];
            buffer[Lead - 1] = 0;
            return filter;
        }

        // Each filter adds a prediction to every byte: from the byte a pixel to the left (a),
        // the one above (b) and the one above that (c), which are zeros off the image's edge.
        // Rows of 3- and 4-byte pixels, the common ones, carry a pixel's bytes in locals.
        private static void Unfilter(
            byte filter,
            ref byte row,
            ref byte above,
            int length,
            int step)
        {
            switch (filter)
            {
                case 0:
                    break;

                case 1: // Sub
                    if (step == 4)
                    {
                        uint left = 0;
                        for (int i = 0; i < length; i += 4)
                        {
                            left = AddBytes(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref row, i)), left);
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref row, i), left);
                        }
                    }
                    else if (step == 3)
                    {
                        byte a0 = 0, a1 = 0, a2 = 0;
                        for (int i = 0; i < length; i += 3)
                        {
                            ref byte p = ref Unsafe.Add(ref row, i);
                            a0 = p = (byte)(p + a0);
                            a1 = Unsafe.Add(ref p, 1) = (byte)(Unsafe.Add(ref p, 1) + a1);
                            a2 = Unsafe.Add(ref p, 2) = (byte)(Unsafe.Add(ref p, 2) + a2);
                        }
                    }
                    else if (step == 1)
                    {
                        byte a = 0;
                        for (int i = 0; i < length; i++)
                        {
                            ref byte p = ref Unsafe.Add(ref row, i);
                            a = p = (byte)(p + a);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < length; i++)
                        {
                            Unsafe.Add(ref row, i) += Unsafe.Add(ref row, i - step);
                        }
                    }

                    break;

                case 2: // Up
                    {
                        // Whole vectors: the rows have room after them
                        int i = 0;
                        for (; i < length; i += 16)
                        {
                            (Vector128.LoadUnsafe(ref row, (nuint)i) + Vector128.LoadUnsafe(ref above, (nuint)i)).StoreUnsafe(ref row, (nuint)i);
                        }

                        break;
                    }

                case 3: // Average
                    if (step == 4)
                    {
                        uint left = 0;
                        for (int i = 0; i < length; i += 4)
                        {
                            uint up = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref above, i));

                            // Each byte's (left + up) / 2, without carries between bytes
                            uint average = (left & up) + (((left ^ up) & 0xfefefefe) >> 1);
                            left = AddBytes(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref row, i)), average);
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref row, i), left);
                        }
                    }
                    else if (step == 3)
                    {
                        int a0 = 0, a1 = 0, a2 = 0;
                        for (int i = 0; i < length; i += 3)
                        {
                            ref byte p = ref Unsafe.Add(ref row, i);
                            ref byte b = ref Unsafe.Add(ref above, i);
                            a0 = p = (byte)(p + ((a0 + b) >> 1));
                            a1 = Unsafe.Add(ref p, 1) = (byte)(Unsafe.Add(ref p, 1) + ((a1 + Unsafe.Add(ref b, 1)) >> 1));
                            a2 = Unsafe.Add(ref p, 2) = (byte)(Unsafe.Add(ref p, 2) + ((a2 + Unsafe.Add(ref b, 2)) >> 1));
                        }
                    }
                    else if (step == 1)
                    {
                        int a = 0;
                        for (int i = 0; i < length; i++)
                        {
                            ref byte p = ref Unsafe.Add(ref row, i);
                            a = p = (byte)(p + ((a + Unsafe.Add(ref above, i)) >> 1));
                        }
                    }
                    else
                    {
                        for (int i = 0; i < length; i++)
                        {
                            Unsafe.Add(ref row, i) += (byte)((Unsafe.Add(ref row, i - step) + Unsafe.Add(ref above, i)) >> 1);
                        }
                    }

                    break;

                case 4: // Paeth
                    if (step == 1)
                    {
                        int a = 0, c = 0;
                        for (int i = 0; i < length; i++)
                        {
                            ref byte p = ref Unsafe.Add(ref row, i);
                            int b = Unsafe.Add(ref above, i);
                            a = p = (byte)(p + Paeth(a, b, c));
                            c = b;
                        }
                    }
                    else
                    {
                        PaethPixels(ref row, ref above, length, step);
                    }

                    break;

                default:
                    throw new InvalidDataException($"The PNG image has a row with an unknown filter ({filter}).");
            }
        }

        // Paeth for pixels of 2 to 8 bytes: a pixel's bytes in a vector's lanes (as 16-bit
        // values, which hold the sums), so the pixel's bytes are predicted together. Each row
        // has room after it for the 8 bytes read at its last pixel.
        private static void PaethPixels(
            ref byte row,
            ref byte above,
            int length,
            int step)
        {
            var left = Vector128<short>.Zero;
            var upLeft = Vector128<short>.Zero;
            var byteMask = Vector128.Create((short)0xff);
            for (int i = 0; i < length; i += step)
            {
                ref byte p = ref Unsafe.Add(ref row, i);
                var up = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref above, i))).AsByte()).AsInt16();
                var raw = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref p)).AsByte()).AsInt16();

                var upMinusUpLeft = up - upLeft;
                var leftMinusUpLeft = left - upLeft;
                var toLeft = Vector128.Abs(upMinusUpLeft);
                var toUp = Vector128.Abs(leftMinusUpLeft);
                var toUpLeft = Vector128.Abs(upMinusUpLeft + leftMinusUpLeft);
                var nearer = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(toUp, toUpLeft), up, upLeft);
                var leftNearest = Vector128.LessThanOrEqual(toLeft, toUp) & Vector128.LessThanOrEqual(toLeft, toUpLeft);
                left = (raw + Vector128.ConditionalSelect(leftNearest, left, nearer)) & byteMask;
                upLeft = up;

                ulong bytes = Vector128.Narrow(left.AsUInt16(), left.AsUInt16()).AsUInt64().ToScalar();
                switch (step)
                {
                    case 2:
                        Unsafe.WriteUnaligned(ref p, (ushort)bytes);
                        break;

                    case 3:
                        Unsafe.WriteUnaligned(ref p, (ushort)bytes);
                        Unsafe.Add(ref p, 2) = (byte)(bytes >> 16);
                        break;

                    case 4:
                        Unsafe.WriteUnaligned(ref p, (uint)bytes);
                        break;

                    case 6:
                        Unsafe.WriteUnaligned(ref p, (uint)bytes);
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, 4), (ushort)(bytes >> 32));
                        break;

                    default:
                        Unsafe.WriteUnaligned(ref p, bytes);
                        break;
                }
            }
        }

        // Paeth for two rows of pixels of 2 to 4 bytes at once, the lower two pixels behind the
        // upper: the upper row's pixel in the vector's low lanes, the lower row's in the high
        // ones. A pixel's prediction waits for the pixel to its left, so a row is a chain of
        // dependent steps; two rows in one vector run two chains for the price of one. The
        // lower row's pixel above is the upper row's result from two steps before, and the one
        // above-left from three: two steps give that result time to reach the lower lanes
        // without holding up the chain.
        private static void PaethTwoRows(
            ref byte top,
            ref byte bottom,
            ref byte above,
            int length,
            int step)
        {
            var left = Vector128<short>.Zero;
            var upLeft = Vector128<short>.Zero;
            var byteMask = Vector128.Create((short)0xff);
            var topLanes = Vector128.Create(-1, -1, -1, -1, 0, 0, 0, 0).AsInt16();
            ulong lastTop = 0;
            ulong topBefore = 0;
            int pixels = length / step;
            for (int k = 0; k <= pixels + 1; k++)
            {
                // At k < 2 the lower row's pixel is before its first, and at k >= pixels the
                // upper row's after its last: computed but not stored. The reads stay inside the
                // rows' room before and after them.
                ref byte topPixel = ref Unsafe.Add(ref top, k * step);
                ref byte bottomPixel = ref Unsafe.Add(ref bottom, (k - 2) * step);
                ulong aboveBytes = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref above, k * step)) | (topBefore << 32);
                ulong rawBytes = Unsafe.ReadUnaligned<uint>(ref topPixel) | ((ulong)Unsafe.ReadUnaligned<uint>(ref bottomPixel) << 32);
                var up = Vector128.WidenLower(Vector128.CreateScalarUnsafe(aboveBytes).AsByte()).AsInt16();
                var raw = Vector128.WidenLower(Vector128.CreateScalarUnsafe(rawBytes).AsByte()).AsInt16();

                var upMinusUpLeft = up - upLeft;
                var leftMinusUpLeft = left - upLeft;
                var toLeft = Vector128.Abs(upMinusUpLeft);
                var toUp = Vector128.Abs(leftMinusUpLeft);
                var toUpLeft = Vector128.Abs(upMinusUpLeft + leftMinusUpLeft);
                var nearer = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(toUp, toUpLeft), up, upLeft);
                var leftNearest = Vector128.LessThanOrEqual(toLeft, toUp) & Vector128.LessThanOrEqual(toLeft, toUpLeft);
                left = (raw + Vector128.ConditionalSelect(leftNearest, left, nearer)) & byteMask;
                upLeft = up;

                ulong bytes = Vector128.Narrow(left.AsUInt16(), left.AsUInt16()).AsUInt64().ToScalar();
                topBefore = lastTop;
                lastTop = (uint)bytes;
                if (k < pixels)
                {
                    StorePixel(ref topPixel, (uint)bytes, step);
                }

                if (k >= 2)
                {
                    StorePixel(ref bottomPixel, (uint)(bytes >> 32), step);
                }
                else
                {
                    // The lower row's first pixel has nothing to its left
                    left &= topLanes;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StorePixel(
            ref byte pixel,
            uint bytes,
            int step)
        {
            if (step == 4)
            {
                Unsafe.WriteUnaligned(ref pixel, bytes);
            }
            else
            {
                Unsafe.WriteUnaligned(ref pixel, (ushort)bytes);
                if (step == 3)
                {
                    Unsafe.Add(ref pixel, 2) = (byte)(bytes >> 16);
                }
            }
        }

        // Adds four bytes to four, each wrapping on its own
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint AddBytes(
            uint x,
            uint y)
        {
            return ((x & 0x7f7f7f7f) + (y & 0x7f7f7f7f)) ^ ((x ^ y) & 0x80808080);
        }

        // Whichever of left, up and up-left is nearest left + up - up-left, preferring them in
        // that order. Without branches: on noisy images they'd be mispredicted half the time.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Paeth(
            int left,
            int up,
            int upLeft)
        {
            int toLeft = Abs(up - upLeft);
            int toUp = Abs(left - upLeft);
            int toUpLeft = Abs(left + up - upLeft - upLeft);

            // All ones where the first is at most the second (the values are small, so the
            // difference's sign says)
            int upNearer = ~((toUpLeft - toUp) >> 31);
            int leftNearest = ~((toUp - toLeft) >> 31) & ~((toUpLeft - toLeft) >> 31);
            int nearer = upLeft ^ ((up ^ upLeft) & upNearer);
            return nearer ^ ((left ^ nearer) & leftNearest);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Abs(
            int value)
        {
            int sign = value >> 31;
            return (value ^ sign) - sign;
        }

        // Converts a row's samples to the pixels from start, every step
        private void WriteRow(
            ReadOnlySpan<byte> row,
            Span<uint> pixels,
            int start,
            int step,
            int count)
        {
            // The pass's math keeps start + (count - 1) * step inside the image
            ref uint target = ref pixels[start];
            ref byte source = ref MemoryMarshal.GetReference(row);
            int depth = this.image.BitDepth;

            if (this.table is not null)
            {
                // The table has an entry for every byte, so a sample indexes it without a check;
                // a palette's index past its colors is an error
                ref uint table = ref MemoryMarshal.GetArrayDataReference(this.table);
                int tableCount = this.tableCount;
                if (depth == 8)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int index = Unsafe.Add(ref source, i);
                        if (index >= tableCount)
                        {
                            ThrowMissingColor(index);
                        }

                        Unsafe.Add(ref target, i * step) = Unsafe.Add(ref table, index);
                    }
                }
                else
                {
                    // Samples per byte as a power of two: 8 of 1 bit, 4 of 2, 2 of 4
                    int perByteShift = (depth == 1) ? 3 : ((depth == 2) ? 2 : 1);
                    int perByteMask = (1 << perByteShift) - 1;
                    int mask = (1 << depth) - 1;
                    for (int i = 0; i < count; i++)
                    {
                        int shift = 8 - depth - ((i & perByteMask) * depth);
                        int index = (Unsafe.Add(ref source, i >> perByteShift) >> shift) & mask;
                        if (index >= tableCount)
                        {
                            ThrowMissingColor(index);
                        }

                        Unsafe.Add(ref target, i * step) = Unsafe.Add(ref table, index);
                    }
                }

                return;
            }

            if (depth == 8)
            {
                switch (this.image.ColorType)
                {
                    case Rgba:
                        if (!this.premultiply && (step == 1))
                        {
                            Unsafe.CopyBlockUnaligned(ref Unsafe.As<uint, byte>(ref target), ref source, (uint)(count * 4));
                        }
                        else if (!this.premultiply)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                Unsafe.Add(ref target, i * step) = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, i * 4));
                            }
                        }
                        else
                        {
                            // Four pixels at a time: opaque ones, most of most images, as they are
                            int i = 0;
                            for (; i + 4 <= count; i += 4)
                            {
                                ref byte p = ref Unsafe.Add(ref source, i * 4);
                                uint p0 = Unsafe.ReadUnaligned<uint>(ref p);
                                uint p1 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 4));
                                uint p2 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 8));
                                uint p3 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 12));
                                if ((p0 & p1 & p2 & p3) < 0xff000000)
                                {
                                    p0 = Premultiply.Pixel(p0);
                                    p1 = Premultiply.Pixel(p1);
                                    p2 = Premultiply.Pixel(p2);
                                    p3 = Premultiply.Pixel(p3);
                                }

                                ref uint t = ref Unsafe.Add(ref target, i * step);
                                t = p0;
                                Unsafe.Add(ref t, step) = p1;
                                Unsafe.Add(ref t, step * 2) = p2;
                                Unsafe.Add(ref t, step * 3) = p3;
                            }

                            for (; i < count; i++)
                            {
                                Unsafe.Add(ref target, i * step) = Premultiply.Pixel(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, i * 4)));
                            }
                        }

                        return;

                    case Rgb:
                        if (!this.image.HasTransparentColor)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                ref byte p = ref Unsafe.Add(ref source, i * 3);
                                Unsafe.Add(ref target, i * step) = p | ((uint)Unsafe.Add(ref p, 1) << 8) | ((uint)Unsafe.Add(ref p, 2) << 16) | 0xff000000;
                            }

                            return;
                        }

                        break;

                    case GrayscaleAlpha:
                        for (int i = 0; i < count; i++)
                        {
                            uint gray = Unsafe.Add(ref source, i * 2);
                            uint a = Unsafe.Add(ref source, (i * 2) + 1);
                            if (this.premultiply)
                            {
                                // (gray * a + 127) / 255, as Premultiply.Pixel divides
                                uint t = (gray * a) + 127;
                                gray = (t + 1 + (t >> 8)) >> 8;
                            }

                            Unsafe.Add(ref target, i * step) = (gray * 0x010101u) | (a << 24);
                        }

                        return;
                }
            }

            // 16-bit samples keep their high byte, the first
            if (depth == 16)
            {
                switch (this.image.ColorType)
                {
                    case Rgba:
                        for (int i = 0; i < count; i++)
                        {
                            ulong p = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i * 8));
                            uint pixel = (uint)(p & 0xff) | (uint)((p >> 8) & 0xff00) | (uint)((p >> 16) & 0xff0000) | (uint)((p >> 24) & 0xff000000);
                            Unsafe.Add(ref target, i * step) = this.premultiply ? Premultiply.Pixel(pixel) : pixel;
                        }

                        return;

                    case Rgb when !this.image.HasTransparentColor:
                        for (int i = 0; i < count; i++)
                        {
                            ref byte p = ref Unsafe.Add(ref source, i * 6);
                            Unsafe.Add(ref target, i * step) = p | ((uint)Unsafe.Add(ref p, 2) << 8) | ((uint)Unsafe.Add(ref p, 4) << 16) | 0xff000000;
                        }

                        return;
                }
            }

            // Everything else (a transparent color, 16-bit gray), a sample at a time
            for (int i = 0; i < count; i++)
            {
                uint pixel = ReadPixel(this.image, row, i);
                Unsafe.Add(ref target, i * step) = this.premultiply ? Premultiply.Pixel(pixel) : pixel;
            }
        }

        private static void ThrowMissingColor(
            int index)
        {
            throw new InvalidDataException($"The PNG image uses a color its palette doesn't have ({index}).");
        }
    }

    private static uint ReadPixel(
        in ImageInfo image,
        ReadOnlySpan<byte> row,
        int column)
    {
        int depth = image.BitDepth;
        int sample = column * image.Channels;
        switch (image.ColorType)
        {
            case Grayscale:
                {
                    int gray = ReadSample(row, sample, depth);
                    byte value = ToByte(gray, depth);
                    byte a = (gray == image.TransparentGray) ? (byte)0 : (byte)255;
                    return Pack(value, value, value, a);
                }

            case Rgb:
                {
                    int r = ReadSample(row, sample, depth);
                    int g = ReadSample(row, sample + 1, depth);
                    int b = ReadSample(row, sample + 2, depth);
                    bool transparent = image.HasTransparentColor && (r == image.TransparentRed) && (g == image.TransparentGreen) && (b == image.TransparentBlue);
                    return Pack(ToByte(r, depth), ToByte(g, depth), ToByte(b, depth), transparent ? (byte)0 : (byte)255);
                }

            case Indexed:
                {
                    int index = ReadSample(row, sample, depth);
                    var palette = image.Palette!;
                    if ((index * 3) + 2 >= palette.Length)
                    {
                        throw new InvalidDataException($"The PNG image uses a color its palette doesn't have ({index}).");
                    }

                    var paletteAlpha = image.PaletteAlpha;
                    byte a = ((paletteAlpha is not null) && (index < paletteAlpha.Length)) ? paletteAlpha[index] : (byte)255;
                    return Pack(palette[index * 3], palette[(index * 3) + 1], palette[(index * 3) + 2], a);
                }

            case GrayscaleAlpha:
                {
                    byte value = ToByte(ReadSample(row, sample, depth), depth);
                    return Pack(value, value, value, ToByte(ReadSample(row, sample + 1, depth), depth));
                }

            default:
                return Pack(
                    ToByte(ReadSample(row, sample, depth), depth),
                    ToByte(ReadSample(row, sample + 1, depth), depth),
                    ToByte(ReadSample(row, sample + 2, depth), depth),
                    ToByte(ReadSample(row, sample + 3, depth), depth));
        }
    }

    // A sample at its bit depth: samples smaller than a byte are packed from the high bits
    private static int ReadSample(
        ReadOnlySpan<byte> row,
        int index,
        int depth)
    {
        switch (depth)
        {
            case 8:
                return row[index];

            case 16:
                return (row[index * 2] << 8) | row[(index * 2) + 1];

            default:
                int bit = index * depth;
                int shift = 8 - depth - (bit & 7);
                return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
        }
    }

    // A sample scaled to a byte: small depths spread over 0 to 255, 16 bits keeps its high byte
    private static byte ToByte(
        int sample,
        int depth)
    {
        return depth switch
        {
            8 => (byte)sample,
            16 => (byte)(sample >> 8),
            _ => (byte)((sample * 255) / ((1 << depth) - 1)),
        };
    }
}
