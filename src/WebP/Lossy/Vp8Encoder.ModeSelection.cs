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

internal static partial class Vp8Encoder
{
    private sealed partial class Encoder
    {
        // PickBestIntra16
        private void PickBestIntra16(
            Iterator it,
            ref ModeScore rd,
            ModeScore scratch)
        {
            const int numBlocks = 16;
            var dqm = this.Dqm[this.MbSegment[it.Mb]];
            int lambda = dqm.LambdaI16;
            int tlambda = dqm.TLambda;
            var rdCur = scratch;
            var rdBest = rd;
            var original = rd;
            bool isFlat = IsFlatSource16(it.YuvIn, YOffEnc);
            rd.ModeI16 = -1;
            for (int mode = 0; mode < NumPredModes; mode++)
            {
                var tmpDst = it.YuvOut2;
                rdCur.ModeI16 = mode;
                rdCur.Nz = this.ReconstructIntra16(it, rdCur, tmpDst, mode);
                rdCur.D = Sse(it.YuvIn, YOffEnc, tmpDst, YOffEnc, 16, 16);
                rdCur.SD = (tlambda != 0) ? Mult8B(tlambda, Disto16x16(it.YuvIn, YOffEnc, tmpDst, YOffEnc, WeightY)) : 0;
                rdCur.H = FixedCostsI16[mode];
                rdCur.R = this.GetCostLuma16(it, rdCur);
                if (isFlat)
                {
                    isFlat = IsFlat(rdCur.YAcLevels, numBlocks, FlatnessLimitI16);
                    if (isFlat)
                    {
                        rdCur.D *= 2;
                        rdCur.SD *= 2;
                    }
                }

                rdCur.SetRdScore(lambda);
                if ((mode == 0) || (rdCur.Score < rdBest.Score))
                {
                    (rdCur, rdBest) = (rdBest, rdCur);
                    it.SwapOut();
                }
            }

            if (rdBest != original)
            {
                original.CopyAllFrom(rdBest);
            }

            rd = original;
            rd.SetRdScore(dqm.LambdaMode);
            it.SetIntra16Mode(rd.ModeI16);
            if (((rd.Nz & 0x100ffff) == 0x1000000) && (rd.D > dqm.MinDisto))
            {
                StoreMaxDelta(dqm, rd.YDcLevels);
            }
        }

        // GetCostModeI4
        private int ModeCostsI4(
            Iterator it,
            byte[] modes)
        {
            int x = it.I4 & 3, y = it.I4 >> 2;
            int left = (x == 0) ? this.Preds[it.Preds + (y * this.PredsW) - 1] : modes[it.I4 - 1];
            int top = (y == 0) ? this.Preds[it.Preds - this.PredsW + x] : modes[it.I4 - 4];
            return ((top * NumBModes) + left) * NumBModes;
        }

