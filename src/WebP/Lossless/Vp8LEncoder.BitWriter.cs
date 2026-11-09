// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/utils/bit_writer_utils.c, the VP8L part, and
// src/utils/huffman_encode_utils.c), modified by Hot Chai Games LLC: written in C#. libwebp carries this
// notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    /// <summary>
    /// The VP8L bit writer: bits least significant first, in bytes of increasing address, gathered
    /// in 64 bits and written 32 at a time (libwebp's 64-bit build).
    /// </summary>
    internal sealed class BitWriter
    {
        private byte[] buffer;
        private int count;
        private ulong bits;
        private int used;

        public BitWriter(
            int capacity)
        {
            this.buffer = new byte[Math.Max(capacity, 64)];
        }

        /// <summary>
        /// The writer's pending bits in locals, for a loop of many writes: <see cref="End"/> gives
        /// them back. The writer isn't used in between.
        /// </summary>
        public ref struct Sink
        {
            private readonly BitWriter writer;
            private ulong bits;
            private int used;

            public Sink(
                BitWriter writer)
            {
                this.writer = writer;
                this.bits = writer.bits;
                this.used = writer.used;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Put(
                uint value,
                int numBits)
            {
                if (numBits > 0)
                {
                    if (this.used >= 32)
                    {
                        this.writer.WriteWord((uint)this.bits);
                        this.bits >>= 32;
                        this.used -= 32;
                    }

                    this.bits |= (ulong)value << this.used;
                    this.used += numBits;
                }
            }

            public readonly void End()
            {
                this.writer.bits = this.bits;
                this.writer.used = this.used;
            }
        }

        private void WriteWord(
            uint word)
        {
            if (this.count + 4 > this.buffer.Length)
            {
                Array.Resize(ref this.buffer, Math.Max(this.buffer.Length * 2, this.count + 32768));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(this.buffer.AsSpan(this.count), word);
            this.count += 4;
        }

        /// <summary>The bytes written, counting the bits not yet flushed (VP8LBitWriterNumBytes).</summary>
        public int NumBytes => this.count + ((this.used + 7) >> 3);

        /// <summary>Where the writer is, to come back to with <see cref="Reset"/>.</summary>
        public (int Count, ulong Bits, int Used) Mark => (this.count, this.bits, this.used);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutBits(
            uint value,
            int numBits)
        {
            if (numBits > 0)
            {
                if (this.used >= 32)
                {
                    this.Flush();
                }

                this.bits |= (ulong)value << this.used;
                this.used += numBits;
            }
        }

        private void Flush()
        {
            if (this.count + 4 > this.buffer.Length)
            {
                Array.Resize(ref this.buffer, Math.Max(this.buffer.Length * 2, this.count + 32768));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(this.buffer.AsSpan(this.count), (uint)this.bits);
            this.count += 4;
            this.bits >>= 32;
            this.used -= 32;
        }

        /// <summary>Back to a mark (VP8LBitWriterReset).</summary>
        public void Reset(
            (int Count, ulong Bits, int Used) mark)
        {
            this.count = mark.Count;
            this.bits = mark.Bits;
            this.used = mark.Used;
        }

        /// <summary>This writer's bytes and bits copied into <paramref name="destination"/> (VP8LBitWriterClone).</summary>
        public void CloneTo(
            BitWriter destination)
        {
            if (destination.buffer.Length < this.count)
            {
                destination.buffer = new byte[this.buffer.Length];
            }

            this.buffer.AsSpan(0, this.count).CopyTo(destination.buffer);
            destination.count = this.count;
            destination.bits = this.bits;
            destination.used = this.used;
        }

        /// <summary>Swaps two writers' contents (VP8LBitWriterSwap).</summary>
        public static void Swap(
            BitWriter a,
            BitWriter b)
        {
            (a.buffer, b.buffer) = (b.buffer, a.buffer);
            (a.count, b.count) = (b.count, a.count);
            (a.bits, b.bits) = (b.bits, a.bits);
            (a.used, b.used) = (b.used, a.used);
        }

        /// <summary>Flushes the bits left and returns the bytes (VP8LBitWriterFinish).</summary>
        public ReadOnlySpan<byte> Finish()
        {
            if (this.count + 8 > this.buffer.Length)
            {
                Array.Resize(ref this.buffer, this.count + 8);
            }

            while (this.used > 0)
            {
                this.buffer[this.count++] = (byte)this.bits;
                this.bits >>= 8;
                this.used -= 8;
            }

            this.used = 0;
            return this.buffer.AsSpan(0, this.count);
        }
    }

    //------------------------------------------------------------------------------
    // Huffman codes (huffman_encode_utils.c)

    private const int MaxAllowedCodeLength = 15;
    private const int CodeLengthCodes = 19;

    private struct HuffmanTreeToken
    {
        public byte Code;
        public byte ExtraBits;
    }

    private sealed class HuffmanTreeCode
    {
        public int NumSymbols;
        public byte[] CodeLengths = [];
        public ushort[] Codes = [];
    }

    private struct HuffmanTree
    {
        public uint TotalCount;
        public int Value;
        public int PoolIndexLeft;
        public int PoolIndexRight;
    }

    private static void OptimizeHuffmanForRle(
        int length,
        Span<byte> goodForRle,
        Span<uint> counts)
    {
        // 1) Trailing zeros don't count
        for (; length >= 0; --length)
        {
            if (length == 0)
            {
                return;
            }

            if (counts[length - 1] != 0)
            {
                break;
            }
        }

        // 2) Mark the runs that already encode well: zeros longer than 5, others longer than 7
        {
            uint symbol = counts[0];
            int stride = 0;
            for (int i = 0; i < length + 1; i++)
            {
                if ((i == length) || (counts[i] != symbol))
                {
                    if (((symbol == 0) && (stride >= 5)) || ((symbol != 0) && (stride >= 7)))
                    {
                        for (int k = 0; k < stride; k++)
                        {
                            goodForRle[i - k - 1] = 1;
                        }
                    }

                    stride = 1;
                    if (i != length)
                    {
                        symbol = counts[i];
                    }
                }
                else
                {
                    ++stride;
                }
            }
        }

        // 3) Replace the counts that lead to more run-length codes
        {
            uint stride = 0;
            uint limit = counts[0];
            uint sum = 0;
            for (int i = 0; i < length + 1; i++)
            {
                if ((i == length) || (goodForRle[i] != 0) || ((i != 0) && (goodForRle[i - 1] != 0)) || !(Math.Abs((int)counts[i] - (int)limit) < 4))
                {
                    if ((stride >= 4) || ((stride >= 3) && (sum == 0)))
                    {
                        uint count = (sum + (stride / 2)) / stride;
                        if (count < 1)
                        {
                            count = 1;
                        }

                        if (sum == 0)
                        {
                            count = 0;
                        }

                        for (uint k = 0; k < stride; k++)
                        {
                            counts[(int)(i - k - 1)] = count;
                        }
                    }

                    stride = 0;
                    sum = 0;
                    if (i < length - 3)
                    {
                        limit = (counts[i] + counts[i + 1] + counts[i + 2] + counts[i + 3] + 2) / 4;
                    }
                    else if (i < length)
                    {
                        limit = counts[i];
                    }
                    else
                    {
                        limit = 0;
                    }
                }

                ++stride;
                if (i != length)
                {
                    sum += counts[i];
                    if (stride >= 4)
                    {
                        limit = (sum + (stride / 2)) / stride;
                    }
                }
            }
        }
    }

    private static void SetBitDepths(
        in HuffmanTree tree,
        ReadOnlySpan<HuffmanTree> pool,
        Span<byte> bitDepths,
        int level)
    {
        if (tree.PoolIndexLeft >= 0)
        {
            SetBitDepths(pool[tree.PoolIndexLeft], pool, bitDepths, level + 1);
            SetBitDepths(pool[tree.PoolIndexRight], pool, bitDepths, level + 1);
        }
        else
        {
            bitDepths[tree.Value] = (byte)level;
        }
    }

    // Sorts by count, highest first, then by value (CompareHuffmanTrees; the values are unique)
    private static void SortTrees(
        Span<HuffmanTree> trees)
    {
        trees.Sort(static (a, b) => (a.TotalCount > b.TotalCount) ? -1 : (a.TotalCount < b.TotalCount) ? 1 : a.Value.CompareTo(b.Value));
    }

    private static void GenerateOptimalTree(
        ReadOnlySpan<uint> histogram,
        Span<HuffmanTree> tree,
        int treeDepthLimit,
        Span<byte> bitDepths)
    {
        int histogramSize = histogram.Length;
        int treeSizeOrig = 0;
        for (int i = 0; i < histogramSize; i++)
        {
            if (histogram[i] != 0)
            {
                ++treeSizeOrig;
            }
        }

        if (treeSizeOrig == 0)
        {
            return;
        }

        var treePool = tree.Slice(treeSizeOrig);
        for (uint countMin = 1; ; countMin *= 2)
        {
            int treeSize = treeSizeOrig;
            int idx = 0;
            for (int j = 0; j < histogramSize; j++)
            {
                if (histogram[j] != 0)
                {
                    uint count = (histogram[j] < countMin) ? countMin : histogram[j];
                    tree[idx].TotalCount = count;
                    tree[idx].Value = j;
                    tree[idx].PoolIndexLeft = -1;
                    tree[idx].PoolIndexRight = -1;
                    ++idx;
                }
            }

            SortTrees(tree.Slice(0, treeSize));
            if (treeSize > 1)
            {
                int treePoolSize = 0;
                while (treeSize > 1)
                {
                    treePool[treePoolSize++] = tree[treeSize - 1];
                    treePool[treePoolSize++] = tree[treeSize - 2];
                    uint count = treePool[treePoolSize - 1].TotalCount + treePool[treePoolSize - 2].TotalCount;
                    treeSize -= 2;
                    int k;
                    for (k = 0; k < treeSize; k++)
                    {
                        if (tree[k].TotalCount <= count)
                        {
                            break;
                        }
                    }

                    tree.Slice(k, treeSize - k).CopyTo(tree.Slice(k + 1));
                    tree[k].TotalCount = count;
                    tree[k].Value = -1;
                    tree[k].PoolIndexLeft = treePoolSize - 1;
                    tree[k].PoolIndexRight = treePoolSize - 2;
                    treeSize++;
                }

                SetBitDepths(tree[0], treePool, bitDepths, 0);
            }
            else if (treeSize == 1)
            {
                bitDepths[tree[0].Value] = 1;
            }

            int maxDepth = bitDepths[0];
            for (int j = 1; j < histogramSize; j++)
            {
                if (maxDepth < bitDepths[j])
                {
                    maxDepth = bitDepths[j];
                }
            }

            if (maxDepth <= treeDepthLimit)
            {
                break;
            }
        }
    }

    private static int CodeRepeatedValues(
        int repetitions,
        Span<HuffmanTreeToken> tokens,
        int t,
        int value,
        int previousValue)
    {
        if (value != previousValue)
        {
            tokens[t].Code = (byte)value;
            tokens[t].ExtraBits = 0;
            ++t;
            --repetitions;
        }

        while (repetitions >= 1)
        {
            if (repetitions < 3)
            {
                for (int i = 0; i < repetitions; i++)
                {
                    tokens[t].Code = (byte)value;
                    tokens[t].ExtraBits = 0;
                    ++t;
                }

                break;
            }
            else if (repetitions < 7)
            {
                tokens[t].Code = 16;
                tokens[t].ExtraBits = (byte)(repetitions - 3);
                ++t;
                break;
            }
            else
            {
                tokens[t].Code = 16;
                tokens[t].ExtraBits = 3;
                ++t;
                repetitions -= 6;
            }
        }

        return t;
    }

    private static int CodeRepeatedZeros(
        int repetitions,
        Span<HuffmanTreeToken> tokens,
        int t)
    {
        while (repetitions >= 1)
        {
            if (repetitions < 3)
            {
                for (int i = 0; i < repetitions; i++)
                {
                    tokens[t].Code = 0;
                    tokens[t].ExtraBits = 0;
                    ++t;
                }

                break;
            }
            else if (repetitions < 11)
            {
                tokens[t].Code = 17;
                tokens[t].ExtraBits = (byte)(repetitions - 3);
                ++t;
                break;
            }
            else if (repetitions < 139)
            {
                tokens[t].Code = 18;
                tokens[t].ExtraBits = (byte)(repetitions - 11);
                ++t;
                break;
            }
            else
            {
                tokens[t].Code = 18;
                tokens[t].ExtraBits = 0x7f;
                ++t;
                repetitions -= 138;
            }
        }

        return t;
    }

    private static int CreateCompressedHuffmanTree(
        HuffmanTreeCode tree,
        Span<HuffmanTreeToken> tokens)
    {
        int depthSize = tree.NumSymbols;
        int previousValue = 8;
        int i = 0;
        int t = 0;
        while (i < depthSize)
        {
            int value = tree.CodeLengths[i];
            int k = i + 1;
            while ((k < depthSize) && (tree.CodeLengths[k] == value))
            {
                ++k;
            }

            int runs = k - i;
            if (value == 0)
            {
                t = CodeRepeatedZeros(runs, tokens, t);
            }
            else
            {
                t = CodeRepeatedValues(runs, tokens, t, value, previousValue);
                previousValue = value;
            }

            i += runs;
        }

        return t;
    }

    private static uint ReverseBits(
        int numBits,
        uint bits)
    {
        ReadOnlySpan<byte> reversed = [0x0, 0x8, 0x4, 0xc, 0x2, 0xa, 0x6, 0xe, 0x1, 0x9, 0x5, 0xd, 0x3, 0xb, 0x7, 0xf];
        uint result = 0;
        int i = 0;
        while (i < numBits)
        {
            i += 4;
            result |= (uint)reversed[(int)(bits & 0xf)] << (MaxAllowedCodeLength + 1 - i);
            bits >>= 4;
        }

        result >>= MaxAllowedCodeLength + 1 - numBits;
        return result;
    }

    private static void ConvertBitDepthsToSymbols(
        HuffmanTreeCode tree)
    {
        Span<uint> nextCode = stackalloc uint[MaxAllowedCodeLength + 1];
        Span<int> depthCount = stackalloc int[MaxAllowedCodeLength + 1];
        depthCount.Clear();
        int length = tree.NumSymbols;
        for (int i = 0; i < length; i++)
        {
            depthCount[tree.CodeLengths[i]]++;
        }

        depthCount[0] = 0;
        nextCode[0] = 0;
        uint code = 0;
        for (int i = 1; i <= MaxAllowedCodeLength; i++)
        {
            code = (code + (uint)depthCount[i - 1]) << 1;
            nextCode[i] = code;
        }

        for (int i = 0; i < length; i++)
        {
            int codeLength = tree.CodeLengths[i];
            tree.Codes[i] = (ushort)ReverseBits(codeLength, nextCode[codeLength]++);
        }
    }

    // VP8LCreateHuffmanTree: the histogram is changed (made friendlier to run-length codes)
    private static void CreateHuffmanTree(
        Span<uint> histogram,
        int treeDepthLimit,
        Span<byte> bufferRle,
        Span<HuffmanTree> huffmanTree,
        HuffmanTreeCode code)
    {
        int numSymbols = code.NumSymbols;
        bufferRle.Slice(0, numSymbols).Clear();
        OptimizeHuffmanForRle(numSymbols, bufferRle, histogram);
        GenerateOptimalTree(histogram.Slice(0, numSymbols), huffmanTree, treeDepthLimit, code.CodeLengths);
        ConvertBitDepthsToSymbols(code);
    }
}
