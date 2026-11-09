// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/quant_enc.c and cost_enc.c, and src/dsp/cost.c), modified by Hot
// Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.WebP;

// Each macroblock's modes and levels: the rate-distortion choices (VP8Decimate), the trellis
// quantizer, and the costs they're weighed by
internal static partial class Vp8Encoder
{
    private const int FlatnessLimitI16 = 0;
    private const int FlatnessLimitI4 = 3;
    private const int FlatnessLimitUv = 2;
    private const int FlatnessPenalty = 140;
    private const int RdDistoMult = 256;

    private const int TypeI16Ac = 0;
    private const int TypeI16Dc = 1;
    private const int TypeChromaA = 2;
    private const int TypeI4Ac = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mult8B(
        int a,
        int b)
    {
        return ((a * b) + 128) >> 8;
    }

    // VP8Residual: the coefficients of a block, from first to last, and the tables of their type
    private struct Residual
    {
        public int First;
        public int Last;
        public short[] Coeffs;
        public int CoeffsAt;
        public int CoeffType;

        public static Residual Init(
            int first,
            int coeffType)
        {
            return new Residual { First = first, CoeffType = coeffType, Coeffs = [] };
        }

        // VP8SetResidualCoeffs
        public void SetCoeffs(
            short[] coeffs,
            int at)
        {
            this.Last = LastNonZero(ref coeffs[at]);
            this.Coeffs = coeffs;
            this.CoeffsAt = at;
        }
    }

    // A place in a buffer: the C's pointers into the work buffers
    private struct Place
    {
        public byte[] Array;
        public int At;

        public Place(
            byte[] array,
            int at)
        {
            this.Array = array;
            this.At = at;
        }
    }

    private static void MakeLuma16Preds(
        Iterator it)
    {
        int left = (it.X != 0) ? YLeft : -1;
        int top = (it.Y != 0) ? it.YTopAt : -1;
        Intra16Preds(it.YuvP, it.Left, left, it.YTopArray, top);
    }

    private static void MakeChroma8Preds(
        Iterator it)
    {
        int left = (it.X != 0) ? ULeft : -1;
        int top = (it.Y != 0) ? it.UvTopAt : -1;
        IntraChromaPreds(it.YuvP, it.Left, left, it.UvTopArray, top);
    }

