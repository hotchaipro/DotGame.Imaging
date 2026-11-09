// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/vp8_dec.c, tree_dec.c, quant_dec.c, frame_dec.c and io_dec.c, and
// src/utils/bit_reader_utils.c), modified by Hot Chai Games LLC: written in C#, single-threaded, and with no
// cropping, scaling, dithering or incremental decoding. libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

/// <summary>
/// The lossy WebP decoder: a VP8 key frame (RFC 6386) to RGBA, alpha 255, with libwebp's fancy
/// upsampling of its chroma.
/// </summary>
internal static partial class Vp8Decoder
{
    // Prediction modes: the 4x4 ones, the 16x16 and chroma ones among them, and the DC variants at
    // the edges (common_dec.h)
    private const int BDcPred = 0;
    private const int BTmPred = 1;
    private const int BVePred = 2;
    private const int BHePred = 3;
    private const int BRdPred = 4;
    private const int BVrPred = 5;
    private const int BLdPred = 6;
    private const int BVlPred = 7;
    private const int BHdPred = 8;
    private const int BHuPred = 9;
    private const int DcPred = BDcPred;
    private const int VPred = BVePred;
    private const int HPred = BHePred;
    private const int TmPred = BTmPred;
    private const int DcPredNoTop = 4;
    private const int DcPredNoLeft = 5;
    private const int DcPredNoTopLeft = 6;

    private const int NumMbSegments = 4;
    private const int NumRefLfDeltas = 4;
    private const int NumModeLfDeltas = 4;
    private const int MaxNumPartitions = 8;
    private const int NumTypes = 4;
    private const int NumBands = 8;
    private const int NumCtx = 3;
    private const int NumProbas = 11;
    private const int BandStride = NumCtx * NumProbas;

    // The work block's stride, and where Y, U and V sit in it, with their top and left samples
    // (vp8i_dec.h): 32 x (17 + 9) bytes
    private const int Bps = 32;
    private const int YuvSize = (Bps * 17) + (Bps * 9);
    private const int YOff = (Bps * 1) + 8;
    private const int UOff = YOff + (Bps * 16) + Bps;
    private const int VOff = UOff + 16;

    // The rows below a macroblock row that its loop filter still changes: none, simple, complex
    private static ReadOnlySpan<byte> FilterExtraRows => [0, 2, 8];

    // The 4x4 modes' tree (tree_dec.c's kYModesIntra4)
    private static ReadOnlySpan<sbyte> YModesIntra4 =>
    [
        -BDcPred, 1, -BTmPred, 2, -BVePred, 3, 4, 6, -BHePred, 5, -BRdPred, -BVrPred, -BLdPred, 7, -BVlPred, 8, -BHdPred, -BHuPred,
    ];

    private static ReadOnlySpan<byte> Bands => [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];

    private static ReadOnlySpan<byte> Zigzag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    // Each 4x4 block's place in the work block
    private static ReadOnlySpan<ushort> Scan =>
    [
        0 + (0 * Bps), 4 + (0 * Bps), 8 + (0 * Bps), 12 + (0 * Bps),
        0 + (4 * Bps), 4 + (4 * Bps), 8 + (4 * Bps), 12 + (4 * Bps),
        0 + (8 * Bps), 4 + (8 * Bps), 8 + (8 * Bps), 12 + (8 * Bps),
        0 + (12 * Bps), 4 + (12 * Bps), 8 + (12 * Bps), 12 + (12 * Bps),
    ];

