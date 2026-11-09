// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/vp8l_dec.c: DecodeImageData, DecodeAlphaData and the rows'
// output), modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2012 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LDecoder
{
    private sealed partial class Decoding
    {
        // libwebp's DecodeImageData: the pixels of an image stream (the level-0 image's then put out,
        // transformed, 16 rows at a time). The tables' indices are in range by
        // construction (a group index is under the groups' count, a code's table is complete), so
        // they're read without bounds checks.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void DecodeImageData(
            uint[] image,
            int w,
            int h,
            int lastRowToDecode,
            OutputKind output,
            Span<byte> rgba)
        {
            var bytes = data;
            var reader = this.br;
            var meta = this.hdr;
            ref var codes = ref MemoryMarshal.GetArrayDataReference(meta.Tables.Codes);
            ref var packed = ref MemoryMarshal.GetArrayDataReference(meta.Packed);
            ref var groups = ref MemoryMarshal.GetArrayDataReference(meta.Groups);
            ref uint pixels = ref MemoryMarshal.GetArrayDataReference(image);
            uint[]? cache = (meta.ColorCacheSize > 0) ? meta.ColorCache : null;
            int cacheShift = meta.ColorCacheShift;
            int mask = meta.HuffmanMask;
            const int lengthCodeLimit = NumLiteralCodes + NumLengthCodes;
            int colorCacheLimit = lengthCodeLimit + meta.ColorCacheSize;
            bool emits = output != OutputKind.None;

            int row = 0;
            int col = 0;
            int src = 0;
            int lastCached = 0;
            int srcEnd = w * h;
            int srcLast = w * lastRowToDecode;
            int groupIndex = (src < srcLast) ? MetaIndex(meta, col, row) : 0;

            // The pixels far from the stream's end, then the rest with every check
            var state = new LoopState { Reader = reader, Src = src, Row = row, Col = col, LastCached = lastCached, GroupIndex = groupIndex };
            state = this.DecodeFast(image, w, srcEnd, srcLast, state);
            reader = state.Reader;
            src = state.Src;
            row = state.Row;
            col = state.Col;
            lastCached = state.LastCached;
            groupIndex = state.GroupIndex;
            while (src < srcLast)
            {
                if ((col & mask) == 0)
                {
                    groupIndex = MetaIndex(meta, col, row);
                }

                ref var group = ref Unsafe.Add(ref groups, groupIndex);
                if (group.IsTrivialCode)
                {
                    Unsafe.Add(ref pixels, src) = group.LiteralArb;
                }
                else
                {
                    reader.Fill(bytes);
                    int code;
                    if (group.UsePackedTable)
                    {
                        ref var packedEntry = ref Unsafe.Add(ref packed, (groupIndex * PackedTableSize) + (int)(reader.Prefetch() & (PackedTableSize - 1)));
                        int entryBits = packedEntry.Bits;
                        uint entryValue = packedEntry.Value;
                        if (entryBits < BitsSpecialMarker)
                        {
                            reader.BitPos += entryBits;
                            Unsafe.Add(ref pixels, src) = entryValue;
                            if (reader.IsEndOfStream())
                            {
                                break;
                            }

                            goto AdvanceByOne;
                        }

                        reader.BitPos += entryBits - BitsSpecialMarker;
                        code = (int)entryValue;
                    }
                    else
                    {
                        code = ReadSymbol(ref codes, group.Green, ref reader);
                    }

                    if (reader.IsEndOfStream())
                    {
                        break;
                    }

                    if (code < NumLiteralCodes)
                    {
                        if (group.IsTrivialLiteral)
                        {
                            Unsafe.Add(ref pixels, src) = group.LiteralArb | ((uint)code << 8);
                        }
                        else
                        {
                            uint red = (uint)ReadSymbol(ref codes, group.Red, ref reader);
                            reader.Fill(bytes);
                            uint blue = (uint)ReadSymbol(ref codes, group.Blue, ref reader);
                            uint alpha = (uint)ReadSymbol(ref codes, group.Alpha, ref reader);
                            if (reader.IsEndOfStream())
                            {
                                break;
                            }

                            Unsafe.Add(ref pixels, src) = (alpha << 24) | (red << 16) | ((uint)code << 8) | blue;
                        }
                    }
                    else if (code < lengthCodeLimit)
                    {
                        // A copy of earlier pixels
                        int length = CopyDistance(code - NumLiteralCodes, bytes, ref reader);
                        int distSymbol = ReadSymbol(ref codes, group.Dist, ref reader);
                        reader.Fill(bytes);
                        int distCode = CopyDistance(distSymbol, bytes, ref reader);
                        int dist = PlaneCodeToDistance(w, distCode);
                        if (reader.IsEndOfStream())
                        {
                            break;
                        }

                        if ((src < dist) || (srcEnd - src < length))
                        {
                            throw Damaged();
                        }

                        CopyBlock(ref Unsafe.Add(ref pixels, src), dist, length);
                        src += length;
                        col += length;
                        while (col >= w)
                        {
                            col -= w;
                            row++;
                        }

                        if ((col & mask) != 0)
                        {
                            groupIndex = MetaIndex(meta, col, row);
                        }

                        if (cache is not null)
                        {
                            InsertIntoCache(cache, cacheShift, ref pixels, ref lastCached, src);
                        }

                        continue;
                    }
                    else if (code < colorCacheLimit)
                    {
                        // A color from the cache, which takes every pixel decoded before it
                        InsertIntoCache(cache!, cacheShift, ref pixels, ref lastCached, src);
                        Unsafe.Add(ref pixels, src) = cache![code - lengthCodeLimit];
                    }
                    else
                    {
                        throw Damaged();
                    }
                }

            AdvanceByOne:
                src++;
                col++;
                if (col >= w)
                {
                    col = 0;
                    row++;
                    if (cache is not null)
                    {
                        InsertIntoCache(cache, cacheShift, ref pixels, ref lastCached, src);
                    }
                }
            }

            reader.Eos = reader.IsEndOfStream();
            this.br = reader;
            if (reader.Eos)
            {
                throw Damaged();
            }

            // The rows put out 16 at a time, as libwebp does as it decodes (each transform reads only the
            // rows before, so they're the same after)
            if (emits)
            {
                int last = (row > lastRowToDecode) ? lastRowToDecode : row;
                for (int r = NumArgbCacheRows; r < last; r += NumArgbCacheRows)
                {
                    this.ProcessRows(r, output, rgba);
                }

                this.ProcessRows(last, output, rgba);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void InsertIntoCache(
            uint[] cache,
            int shift,
            ref uint pixels,
            ref int lastCached,
            int src)
        {
            ref uint colors = ref MemoryMarshal.GetArrayDataReference(cache);
            int i = lastCached;
            while (i < src)
            {
                uint argb = Unsafe.Add(ref pixels, i++);
                Unsafe.Add(ref colors, (int)((argb * HashMultiplier) >> shift)) = argb;
            }

            lastCached = i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MetaIndex(
            Metadata meta,
            int x,
            int y)
        {
            int bits = meta.HuffmanSubsampleBits;
            if (bits == 0)
            {
                return 0;
            }

            return (int)meta.HuffmanImage![(meta.HuffmanXSize * (y >> bits)) + (x >> bits)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadSymbol(
            ref Vp8LHuffmanCode codes,
            int table,
            ref Vp8LBitReader reader)
        {
            // An entry as one word: its bits in the low byte, its value in the high half
            uint value = reader.Prefetch();
            int index = table + (int)(value & Vp8LHuffmanTables.TableMask);
            uint entry = Unsafe.As<Vp8LHuffmanCode, uint>(ref Unsafe.Add(ref codes, index));
            int extra = (int)(entry & 0xff) - Vp8LHuffmanTables.TableBits;
            if (extra > 0)
            {
                // A second-level table, for the code's bits past the root's 8
                reader.BitPos += Vp8LHuffmanTables.TableBits;
                value = reader.Prefetch();
                index += (int)(entry >> 16) + (int)(value & ((1u << extra) - 1));
                entry = Unsafe.As<Vp8LHuffmanCode, uint>(ref Unsafe.Add(ref codes, index));
            }

            reader.BitPos += (int)(entry & 0xff);
            return (int)(entry >> 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CopyDistance(
            int symbol,
            byte[] bytes,
            ref Vp8LBitReader reader)
        {
            if (symbol < 4)
            {
                return symbol + 1;
            }

            int extraBits = (symbol - 2) >> 1;
            int offset = (2 + (symbol & 1)) << extraBits;
            return offset + (int)reader.ReadBits(bytes, extraBits) + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PlaneCodeToDistance(
            int xsize,
            int planeCode)
        {
            if (planeCode > CodeToPlane.Length)
            {
                return planeCode - CodeToPlane.Length;
            }

            int distCode = CodeToPlane[planeCode - 1];
            int yoffset = distCode >> 4;
            int xoffset = 8 - (distCode & 0xf);
            int dist = (yoffset * xsize) + xoffset;
            return (dist >= 1) ? dist : 1;
        }

        // A copy of earlier pixels, overlapping as it goes when the distance is under the length:
        // four at a time where the copy doesn't overlap within them
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyBlock(
            ref uint target,
            int dist,
            int length)
        {
            ref uint source = ref Unsafe.Subtract(ref target, dist);
            int i = 0;
            if (dist >= length)
            {
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<uint, byte>(ref target), ref Unsafe.As<uint, byte>(ref source), (uint)length * 4);
                return;
            }

            if (dist == 1)
            {
                MemoryMarshal.CreateSpan(ref target, length).Fill(source);
                return;
            }

            if (dist >= 4)
            {
                for (; i + 4 <= length; i += 4)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref target, i)), Unsafe.ReadUnaligned<UInt128>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref source, i))));
                }
            }

            for (; i < length; i++)
            {
                Unsafe.Add(ref target, i) = Unsafe.Add(ref source, i);
            }
        }

        // Rows of the level-0 image from the last put out to 'row': transformed, and to RGBA or alpha
        private void ProcessRows(
            int row,
            OutputKind output,
            Span<byte> rgba)
        {
            if (output == OutputKind.Alpha)
            {
                this.ExtractAlphaRows(row);
                return;
            }

            int numRows = row - this.lastRow;
            if ((numRows > 0) && (this.nextTransform > 0) && (this.transforms[0].Type == SubtractGreenTransform))
            {
                // Subtract-green applied last: the other transforms to the cache (if any), then green
                // added back and the pixels converted to RGBA in one pass to the output
                int input = this.width * this.lastRow;
                int n = this.nextTransform;
                int endRow = this.lastRow + numRows;
                while (n-- > 1)
                {
                    this.InverseTransform(ref this.transforms[n], this.lastRow, endRow, input, this.argbCache);
                    input = this.argbCache;
                }

                int count = this.ioWidth * numRows;
                var target = MemoryMarshal.Cast<byte, uint>(rgba.Slice(this.lastOutRow * this.ioWidth * 4, count * 4));
                AddGreenToBlueAndRed(this.pixels.AsSpan(input, count), target, toRgba: true);
                this.lastOutRow += numRows;
            }
            else if ((numRows > 0) && (this.nextTransform > 0) && (this.transforms[0].Type == ColorIndexingTransform))
            {
                // A palette applied last: the other transforms to the cache (if any), then its indices
                // straight to the output, its colors RGBA already
                int input = this.width * this.lastRow;
                int n = this.nextTransform;
                int endRow = this.lastRow + numRows;
                while (n-- > 1)
                {
                    this.InverseTransform(ref this.transforms[n], this.lastRow, endRow, input, this.argbCache);
                    input = this.argbCache;
                }

                ref var palette = ref this.transforms[0];
                var target = MemoryMarshal.Cast<byte, uint>(rgba.Slice(this.lastOutRow * this.ioWidth * 4, this.ioWidth * numRows * 4));
                MapColors(ref palette, this.lastRow, endRow, ref this.pixels[input], ref MemoryMarshal.GetReference(target), palette.RgbaColors!, ref palette.ExpandedRgba);
                this.lastOutRow += numRows;
            }
            else if (numRows > 0)
            {
                this.ApplyInverseTransforms(this.lastRow, numRows, this.width * this.lastRow);
                int count = this.ioWidth * numRows;
                var source = this.pixels.AsSpan(this.argbCache, count);
                var target = MemoryMarshal.Cast<byte, uint>(rgba.Slice(this.lastOutRow * this.ioWidth * 4, count * 4));
                ConvertBgraToRgba(source, target);
                this.lastOutRow += numRows;
            }

            this.lastRow = row;
        }

        // libwebp's ApplyInverseTransforms: the transforms in reverse, the first from the decoded rows
        // to the cache, the rest in the cache
        private void ApplyInverseTransforms(
            int startRow,
            int numRows,
            int rowsIn)
        {
            int n = this.nextTransform;
            int endRow = startRow + numRows;
            int input = rowsIn;
            int output = this.argbCache;
            while (n-- > 0)
            {
                this.InverseTransform(ref this.transforms[n], startRow, endRow, input, output);
                input = output;
            }

            if (input != output)
            {
                Array.Copy(this.pixels, input, this.pixels, output, this.width * numRows);
            }
        }

        // The alpha of 32-bit image rows (its green), unfiltered, 16 rows at a time
        private void ExtractAlphaRows(
            int last)
        {
            int current = this.lastRow;
            int numRows = last - current;
            int input = this.width * current;
            var plane = this.alphaPlane!;
            while (numRows > 0)
            {
                int rows = (numRows > NumArgbCacheRows) ? NumArgbCacheRows : numRows;
                int w = this.ioWidth;
                int count = w * rows;
                this.ApplyInverseTransforms(current, rows, input);
                // Each pixel's green: 16 at a time, narrowed
                ref uint source = ref this.pixels[this.argbCache];
                ref byte target = ref plane[w * current];
                int i = 0;
                for (; i + 16 <= count; i += 16)
                {
                    var a = Vector128.Narrow(Vector128.LoadUnsafe(ref source, (nuint)i) >> 8, Vector128.LoadUnsafe(ref source, (nuint)(i + 4)) >> 8);
                    var b = Vector128.Narrow(Vector128.LoadUnsafe(ref source, (nuint)(i + 8)) >> 8, Vector128.LoadUnsafe(ref source, (nuint)(i + 12)) >> 8);
                    Vector128.Narrow(a & Vector128.Create((ushort)0xff), b & Vector128.Create((ushort)0xff)).StoreUnsafe(ref target, (nuint)i);
                }

                for (; i < count; i++)
                {
                    Unsafe.Add(ref target, i) = (byte)(Unsafe.Add(ref source, i) >> 8);
                }

                this.AlphaApplyFilter(current, current + rows, w * current, w);
                numRows -= rows;
                input += rows * this.width;
                current += rows;
            }

            this.lastRow = this.lastOutRow = last;
        }

        private void AlphaApplyFilter(
            int firstRow,
            int lastRowExclusive,
            int output,
            int stride)
        {
            if (this.alphaFilter == 0)
            {
                return;
            }

            ref byte plane = ref MemoryMarshal.GetArrayDataReference(this.alphaPlane!);
            int previous = this.alphaPrevLine;
            for (int y = firstRow; y < lastRowExclusive; y++)
            {
                ref byte row = ref Unsafe.Add(ref plane, output);
                AlphaDecoder.Unfilter(this.alphaFilter, previous >= 0, ref Unsafe.Add(ref plane, Math.Max(previous, 0)), ref row, ref row, stride);
                previous = output;
                output += stride;
            }

            this.alphaPrevLine = previous;
        }

        // libwebp's DecodeAlphaData: alpha whose only transform is a palette, its indices a byte each
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void DecodeAlphaData(
            byte[] indices,
            int w,
            int h,
            int lastRowToDecode)
        {
            var bytes = data;
            var reader = this.br;
            var meta = this.hdr;
            ref var codes = ref MemoryMarshal.GetArrayDataReference(meta.Tables.Codes);
            ref var groups = ref MemoryMarshal.GetArrayDataReference(meta.Groups);
            int mask = meta.HuffmanMask;
            const int lengthCodeLimit = NumLiteralCodes + NumLengthCodes;
            int row = 0;
            int col = 0;
            int pos = 0;
            int end = w * h;
            int last = w * lastRowToDecode;
            int groupIndex = (pos < last) ? MetaIndex(meta, col, row) : 0;
            bool ok = true;
            while (!reader.Eos && (pos < last))
            {
                if ((col & mask) == 0)
                {
                    groupIndex = MetaIndex(meta, col, row);
                }

                ref var group = ref Unsafe.Add(ref groups, groupIndex);
                reader.Fill(bytes);
                int code = ReadSymbol(ref codes, group.Green, ref reader);
                if (code < NumLiteralCodes)
                {
                    indices[pos] = (byte)code;
                    pos++;
                    col++;
                    if (col >= w)
                    {
                        col = 0;
                        row++;
                        if ((row <= lastRowToDecode) && ((row % NumArgbCacheRows) == 0))
                        {
                            this.ExtractPalettedAlphaRows(indices, row);
                        }
                    }
                }
                else if (code < lengthCodeLimit)
                {
                    int length = CopyDistance(code - NumLiteralCodes, bytes, ref reader);
                    int distSymbol = ReadSymbol(ref codes, group.Dist, ref reader);
                    reader.Fill(bytes);
                    int distCode = CopyDistance(distSymbol, bytes, ref reader);
                    int dist = PlaneCodeToDistance(w, distCode);
                    if ((pos >= dist) && (end - pos >= length))
                    {
                        for (int i = 0; i < length; i++)
                        {
                            indices[pos + i] = indices[pos + i - dist];
                        }
                    }
                    else
                    {
                        ok = false;
                        break;
                    }

                    pos += length;
                    col += length;
                    while (col >= w)
                    {
                        col -= w;
                        row++;
                        if ((row <= lastRowToDecode) && ((row % NumArgbCacheRows) == 0))
                        {
                            this.ExtractPalettedAlphaRows(indices, row);
                        }
                    }

                    if ((pos < last) && ((col & mask) != 0))
                    {
                        groupIndex = MetaIndex(meta, col, row);
                    }
                }
                else
                {
                    ok = false;
                    break;
                }

                reader.Eos = reader.IsEndOfStream();
            }

            if (ok)
            {
                this.ExtractPalettedAlphaRows(indices, (row > lastRowToDecode) ? lastRowToDecode : row);
            }

            reader.Eos = reader.IsEndOfStream();
            this.br = reader;
            if (!ok || (reader.Eos && (pos < end)))
            {
                throw Damaged();
            }
        }

        // Paletted alpha rows: each index's color's green, unfiltered
        private void ExtractPalettedAlphaRows(
            byte[] indices,
            int last)
        {
            int firstRow = this.lastRow;
            if (last > firstRow)
            {
                int w = this.ioWidth;
                int output = w * firstRow;
                ref var transform = ref this.transforms[0];
                MapAlpha(ref transform, firstRow, last, indices, this.width * firstRow, this.alphaPlane!, output);
                this.AlphaApplyFilter(firstRow, last, output, w);
            }

            this.lastRow = this.lastOutRow = last;
        }
    }
}