    private sealed partial class Encoder
    {
        // The offset of a level cost table [type][band][ctx] in levelCost
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CostTable(
            int type,
            int band,
            int ctx)
        {
            return ((((type * NumBands) + band) * NumCtx) + ctx) * (MaxVariableLevel + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ProbaAt(
            int type,
            int band,
            int ctx)
        {
            return ((((type * NumBands) + band) * NumCtx) + ctx) * NumProbas;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int LevelCost(
            int table,
            int level)
        {
            return LevelFixedCosts[level] + this.levelCost[table + ((level > MaxVariableLevel) ? MaxVariableLevel : level)];
        }

        // VP8CalculateLevelCosts
        public void CalculateLevelCosts()
        {
            if (!this.probaDirty)
            {
                return;
            }

            ref ushort codes = ref MemoryMarshal.GetReference(LevelCodes);
            ref ushort levelCosts = ref MemoryMarshal.GetArrayDataReference(this.levelCost);
            Span<int> bitCosts = stackalloc int[2 * NumProbas];
            for (int type = 0; type < NumTypes; type++)
            {
                for (int band = 0; band < NumBands; band++)
                {
                    for (int ctx = 0; ctx < NumCtx; ctx++)
                    {
                        int p = ProbaAt(type, band, ctx);
                        int table = CostTable(type, band, ctx);

                        // The costs of each probability's 0 and 1, then VariableLevelCost's sums of them
                        for (int i = 0; i < NumProbas; i++)
                        {
                            byte proba = this.coeffs[p + i];
                            bitCosts[2 * i] = BitCost(false, proba);
                            bitCosts[(2 * i) + 1] = BitCost(true, proba);
                        }

                        int cost0 = (ctx > 0) ? bitCosts[1] : 0;
                        int costBase = bitCosts[3] + cost0;
                        Unsafe.Add(ref levelCosts, table) = (ushort)(bitCosts[2] + cost0);
                        ref int bc = ref MemoryMarshal.GetReference(bitCosts);
                        int lastCode = -1;
                        int cost = 0;
                        for (int v = 1; v <= MaxVariableLevel; v++)
                        {
                            // (the levels of a category share their code, so their cost)
                            int pattern = Unsafe.Add(ref codes, (v - 1) * 2);
                            int bits = Unsafe.Add(ref codes, ((v - 1) * 2) + 1);
                            int code = (pattern << 16) | bits;
                            if (code != lastCode)
                            {
                                lastCode = code;
                                cost = 0;
                                for (int i = 2; pattern != 0; i++)
                                {
                                    cost += -(pattern & 1) & Unsafe.Add(ref bc, (2 * i) + (bits & 1));
                                    bits >>= 1;
                                    pattern >>= 1;
                                }
                            }

                            Unsafe.Add(ref levelCosts, table + v) = (ushort)(costBase + cost);
                        }
                    }
                }
            }

            this.probaDirty = false;
        }

        // The level cost tables' offset of each position's band: VP8EncBands[n] x 3 contexts x 68
        private static ReadOnlySpan<ushort> BandCostOffsets =>
        [
            0 * 204, 1 * 204, 2 * 204, 3 * 204, 6 * 204, 4 * 204, 5 * 204, 6 * 204, 6 * 204,
            6 * 204, 6 * 204, 6 * 204, 6 * 204, 6 * 204, 6 * 204, 7 * 204, 0 * 204,
        ];

        // GetResidualCost (after GetResidualCost_NEON: the levels' magnitudes, capped magnitudes and
        // next contexts first, then the costs)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int GetResidualCost(
            int ctx0,
            in Residual res)
        {
            int n = res.First;
            int type = res.CoeffType;
            int last = res.Last;
            ref byte probas = ref MemoryMarshal.GetArrayDataReference(this.coeffs);
            byte p0 = Unsafe.Add(ref probas, ProbaAt(type, n, ctx0));
            if (last < 0)
            {
                return BitCost(false, p0);
            }

            ref short coeffs = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(res.Coeffs), res.CoeffsAt);
            ref short magnitudes = ref MemoryMarshal.GetArrayDataReference(this.costMagnitudes);
            ref short capped = ref MemoryMarshal.GetArrayDataReference(this.costCapped);
            ref short contexts = ref MemoryMarshal.GetArrayDataReference(this.costContexts);
            if (Vector128.IsHardwareAccelerated)
            {
                var a0 = Vector128.Abs(Vector128.LoadUnsafe(ref coeffs));
                var a1 = Vector128.Abs(Vector128.LoadUnsafe(ref coeffs, 8));
                a0.StoreUnsafe(ref magnitudes);
                a1.StoreUnsafe(ref magnitudes, 8);
                var maxVariable = Vector128.Create((short)MaxVariableLevel);
                Vector128.Min(a0, maxVariable).StoreUnsafe(ref capped);
                Vector128.Min(a1, maxVariable).StoreUnsafe(ref capped, 8);
                var two = Vector128.Create((short)2);
                var tableSize = Vector128.Create((short)(MaxVariableLevel + 1));
                (Vector128.Min(a0, two) * tableSize).StoreUnsafe(ref contexts);
                (Vector128.Min(a1, two) * tableSize).StoreUnsafe(ref contexts, 8);
            }
            else
            {
                for (int i = 0; i < 16; i++)
                {
                    int v = Math.Abs((int)Unsafe.Add(ref coeffs, i));
                    Unsafe.Add(ref magnitudes, i) = (short)v;
                    Unsafe.Add(ref capped, i) = (short)Math.Min(v, MaxVariableLevel);
                    Unsafe.Add(ref contexts, i) = (short)(Math.Min(v, 2) * (MaxVariableLevel + 1));
                }
            }

            ref ushort fixedCosts = ref MemoryMarshal.GetReference(LevelFixedCosts);
            ref ushort levelCosts = ref MemoryMarshal.GetArrayDataReference(this.levelCost);
            ref ushort bandOffsets = ref MemoryMarshal.GetReference(BandCostOffsets);
            int typeBase = CostTable(type, 0, 0);
            int t = typeBase + CostTable(0, EncBands[n], ctx0);
            int cost = (ctx0 == 0) ? BitCost(true, p0) : 0;
            for (; n < last; n++)
            {
                cost += Unsafe.Add(ref fixedCosts, Unsafe.Add(ref magnitudes, n)) + Unsafe.Add(ref levelCosts, t + Unsafe.Add(ref capped, n));
                t = typeBase + Unsafe.Add(ref bandOffsets, n + 1) + Unsafe.Add(ref contexts, n);
            }

            int v1 = Unsafe.Add(ref magnitudes, n);
            cost += Unsafe.Add(ref fixedCosts, v1) + Unsafe.Add(ref levelCosts, t + Unsafe.Add(ref capped, n));
            if (n < 15)
            {
                int b = EncBands[n + 1];
                int ctx = (v1 == 1) ? 1 : 2;
                cost += BitCost(false, Unsafe.Add(ref probas, ProbaAt(type, b, ctx)));
            }

            return cost;
        }

        private readonly short[] costMagnitudes = new short[16];
        private readonly short[] costCapped = new short[16];
        private readonly short[] costContexts = new short[16];

        private int GetCostLuma4(
            Iterator it,
            short[] levels,
            int at)
        {
            int x = it.I4 & 3, y = it.I4 >> 2;
            var res = Residual.Init(0, 3);
            int ctx = it.TopNz[x] + it.LeftNz[y];
            res.SetCoeffs(levels, at);
            return this.GetResidualCost(ctx, res);
        }

        private int GetCostLuma16(
            Iterator it,
            ModeScore rd)
        {
            it.NzToBytes();
            var res = Residual.Init(0, 1);
            res.SetCoeffs(rd.YDcLevels, 0);
            int r = this.GetResidualCost(it.TopNz[8] + it.LeftNz[8], res);
            res = Residual.Init(1, 0);
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int ctx = it.TopNz[x] + it.LeftNz[y];
                    res.SetCoeffs(rd.YAcLevels, (x + (y * 4)) * 16);
                    r += this.GetResidualCost(ctx, res);
                    it.TopNz[x] = it.LeftNz[y] = (res.Last >= 0) ? 1 : 0;
                }
            }