        // PickBestIntra4
        private bool PickBestIntra4(
            Iterator it,
            ModeScore rd,
            ModeScore rdBest)
        {
            var dqm = this.Dqm[this.MbSegment[it.Mb]];
            int lambda = dqm.LambdaI4;
            int tlambda = dqm.TLambda;
            var bestBlocks = it.YuvOut2;
            int totalHeaderBits = 0;
            if (this.maxI4HeaderBits == 0)
            {
                return false;
            }

            rdBest.Init();
            rdBest.H = 211;
            rdBest.SetRdScore(dqm.LambdaMode);
            it.StartI4();
            var fixedCosts = FixedCostsI4;
            var tmpLevels = this.i4Levels;
            var scan = Scan;
            var rdI4 = this.rdI4;
            do
            {
                int bestMode = -1;
                int src = YOffEnc + scan[it.I4];
                int modeCosts = this.ModeCostsI4(it, rd.ModesI4);
                var bestBlock = new Place(bestBlocks, YOffEnc + scan[it.I4]);
                var tmpDst = new Place(it.YuvP, I4TMP);
                rdI4.Init();
                Intra4Preds(it.YuvP, it.I4Boundary, it.I4Top);
                // (the trial's score in locals; the best's in rdI4)
                ref short levels = ref MemoryMarshal.GetArrayDataReference(tmpLevels);
                ref short bestLevels = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(rdBest.YAcLevels), it.I4 * 16);
                for (int mode = 0; mode < NumBModes; mode++)
                {
                    uint nz = (this.ReconstructIntra4(it, tmpLevels, 0, src, tmpDst, mode) ? 1u : 0u) << it.I4;
                    long d = Sse(it.YuvIn, src, tmpDst.Array, tmpDst.At, 4, 4);
                    long sd = (tlambda != 0) ? Mult8B(tlambda, Disto4x4(it.YuvIn, src, tmpDst.Array, tmpDst.At, WeightY)) : 0;
                    long h = fixedCosts[modeCosts + mode];
                    long r = ((mode > 0) && IsFlat(tmpLevels, 1, FlatnessLimitI4)) ? FlatnessPenalty * 1 : 0;
                    long distortion = RdDistoMult * (d + sd);
                    long score = ((r + h) * lambda) + distortion;
                    if ((bestMode >= 0) && (score >= rdI4.Score))
                    {
                        continue;
                    }

                    r += this.GetCostLuma4(it, tmpLevels, 0);
                    score = ((r + h) * lambda) + distortion;
                    if ((bestMode < 0) || (score < rdI4.Score))
                    {
                        rdI4.D = d;
                        rdI4.SD = sd;
                        rdI4.H = h;
                        rdI4.R = r;
                        rdI4.Nz = nz;
                        rdI4.Score = score;
                        bestMode = mode;
                        (tmpDst, bestBlock) = (bestBlock, tmpDst);
                        Vector128.LoadUnsafe(ref levels).StoreUnsafe(ref bestLevels);
                        Vector128.LoadUnsafe(ref levels, 8).StoreUnsafe(ref bestLevels, 8);
                    }
                }

                rdI4.SetRdScore(dqm.LambdaMode);
                rdBest.AddScore(rdI4);
                if (rdBest.Score >= rd.Score)
                {
                    return false;
                }

                totalHeaderBits += (int)rdI4.H;
                if (totalHeaderBits > this.maxI4HeaderBits)
                {
                    return false;
                }

                int bestAt = YOffEnc + scan[it.I4];
                if ((bestBlock.Array != bestBlocks) || (bestBlock.At != bestAt))
                {
                    ref byte from = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bestBlock.Array), bestBlock.At);
                    ref byte to = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bestBlocks), bestAt);
                    for (int y = 0; y < 4; y++)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, y * Bps), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref from, y * Bps)));
                    }
                }

                rd.ModesI4[it.I4] = (byte)bestMode;
                it.TopNz[it.I4 & 3] = it.LeftNz[it.I4 >> 2] = (rdI4.Nz != 0) ? 1 : 0;
            }
            while (it.RotateI4(bestBlocks));

            rd.CopyScoreFrom(rdBest);
            it.SetIntra4Mode(rd.ModesI4);
            it.SwapOut();
            rdBest.YAcLevels.CopyTo(rd.YAcLevels, 0);
            return true;
        }

        // PickBestUV
        private void PickBestUv(
            Iterator it,
            ModeScore rd,
            ModeScore rdUv,
            ModeScore rdBest)
        {
            const int numBlocks = 8;
            var dqm = this.Dqm[this.MbSegment[it.Mb]];
            int lambda = dqm.LambdaUv;
            var tmpDst = new Place(it.YuvOut2, UOffEnc);
            var dst0 = new Place(it.YuvOut, UOffEnc);
            var dst = dst0;
            rd.ModeUv = -1;
            rdBest.Init();
            for (int mode = 0; mode < NumPredModes; mode++)
            {
                rdUv.Nz = this.ReconstructUv(it, rdUv, tmpDst, mode);
                rdUv.D = Sse(it.YuvIn, UOffEnc, tmpDst.Array, tmpDst.At, 16, 8);
                rdUv.SD = 0;
                rdUv.H = FixedCostsUv[mode];
                rdUv.R = this.GetCostUv(it, rdUv);
                if ((mode > 0) && IsFlat(rdUv.UvLevels, numBlocks, FlatnessLimitUv))
                {
                    rdUv.R += FlatnessPenalty * numBlocks;
                }

                rdUv.SetRdScore(lambda);
                if ((mode == 0) || (rdUv.Score < rdBest.Score))
                {
                    rdBest.CopyScoreFrom(rdUv);
                    rd.ModeUv = mode;
                    rdUv.UvLevels.CopyTo(rd.UvLevels, 0);
                    if (this.TopDerr is not null)
                    {
                        rdUv.Derr.CopyTo(rd.Derr, 0);
                    }

                    (dst, tmpDst) = (tmpDst, dst);
                }
            }

            this.MbUvMode[it.Mb] = (byte)rd.ModeUv;
            rd.AddScore(rdBest);
            if ((dst.Array != dst0.Array) || (dst.At != dst0.At))
            {
                for (int y = 0; y < 8; y++)
                {
                    dst.Array.AsSpan(dst.At + (y * Bps), 16).CopyTo(dst0.Array.AsSpan(dst0.At + (y * Bps)));
                }
            }

            if (this.TopDerr is not null)
            {
                this.StoreDiffusionErrors(it, rd);
            }
        }

        // SimpleQuantize
        private void SimpleQuantize(
            Iterator it,
            ModeScore rd)
        {
            bool isI16 = this.MbType[it.Mb] == 1;
            uint nz = 0;
            if (isI16)
            {
                nz = this.ReconstructIntra16(it, rd, it.YuvOut, this.Preds[it.Preds]);
            }
            else
            {
                it.StartI4();
                var scan = Scan;
                do
                {
                    int mode = this.Preds[it.Preds + (it.I4 & 3) + ((it.I4 >> 2) * this.PredsW)];
                    int src = YOffEnc + scan[it.I4];
                    var dst = new Place(it.YuvOut, YOffEnc + scan[it.I4]);
                    Intra4Preds(it.YuvP, it.I4Boundary, it.I4Top);
                    nz |= (this.ReconstructIntra4(it, rd.YAcLevels, it.I4 * 16, src, dst, mode) ? 1u : 0u) << it.I4;
                }
                while (it.RotateI4(it.YuvOut));
            }

            nz |= this.ReconstructUv(it, rd, new Place(it.YuvOut, UOffEnc), this.MbUvMode[it.Mb]);
            rd.Nz = nz;
        }

        // RefineUsingDistortion: the modes by distortion alone (methods 0 to 2)
        private void RefineUsingDistortion(
            Iterator it,
            bool tryBothModes,
            bool refineUvMode,
            ModeScore rd)
        {
            long bestScore = MaxCost;
            uint nz = 0;
            bool isI16 = tryBothModes || (this.MbType[it.Mb] == 1);
            var dqm = this.Dqm[this.MbSegment[it.Mb]];
            const int lambdaDI16 = 106;
            const int lambdaDI4 = 11;
            const int lambdaDUv = 120;
            long scoreI4 = dqm.I4Penalty;
            long i4BitSum = 0;
            long bitLimit = tryBothModes ? this.mbHeaderLimit : MaxCost;
            var fixedI16 = FixedCostsI16;
            if (isI16)
            {
                int bestMode = -1;
                for (int mode = 0; mode < NumPredModes; mode++)
                {
                    long score = ((long)Sse(it.YuvIn, YOffEnc, it.YuvP, I16ModeOffsets[mode], 16, 16) * RdDistoMult) + (fixedI16[mode] * lambdaDI16);
                    if ((mode > 0) && (fixedI16[mode] > bitLimit))
                    {
                        continue;
                    }

                    if (score < bestScore)
                    {
                        bestMode = mode;
                        bestScore = score;
                    }
                }

                if ((it.X == 0) || (it.Y == 0))
                {
                    if (IsFlatSource16(it.YuvIn, YOffEnc))
                    {
                        bestMode = (it.X == 0) ? 0 : 2;
                        tryBothModes = false;
                    }
                }

                it.SetIntra16Mode(bestMode);
            }

            if (tryBothModes || !isI16)
            {
                isI16 = false;
                it.StartI4();
                var fixedI4 = FixedCostsI4;
                var scan = Scan;
                do
                {
                    int bestI4Mode = -1;
                    long bestI4Score = MaxCost;
                    int src = YOffEnc + scan[it.I4];
                    int modeCosts = this.ModeCostsI4(it, rd.ModesI4);
                    Intra4Preds(it.YuvP, it.I4Boundary, it.I4Top);
                    for (int mode = 0; mode < NumBModes; mode++)
                    {
                        long score = ((long)Sse(it.YuvIn, src, it.YuvP, I4ModeOffsets[mode], 4, 4) * RdDistoMult) + (fixedI4[modeCosts + mode] * lambdaDI4);
                        if (score < bestI4Score)
                        {
                            bestI4Mode = mode;
                            bestI4Score = score;
                        }
                    }

                    i4BitSum += fixedI4[modeCosts + bestI4Mode];
                    rd.ModesI4[it.I4] = (byte)bestI4Mode;
                    scoreI4 += bestI4Score;
                    if ((scoreI4 >= bestScore) || (i4BitSum > bitLimit))
                    {
                        isI16 = true;
                        break;
                    }

                    var tmpDst = new Place(it.YuvOut2, YOffEnc + scan[it.I4]);
                    nz |= (this.ReconstructIntra4(it, rd.YAcLevels, it.I4 * 16, src, tmpDst, bestI4Mode) ? 1u : 0u) << it.I4;
                }
                while (it.RotateI4(it.YuvOut2));
            }

            if (!isI16)
            {
                it.SetIntra4Mode(rd.ModesI4);
                it.SwapOut();
                bestScore = scoreI4;
            }
            else
            {
                nz = this.ReconstructIntra16(it, rd, it.YuvOut, this.Preds[it.Preds]);
            }

            if (refineUvMode)
            {
                int bestMode = -1;
                long bestUvScore = MaxCost;
                var fixedUv = FixedCostsUv;
                for (int mode = 0; mode < NumPredModes; mode++)
                {
                    long score = ((long)Sse(it.YuvIn, UOffEnc, it.YuvP, UvModeOffsets[mode], 16, 8) * RdDistoMult) + (fixedUv[mode] * lambdaDUv);
                    if (score < bestUvScore)
                    {
                        bestMode = mode;
                        bestUvScore = score;
                    }
                }

                this.MbUvMode[it.Mb] = (byte)bestMode;
            }

            nz |= this.ReconstructUv(it, rd, new Place(it.YuvOut, UOffEnc), this.MbUvMode[it.Mb]);
            rd.Nz = nz;
            rd.Score = bestScore;
        }

        // VP8Decimate: the macroblock's modes and levels; whether it's skipped (no levels)
        private bool Decimate(
            Iterator it,
            ref ModeScore rd,
            int rdOpt)
        {
            rd.Init();
            MakeLuma16Preds(it);
            MakeChroma8Preds(it);
            if (rdOpt > 0)
            {
                it.DoTrellis = rdOpt >= 3;
                this.PickBestIntra16(it, ref rd, this.scratch1);
                if (this.method >= 2)
                {
                    this.PickBestIntra4(it, rd, this.scratch2);
                }

                this.PickBestUv(it, rd, this.scratch1, this.scratch2);
                if (rdOpt == 2)
                {
                    it.DoTrellis = true;
                    this.SimpleQuantize(it, rd);
                }
            }
            else
            {
                this.RefineUsingDistortion(it, this.method >= 2, this.method >= 1, rd);
            }

            bool isSkipped = rd.Nz == 0;
            this.MbSkip[it.Mb] = (byte)(isSkipped ? 1 : 0);
            return isSkipped;
        }

        private readonly ModeScore scratch1 = new();
        private readonly ModeScore scratch2 = new();
        private readonly ModeScore rdI4 = new();
        private readonly short[] reconTmp = new short[16 * 16];
        private readonly short[] reconTmp4 = new short[16];
        private readonly short[] dcTmp = new short[16];
        private readonly short[] i4Levels = new short[16];

        // The trellis's nodes, [position][0 or 1 above the plain level]: the level, the sign (bit 16)
        // and the previous node (bit 17)
        private readonly int[] trellisNodes = new int[16 * 2];
    }
}