    /// <summary>
    /// The frame's size from its header (libwebp's VP8GetInfo): false unless it's a key frame that's
    /// shown, with VP8's start code.
    /// </summary>
    public static bool TryReadSize(
        ReadOnlySpan<byte> vp8,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if (vp8.Length < 10)
        {
            return false;
        }

        int bits = vp8[0] | (vp8[1] << 8) | (vp8[2] << 16);
        bool keyFrame = (bits & 1) == 0;
        if (!keyFrame || (vp8[3] != 0x9d) || (vp8[4] != 0x01) || (vp8[5] != 0x2a))
        {
            return false;
        }

        // The version (0 to 3), shown, and the first partition's length, which the chunk must hold
        if ((((bits >> 1) & 7) > 3) || (((bits >> 4) & 1) == 0) || ((bits >> 5) >= vp8.Length))
        {
            return false;
        }

        width = ((vp8[7] << 8) | vp8[6]) & 0x3fff;
        height = ((vp8[9] << 8) | vp8[8]) & 0x3fff;
        if ((width == 0) || (height == 0))
        {
            width = 0;
            height = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Decodes the frame into <paramref name="rgba"/>, width x height RGBA pixels, alpha 255.
    /// </summary>
    /// <exception cref="InvalidDataException">The frame is damaged.</exception>
    public static void Decode(
        ReadOnlySpan<byte> vp8,
        int width,
        int height,
        Span<byte> rgba)
    {
        var decoder = new Decoding(vp8.ToArray(), width, height);
        decoder.ReadHeaders();
        decoder.DecodeFrame(rgba);
    }

    private struct FilterInfo
    {
        public byte Limit;
        public byte InnerLevel;
        public byte Inner;
        public byte HevThreshold;
    }

    private sealed partial class Decoding
    {
        private readonly byte[] data;
        private readonly int width;
        private readonly int height;
        private readonly int mbW;
        private readonly int mbH;

        // The first partition (modes and headers) and the token partitions
        private BitReader br;
        private readonly BitReader[] parts = new BitReader[MaxNumPartitions];
        private int numPartsMinusOne;

        // Segments and filtering
        private bool useSegment;
        private bool updateMap;
        private bool absoluteDelta = true;
        private readonly sbyte[] quantizer = new sbyte[NumMbSegments];
        private readonly sbyte[] filterStrength = new sbyte[NumMbSegments];
        private readonly byte[] segmentProbas = [255, 255, 255];
        private bool filterSimple;
        private int filterLevel;
        private int sharpness;
        private bool useLfDelta;
        private readonly int[] refLfDelta = new int[NumRefLfDeltas];
        private readonly int[] modeLfDelta = new int[NumModeLfDeltas];
        private int filterType;
        private readonly FilterInfo[] filterStrengths = new FilterInfo[NumMbSegments * 2];

        // Dequantization: y1 DC and AC, y2 DC and AC, uv DC and AC, per segment
        private readonly int[] dqm = new int[NumMbSegments * 6];

        // Coefficient probabilities [type][band][context][proba], and skipping
        private readonly byte[] probas = new byte[NumTypes * NumBands * BandStride];
        private bool useSkipProba;
        private int skipProba;

        // Per-macroblock state of the row being decoded
        private readonly byte[] intraT;
        private readonly byte[] intraL = new byte[4];
        private readonly byte[] nz;     // [mbW + 1]: the left one first, then each column's top
        private readonly byte[] nzDc;
        private readonly short[] coeffs;
        private readonly bool[] isI4x4;
        private readonly byte[] imodes;
        private readonly byte[] uvMode;
        private readonly uint[] nonZeroY;
        private readonly uint[] nonZeroUv;
        private readonly bool[] skip;
        private readonly byte[] segment;
        private readonly FilterInfo[] fInfo;

        // Reconstruction: the work block, the top samples, and the row cache (with the rows above
        // it that its filtering changes)
        private readonly byte[] yuvB = new byte[YuvSize];
        private readonly byte[] yuvT;
        private byte[] cacheY = [];
        private byte[] cacheU = [];
        private byte[] cacheV = [];
        private int cacheYStride;
        private int cacheUvStride;
        private int cacheYOffset;
        private int cacheUvOffset;

        public Decoding(
            byte[] data,
            int width,
            int height)
        {
            this.data = data;
            this.width = width;
            this.height = height;
            this.mbW = (width + 15) >> 4;
            this.mbH = (height + 15) >> 4;
            this.intraT = new byte[4 * this.mbW];
            this.nz = new byte[this.mbW + 1];
            this.nzDc = new byte[this.mbW + 1];
            this.coeffs = new short[this.mbW * 384];
            this.isI4x4 = new bool[this.mbW];
            this.imodes = new byte[this.mbW * 16];
            this.uvMode = new byte[this.mbW];
            this.nonZeroY = new uint[this.mbW];
            this.nonZeroUv = new uint[this.mbW];
            this.skip = new bool[this.mbW];
            this.segment = new byte[this.mbW];
            this.fInfo = new FilterInfo[this.mbW];
            this.yuvT = new byte[this.mbW * 32];
        }

        // VP8DecodeMB's loop and the row processing (ParseFrame, VP8ProcessRow, FinishRow)
        public void DecodeFrame(
            Span<byte> rgba)
        {
            this.PrecomputeFilterStrengths();
            int extraRows = FilterExtraRows[this.filterType];
            this.cacheYStride = 16 * this.mbW;
            this.cacheUvStride = 8 * this.mbW;
            this.cacheYOffset = extraRows * this.cacheYStride;
            this.cacheUvOffset = (extraRows / 2) * this.cacheUvStride;
            this.cacheY = new byte[this.cacheYOffset + (16 * this.cacheYStride) + RowPadding];
            this.cacheU = new byte[this.cacheUvOffset + (8 * this.cacheUvStride) + RowPadding];
            this.cacheV = new byte[this.cacheUvOffset + (8 * this.cacheUvStride) + RowPadding];
            var output = new Output(this.width, this.height, rgba);
            for (int mbY = 0; mbY < this.mbH; mbY++)
            {
                ref var tokenBr = ref this.parts[mbY & this.numPartsMinusOne];
                this.ParseIntraModeRow();

                if (this.br.Eof)
                {
                    throw new InvalidDataException("The WebP image's first VP8 partition is cut short.");
                }

                for (int mbX = 0; mbX < this.mbW; mbX++)
                {
                    this.DecodeMacroblock(ref tokenBr, mbX);
                    if (tokenBr.Eof)
                    {
                        throw new InvalidDataException("The WebP image's VP8 data is cut short.");
                    }
                }

                // The next row's left context
                this.nz[0] = 0;
                this.nzDc[0] = 0;
                this.intraL.AsSpan().Clear();
                this.ReconstructRow(mbY);
                this.FinishRow(mbY, extraRows, ref output);
            }
        }

        // VP8DecodeMB
        private void DecodeMacroblock(
            ref BitReader tokenBr,
            int mbX)
        {
            bool skipped = this.useSkipProba && this.skip[mbX];
            if (!skipped)
            {
                skipped = this.ParseResiduals(ref tokenBr, mbX);
            }
            else
            {
                this.nz[0] = 0;
                this.nz[mbX + 1] = 0;
                if (!this.isI4x4[mbX])
                {
                    this.nzDc[0] = 0;
                    this.nzDc[mbX + 1] = 0;
                }

                this.nonZeroY[mbX] = 0;
                this.nonZeroUv[mbX] = 0;
            }

            if (this.filterType > 0)
            {
                var info = this.filterStrengths[(this.segment[mbX] * 2) + (this.isI4x4[mbX] ? 1 : 0)];
                info.Inner |= (byte)(skipped ? 0 : 1);
                this.fInfo[mbX] = info;
            }
        }

        // ParseResiduals: the macroblock's coefficients, dequantized, and which of its blocks have them
        private bool ParseResiduals(
            ref BitReader tokenBr,
            int mbX)
        {
            var buf = this.data;
            ref int q = ref this.dqm[this.segment[mbX] * 6];
            var block = this.coeffs.AsSpan(mbX * 384, 384);
            block.Clear();
            ref short dst = ref MemoryMarshal.GetReference(block);
            int y1Dc = q;
            int y1Ac = Unsafe.Add(ref q, 1);
            int uvDc = Unsafe.Add(ref q, 4);
            int uvAc = Unsafe.Add(ref q, 5);
            uint nonZeroY = 0;
            uint nonZeroUv = 0;
            int first;
            int acType;
            if (!this.isI4x4[mbX])
            {
                // The DC coefficients of the 16 luma blocks, transformed together
                Span<short> dc = stackalloc short[16];
                dc.Clear();
                int ctx = this.nzDc[mbX + 1] + this.nzDc[0];
                int nzCount = GetCoeffs(ref tokenBr, buf, this.probas, 1, ctx, Unsafe.Add(ref q, 2), Unsafe.Add(ref q, 3), 0, ref MemoryMarshal.GetReference(dc));
                byte hasDc = (byte)((nzCount > 0) ? 1 : 0);
                this.nzDc[mbX + 1] = hasDc;
                this.nzDc[0] = hasDc;
                if (nzCount > 1)
                {
                    TransformWht(dc, block);
                }
                else
                {
                    short dc0 = (short)((dc[0] + 3) >> 3);
                    for (int i = 0; i < 16 * 16; i += 16)
                    {
                        Unsafe.Add(ref dst, i) = dc0;
                    }
                }

                first = 1;
                acType = 0;
            }
            else
            {
                first = 0;
                acType = 3;
            }

            int tnz = this.nz[mbX + 1] & 0x0f;
            int lnz = this.nz[0] & 0x0f;
            int offset = 0;
            for (int y = 0; y < 4; y++)
            {
                int l = lnz & 1;
                uint nzCoeffs = 0;
                for (int x = 0; x < 4; x++)
                {
                    int ctx = l + (tnz & 1);
                    int nzCount = GetCoeffs(ref tokenBr, buf, this.probas, acType, ctx, y1Dc, y1Ac, first, ref Unsafe.Add(ref dst, offset));
                    l = (nzCount > first) ? 1 : 0;
                    tnz = (tnz >> 1) | (l << 7);
                    nzCoeffs = NzCodeBits(nzCoeffs, nzCount, Unsafe.Add(ref dst, offset) != 0);
                    offset += 16;
                }

                tnz >>= 4;
                lnz = (lnz >> 1) | (l << 7);
                nonZeroY = (nonZeroY << 8) | nzCoeffs;
            }

            uint outTnz = (uint)tnz;
            uint outLnz = (uint)(lnz >> 4);
            for (int ch = 0; ch < 4; ch += 2)
            {
                uint nzCoeffs = 0;
                tnz = this.nz[mbX + 1] >> (4 + ch);
                lnz = this.nz[0] >> (4 + ch);
                for (int y = 0; y < 2; y++)
                {
                    int l = lnz & 1;
                    for (int x = 0; x < 2; x++)
                    {
                        int ctx = l + (tnz & 1);
                        int nzCount = GetCoeffs(ref tokenBr, buf, this.probas, 2, ctx, uvDc, uvAc, 0, ref Unsafe.Add(ref dst, offset));
                        l = (nzCount > 0) ? 1 : 0;
                        tnz = (tnz >> 1) | (l << 3);
                        nzCoeffs = NzCodeBits(nzCoeffs, nzCount, Unsafe.Add(ref dst, offset) != 0);
                        offset += 16;
                    }

                    tnz >>= 2;
                    lnz = (lnz >> 1) | (l << 5);
                }

                nonZeroUv |= nzCoeffs << (4 * ch);
                outTnz |= (uint)((tnz << 4) << ch);
                outLnz |= (uint)((lnz & 0xf0) << ch);
            }

            this.nz[mbX + 1] = (byte)outTnz;
            this.nz[0] = (byte)outLnz;
            this.nonZeroY[mbX] = nonZeroY;
            this.nonZeroUv[mbX] = nonZeroUv;
            return (nonZeroY | nonZeroUv) == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint NzCodeBits(
            uint nzCoeffs,
            int nzCount,
            bool dcNonZero)
        {
            nzCoeffs <<= 2;
            nzCoeffs |= (nzCount > 3) ? 3u : (nzCount > 1) ? 2u : (dcNonZero ? 1u : 0u);
            return nzCoeffs;
        }

        // PrecomputeFilterStrengths
        private void PrecomputeFilterStrengths()
        {
            if (this.filterType == 0)
            {
                return;
            }

            for (int s = 0; s < NumMbSegments; s++)
            {
                int baseLevel;
                if (this.useSegment)
                {
                    baseLevel = this.filterStrength[s];
                    if (!this.absoluteDelta)
                    {
                        baseLevel += this.filterLevel;
                    }
                }
                else
                {
                    baseLevel = this.filterLevel;
                }

                for (int i4x4 = 0; i4x4 <= 1; i4x4++)
                {
                    ref var info = ref this.filterStrengths[(s * 2) + i4x4];
                    int level = baseLevel;
                    if (this.useLfDelta)
                    {
                        level += this.refLfDelta[0];
                        if (i4x4 != 0)
                        {
                            level += this.modeLfDelta[0];
                        }
                    }

                    level = (level < 0) ? 0 : (level > 63) ? 63 : level;
                    if (level > 0)
                    {
                        int ilevel = level;
                        if (this.sharpness > 0)
                        {
                            ilevel >>= (this.sharpness > 4) ? 2 : 1;
                            if (ilevel > 9 - this.sharpness)
                            {
                                ilevel = 9 - this.sharpness;
                            }
                        }

                        if (ilevel < 1)
                        {
                            ilevel = 1;
                        }

                        info.InnerLevel = (byte)ilevel;
                        info.Limit = (byte)((2 * level) + ilevel);
                        info.HevThreshold = (byte)((level >= 40) ? 2 : (level >= 15) ? 1 : 0);
                    }
                    else
                    {
                        info.Limit = 0;
                    }

                    info.Inner = (byte)i4x4;
                }
            }
        }

        private static int CheckMode(
            int mbX,
            int mbY,
            int mode)
        {
            if (mode == BDcPred)
            {
                if (mbX == 0)
                {
                    return (mbY == 0) ? DcPredNoTopLeft : DcPredNoLeft;
                }

                return (mbY == 0) ? DcPredNoTop : BDcPred;
            }

            return mode;
        }

        // DoFilter for each macroblock of the row, then the rows that are final to the output, and the
        // rows the next row's filtering changes kept above the cache (FinishRow)
        private void FinishRow(
            int mbY,
            int extraRows,
            ref Output output)
        {
            int ySize = extraRows * this.cacheYStride;
            int uvSize = (extraRows / 2) * this.cacheUvStride;
            bool isFirstRow = mbY == 0;
            bool isLastRow = mbY >= this.mbH - 1;
            if (this.filterType > 0)
            {
                for (int mbX = 0; mbX < this.mbW; mbX++)
                {
                    this.DoFilter(mbX, mbY);
                }
            }

            int yStart = mbY * 16;
            int yEnd = (mbY + 1) * 16;
            int yRow;
            int uvRow;
            if (!isFirstRow)
            {
                yStart -= extraRows;
                yRow = this.cacheYOffset - ySize;
                uvRow = this.cacheUvOffset - uvSize;
            }
            else
            {
                yRow = this.cacheYOffset;
                uvRow = this.cacheUvOffset;
            }

            if (!isLastRow)
            {
                yEnd -= extraRows;
            }

            if (yEnd > this.height)
            {
                yEnd = this.height;
            }

            if (yStart < yEnd)
            {
                output.Emit(this.cacheY, yRow, this.cacheYStride, this.cacheU, this.cacheV, uvRow, this.cacheUvStride, yStart, yEnd - yStart);
            }

            if (!isLastRow)
            {
                // The rows the next macroblock row's filter reaches, above where it will be written
                this.cacheY.AsSpan(this.cacheYOffset - ySize + (16 * this.cacheYStride), ySize).CopyTo(this.cacheY.AsSpan(this.cacheYOffset - ySize, ySize));
                this.cacheU.AsSpan(this.cacheUvOffset - uvSize + (8 * this.cacheUvStride), uvSize).CopyTo(this.cacheU.AsSpan(this.cacheUvOffset - uvSize, uvSize));
                this.cacheV.AsSpan(this.cacheUvOffset - uvSize + (8 * this.cacheUvStride), uvSize).CopyTo(this.cacheV.AsSpan(this.cacheUvOffset - uvSize, uvSize));
            }
        }

        private void DoFilter(
            int mbX,
            int mbY)
        {
            int yStride = this.cacheYStride;
            ref var info = ref this.fInfo[mbX];
            int y = this.cacheYOffset + (mbX * 16);
            int ilevel = info.InnerLevel;
            int limit = info.Limit;
            if (limit == 0)
            {
                return;
            }

            if (VectorFilters)
            {
                FilterMacroblockVector(this.cacheY, this.cacheU, this.cacheV, y, yStride, this.cacheUvOffset + (mbX * 8), this.cacheUvStride, this.filterType, mbX, mbY, limit, ilevel, info.HevThreshold, info.Inner != 0);
                return;
            }

            var cy = this.cacheY.AsSpan();
            if (this.filterType == 1)
            {
                if (mbX > 0)
                {
                    SimpleHFilter16(cy, y, yStride, limit + 4);
                }

                if (info.Inner != 0)
                {
                    SimpleHFilter16i(cy, y, yStride, limit);
                }

                if (mbY > 0)
                {
                    SimpleVFilter16(cy, y, yStride, limit + 4);
                }

                if (info.Inner != 0)
                {
                    SimpleVFilter16i(cy, y, yStride, limit);
                }
            }
            else
            {
                int uvStride = this.cacheUvStride;
                int uv = this.cacheUvOffset + (mbX * 8);
                var cu = this.cacheU.AsSpan();
                var cv = this.cacheV.AsSpan();
                int hevThreshold = info.HevThreshold;
                if (mbX > 0)
                {
                    FilterLoop26(cy, y, 1, yStride, 16, limit + 4, ilevel, hevThreshold);
                    FilterLoop26(cu, uv, 1, uvStride, 8, limit + 4, ilevel, hevThreshold);
                    FilterLoop26(cv, uv, 1, uvStride, 8, limit + 4, ilevel, hevThreshold);
                }

                if (info.Inner != 0)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        FilterLoop24(cy, y + (4 * k), 1, yStride, 16, limit, ilevel, hevThreshold);
                    }

                    FilterLoop24(cu, uv + 4, 1, uvStride, 8, limit, ilevel, hevThreshold);
                    FilterLoop24(cv, uv + 4, 1, uvStride, 8, limit, ilevel, hevThreshold);
                }

                if (mbY > 0)
                {
                    FilterLoop26(cy, y, yStride, 1, 16, limit + 4, ilevel, hevThreshold);
                    FilterLoop26(cu, uv, uvStride, 1, 8, limit + 4, ilevel, hevThreshold);
                    FilterLoop26(cv, uv, uvStride, 1, 8, limit + 4, ilevel, hevThreshold);
                }

                if (info.Inner != 0)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        FilterLoop24(cy, y + (4 * k * yStride), yStride, 1, 16, limit, ilevel, hevThreshold);
                    }

                    FilterLoop24(cu, uv + (4 * uvStride), uvStride, 1, 8, limit, ilevel, hevThreshold);
                    FilterLoop24(cv, uv + (4 * uvStride), uvStride, 1, 8, limit, ilevel, hevThreshold);
                }
            }
        }
    }
}