            return r;
        }

        private int GetCostUv(
            Iterator it,
            ModeScore rd)
        {
            it.NzToBytes();
            var res = Residual.Init(0, 2);
            int r = 0;
            for (int ch = 0; ch <= 2; ch += 2)
            {
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                        res.SetCoeffs(rd.UvLevels, ((ch * 2) + x + (y * 2)) * 16);
                        r += this.GetResidualCost(ctx, res);
                        it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = (res.Last >= 0) ? 1 : 0;
                    }
                }
            }

            return r;
        }

        // Per position n of the trellis: zigzag[n], kWeightTrellis of it (<< 16) and the band of
        // n + 1 (<< 8)
        private static ReadOnlySpan<uint> TrellisPositions =>
        [
            0 | (1 << 8) | (30 << 16), 1 | (2 << 8) | (27 << 16), 4 | (3 << 8) | (27 << 16), 8 | (6 << 8) | (19 << 16),
            5 | (4 << 8) | (24 << 16), 2 | (5 << 8) | (19 << 16), 3 | (6 << 8) | (11 << 16), 6 | (6 << 8) | (17 << 16),
            9 | (6 << 8) | (17 << 16), 12 | (6 << 8) | (11 << 16), 13 | (6 << 8) | (10 << 16), 10 | (6 << 8) | (12 << 16),
            7 | (6 << 8) | (10 << 16), 11 | (6 << 8) | (8 << 16), 14 | (7 << 8) | (8 << 16), 15 | (0 << 8) | (6 << 16),
        ];
    }
}
