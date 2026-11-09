// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/frame_enc.c, and the recording in cost_enc.c and token_enc.c),
// modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    private sealed partial class Encoder
    {
        // SetLoopParams
        private void SetLoopParams(
            float q)
        {
            q = Clamp(q, 0f, 100f);
            this.SetSegmentParams(q);
            this.SetSegmentProbas();
            this.CalculateLevelCosts();
            this.nbSkip = 0;
        }

        // OneStatPass
        private ulong OneStatPass(
            int rdOpt,
            int nbMbs,
            PassStats s)
        {
            var it = new Iterator(this);
            ulong size = 0;
            ulong sizeP0 = 0;
            ulong distortion = 0;
            ulong pixelCount = (ulong)nbMbs * 384;
            this.SetLoopParams(s.Q);
            var rd = this.info;
            do
            {
                it.Import(null);
                if (this.Decimate(it, ref rd, rdOpt))
                {
                    this.nbSkip++;
                }

                this.RecordResiduals(it, rd);
                size += (ulong)(rd.R + rd.H);
                sizeP0 += (ulong)rd.H;
                distortion += (ulong)rd.D;
                it.SaveBoundary();
            }
            while (it.Next() && (--nbMbs > 0));

            sizeP0 += (ulong)this.segmentHeaderSize;
            if (s.DoSizeSearch)
            {
                size += (ulong)this.FinalizeSkipProba();
                size += (ulong)this.FinalizeTokenProbas();
                size = ((size + sizeP0 + 1024) >> 11) + HeaderSizeEstimate;
                s.Value = size;
            }
            else
            {
                s.Value = GetPsnr(distortion, pixelCount);
            }

            return sizeP0;
        }

        // StatLoop
        private void StatLoop()
        {
            int method = this.method;
            bool doSearch = this.doSearch;
            bool fastProbe = ((method == 0) || (method == 3)) && !doSearch;
            int numPassLeft = this.config.Pass;
            int rdOpt = ((method >= 3) || doSearch) ? 1 : 0;
            int nbMbs = this.MbW * this.MbH;
            var stats = new PassStats(this.config);
            this.stats.AsSpan().Clear();
            if (fastProbe)
            {
                if (method == 3)
                {
                    nbMbs = (nbMbs > 200) ? nbMbs >> 1 : 100;
                }
                else
                {
                    nbMbs = (nbMbs > 200) ? nbMbs >> 2 : 50;
                }
            }

            while (numPassLeft-- > 0)
            {
                bool isLastPass = (Math.Abs(stats.Dq) <= DqLimit) || (numPassLeft == 0) || (this.maxI4HeaderBits == 0);
                ulong sizeP0 = this.OneStatPass(rdOpt, nbMbs, stats);
                if (sizeP0 == 0)
                {
                    // libwebp stops here as for a cancel (no progress hook here, so only an empty
                    // header could): it doesn't finalize the probabilities
                    return;
                }

                if ((this.maxI4HeaderBits > 0) && (sizeP0 > Partition0SizeLimit))
                {
                    ++numPassLeft;
                    this.maxI4HeaderBits >>= 1;
                    continue;
                }

                if (isLastPass)
                {
                    break;
                }

                if (doSearch)
                {
                    stats.ComputeNextQ();
                    if (Math.Abs(stats.Dq) <= DqLimit)
                    {
                        break;
                    }
                }
            }

            if (!doSearch || !stats.DoSizeSearch)
            {
                this.FinalizeSkipProba();
                this.FinalizeTokenProbas();
            }

            this.CalculateLevelCosts();
        }

        // PreLoopInitialize
        private void PreLoopInitialize()
        {
            int averageBytesPerMb = AverageBytesPerMb[this.baseQuant >> 4];
            int bytesPerParts = this.MbW * this.MbH * averageBytesPerMb / this.NumParts;
            for (int p = 0; p < this.NumParts; p++)
            {
                this.parts[p] = new BitWriter(bytesPerParts);
            }
        }

        // PostLoopFinalize
        private void PostLoopFinalize()
        {
            for (int p = 0; p < this.NumParts; p++)
            {
                this.parts[p].Finish();
            }

            this.AdjustFilterStrength();
        }

        // ResetAfterSkip
        private void ResetAfterSkip(
            Iterator it)
        {
            if (this.MbType[it.Mb] == 1)
            {
                this.Nz[it.NzIndex] = 0;
                it.LeftNz[8] = 0;
            }
            else
            {
                this.Nz[it.NzIndex] &= 1u << 24;
            }
        }

        // VP8EncLoop
        public void EncodeLoop()
        {
            this.PreLoopInitialize();
            this.StatLoop();
            var it = new Iterator(this);
            var rd = this.info;
            do
            {
                bool dontUseSkip = !this.useSkipProba;
                it.Import(null);
                if (!this.Decimate(it, ref rd, this.rdOptLevel) || dontUseSkip)
                {
                    this.CodeResiduals(this.parts[it.Part], it, rd);
                }
                else
                {
                    this.ResetAfterSkip(it);
                }

                it.SaveBoundary();
            }
            while (it.Next());

            this.PostLoopFinalize();
        }

        // VP8EncTokenLoop
        public void TokenLoop()
        {
            int maxCount = (this.MbW * this.MbH) >> 3;
            int numPassLeft = this.config.Pass;
            bool doSearch = this.doSearch;
            int rdOpt = this.rdOptLevel;
            ulong pixelCount = (ulong)this.MbW * (ulong)this.MbH * 384;
            var stats = new PassStats(this.config);
            this.PreLoopInitialize();
            if (maxCount < MinTokenCount)
            {
                maxCount = MinTokenCount;
            }

            var rd = this.info;
            while (numPassLeft-- > 0)
            {
                bool isLastPass = (Math.Abs(stats.Dq) <= DqLimit) || (numPassLeft == 0) || (this.maxI4HeaderBits == 0);
                ulong sizeP0 = 0;
                ulong distortion = 0;
                int cnt = maxCount;
                var it = new Iterator(this);
                this.SetLoopParams(stats.Q);
                if (isLastPass)
                {
                    this.stats.AsSpan().Clear();
                }

                this.tokens.Clear();
                do
                {
                    it.Import(null);
                    if (--cnt < 0)
                    {
                        this.FinalizeTokenProbas();
                        this.CalculateLevelCosts();
                        cnt = maxCount;
                    }

                    this.Decimate(it, ref rd, rdOpt);
                    this.RecordTokens(it, rd);
                    sizeP0 += (ulong)rd.H;
                    distortion += (ulong)rd.D;
                    it.SaveBoundary();
                }
                while (it.Next());

                sizeP0 += (ulong)this.segmentHeaderSize;
                if (stats.DoSizeSearch)
                {
                    ulong size = (ulong)this.FinalizeTokenProbas();
                    size += (ulong)this.tokens.EstimateSize(this.coeffs);
                    size = (size + sizeP0 + 1024) >> 11;
                    size += HeaderSizeEstimate;
                    stats.Value = size;
                }
                else
                {
                    stats.Value = GetPsnr(distortion, pixelCount);
                }

                if ((this.maxI4HeaderBits > 0) && (sizeP0 > Partition0SizeLimit))
                {
                    ++numPassLeft;
                    this.maxI4HeaderBits >>= 1;
                    continue;
                }

                if (isLastPass)
                {
                    break;
                }

                if (doSearch)
                {
                    stats.ComputeNextQ();
                }
            }

            if (!stats.DoSizeSearch)
            {
                this.FinalizeTokenProbas();
            }

            this.tokens.Emit(this.parts[0], this.coeffs);
            this.PostLoopFinalize();
        }
    }
}
