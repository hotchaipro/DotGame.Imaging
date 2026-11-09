// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/analysis_enc.c, the segment setup of quant_enc.c, and
// filter_enc.c), modified by Hot Chai Games LLC: written in C#, single-threaded (cwebp's -mt splits the
// analysis in two, with the same result). libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Threading.Tasks;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    private const int MaxAlpha = 255;
    private const int AlphaScale = 2 * MaxAlpha;
    private const int DefaultAlpha = -1;
    private const int MaxItersKMeans = 6;

    private static int Clip(
        int v,
        int m,
        int max)
    {
        return (v < m) ? m : (v > max) ? max : v;
    }

    private sealed partial class Encoder
    {
        // VP8EncAnalyze
        public void Analyze()
        {
            bool doSegments = this.config.EmulateJpegSize || (this.NumSegments > 1) || (this.method <= 1);
            if (doSegments)
            {
                int totalMb = this.MbW * this.MbH;
                var alphas = new int[MaxAlpha + 1];
                int alphaSum = 0, uvAlphaSum = 0;
                if (this.config.UseThreads && (this.MbH >= 2))
                {
                    // As libwebp's -mt: the rows in two jobs, their histograms and sums added (each
                    // macroblock's analysis reads only the picture)
                    int split = this.MbH / 2;
                    var top = new Iterator(this);
                    var bottom = new Iterator(this);
                    var bottomAlphas = new int[MaxAlpha + 1];
                    int bottomAlphaSum = 0, bottomUvAlphaSum = 0;
                    var job = Task.Run(() => this.AnalyzeRows(bottom, split, this.MbH, bottomAlphas, ref bottomAlphaSum, ref bottomUvAlphaSum));
                    this.AnalyzeRows(top, 0, split, alphas, ref alphaSum, ref uvAlphaSum);
                    job.GetAwaiter().GetResult();
                    for (int i = 0; i <= MaxAlpha; i++)
                    {
                        alphas[i] += bottomAlphas[i];
                    }

                    alphaSum += bottomAlphaSum;
                    uvAlphaSum += bottomUvAlphaSum;
                }
                else
                {
                    this.AnalyzeRows(new Iterator(this), 0, this.MbH, alphas, ref alphaSum, ref uvAlphaSum);
                }

                this.alpha = alphaSum / totalMb;
                this.uvAlpha = uvAlphaSum / totalMb;
                this.AssignSegments(alphas);
            }
            else
            {
                // ResetAllMBInfo: every macroblock 16x16, DC, segment 0
                this.MbType.AsSpan().Fill(1);
                this.MbUvMode.AsSpan().Clear();
                this.MbSkip.AsSpan().Clear();
                this.MbSegment.AsSpan().Clear();
                this.MbAlpha.AsSpan().Clear();
                this.Dqm[0].Alpha = 0;
                this.Dqm[0].Beta = 0;
                this.alpha = 0;
                this.uvAlpha = 0;
            }
        }

        // DoSegmentsJob: the macroblocks of rows start to end
        private void AnalyzeRows(
            Iterator it,
            int start,
            int end,
            int[] alphas,
            ref int alphaSum,
            ref int uvAlphaSum)
        {
            var scratch = new byte[32];
            it.SetRow(start);
            it.CountDown = (end - start) * this.MbW;
            do
            {
                it.Import(scratch);
                this.MbAnalyze(it, alphas, ref alphaSum, ref uvAlphaSum);
            }
            while (it.Next());
        }

        private void MbAnalyze(
            Iterator it,
            int[] alphas,
            ref int alphaSum,
            ref int uvAlphaSum)
        {
            it.SetIntra16Mode(0);
            this.MbSkip[it.Mb] = 0;
            this.MbSegment[it.Mb] = 0;
            int bestAlpha = (this.method <= 1) ? this.FastMbAnalyze(it) : MbAnalyzeBestIntra16Mode(it);
            int bestUvAlpha = this.MbAnalyzeBestUvMode(it);
            bestAlpha = ((3 * bestAlpha) + bestUvAlpha + 2) >> 2;
            bestAlpha = Clip(MaxAlpha - bestAlpha, 0, MaxAlpha);
            alphas[bestAlpha]++;
            this.MbAlpha[it.Mb] = (byte)bestAlpha;
            alphaSum += bestAlpha;
            uvAlphaSum += bestUvAlpha;
        }

        private static int GetAlpha(
            int maxValue,
            int lastNonZero)
        {
            return (maxValue > 1) ? AlphaScale * lastNonZero / maxValue : 0;
        }

        private static int MbAnalyzeBestIntra16Mode(
            Iterator it)
        {
            int bestAlpha = DefaultAlpha;
            int bestMode = 0;
            MakeLuma16Preds(it);
            for (int mode = 0; mode < 2; mode++)
            {
                CollectHistogram(it.YuvIn, YOffEnc, it.YuvP, I16ModeOffsets[mode], 0, 16, out int maxValue, out int lastNonZero);
                int a = GetAlpha(maxValue, lastNonZero);
                if (a > bestAlpha)
                {
                    bestAlpha = a;
                    bestMode = mode;
                }
            }

            it.SetIntra16Mode(bestMode);
            return bestAlpha;
        }

        private int FastMbAnalyze(
            Iterator it)
        {
            int q = (int)this.config.Quality;
            uint threshold = (uint)(8 + ((17 - 8) * q / 100));
            Span<uint> dc = stackalloc uint[16];
            for (int k = 0; k < 16; k += 4)
            {
                Mean16x4(it.YuvIn, YOffEnc + (k * Bps), dc.Slice(k));
            }

            uint m = 0, m2 = 0;
            for (int k = 0; k < 16; k++)
            {
                m += dc[k];
                m2 += dc[k] * dc[k];
            }

            if (threshold * m2 < m * m)
            {
                it.SetIntra16Mode(0);
            }
            else
            {
                Span<byte> modes = stackalloc byte[16];
                modes.Clear();
                it.SetIntra4Mode(modes);
            }

            return 0;
        }

        private int MbAnalyzeBestUvMode(
            Iterator it)
        {
            int bestAlpha = DefaultAlpha;
            int smallestAlpha = 0;
            int bestMode = 0;
            MakeChroma8Preds(it);
            for (int mode = 0; mode < 2; mode++)
            {
                CollectHistogram(it.YuvIn, UOffEnc, it.YuvP, UvModeOffsets[mode], 16, 16 + 4 + 4, out int maxValue, out int lastNonZero);
                int a = GetAlpha(maxValue, lastNonZero);
                if (a > bestAlpha)
                {
                    bestAlpha = a;
                }

                if ((mode == 0) || (a < smallestAlpha))
                {
                    smallestAlpha = a;
                    bestMode = mode;
                }
            }

            this.MbUvMode[it.Mb] = (byte)bestMode;
            return bestAlpha;
        }

        // AssignSegments: k-means of the macroblocks' susceptibilities into the segments
        private void AssignSegments(
            int[] alphas)
        {
            int nb = Math.Min(this.NumSegments, NumMbSegments);
            Span<int> centers = stackalloc int[NumMbSegments];
            Span<int> accum = stackalloc int[NumMbSegments];
            Span<int> distAccum = stackalloc int[NumMbSegments];
            var map = new int[MaxAlpha + 1];
            int weightedAverage = 0;
            int n, a, k;
            for (n = 0; (n <= MaxAlpha) && (alphas[n] == 0); n++)
            {
            }

            int minA = n;
            for (n = MaxAlpha; (n > minA) && (alphas[n] == 0); n--)
            {
            }

            int maxA = n;
            int rangeA = maxA - minA;
            for (k = 0, n = 1; k < nb; k++, n += 2)
            {
                centers[k] = minA + (n * rangeA / (2 * nb));
            }

            for (k = 0; k < MaxItersKMeans; k++)
            {
                for (n = 0; n < nb; n++)
                {
                    accum[n] = 0;
                    distAccum[n] = 0;
                }

                n = 0;
                for (a = minA; a <= maxA; a++)
                {
                    if (alphas[a] != 0)
                    {
                        while ((n + 1 < nb) && (Math.Abs(a - centers[n + 1]) < Math.Abs(a - centers[n])))
                        {
                            n++;
                        }

                        map[a] = n;
                        distAccum[n] += a * alphas[a];
                        accum[n] += alphas[a];
                    }
                }

                int displaced = 0;
                weightedAverage = 0;
                int totalWeight = 0;
                for (n = 0; n < nb; n++)
                {
                    if (accum[n] != 0)
                    {
                        int newCenter = (distAccum[n] + (accum[n] / 2)) / accum[n];
                        displaced += Math.Abs(centers[n] - newCenter);
                        centers[n] = newCenter;
                        weightedAverage += newCenter * accum[n];
                        totalWeight += accum[n];
                    }
                }

                weightedAverage = (weightedAverage + (totalWeight / 2)) / totalWeight;
                if (displaced < 5)
                {
                    break;
                }
            }

            for (n = 0; n < this.MbW * this.MbH; n++)
            {
                int alpha = this.MbAlpha[n];
                this.MbSegment[n] = (byte)map[alpha];
                this.MbAlpha[n] = (byte)centers[map[alpha]];
            }

            if ((nb > 1) && ((this.config.Preprocessing & 1) != 0))
            {
                this.SmoothSegmentMap();
            }

            // SetSegmentAlphas
            int min = centers[0], max = centers[0];
            if (nb > 1)
            {
                for (n = 0; n < nb; n++)
                {
                    min = Math.Min(min, centers[n]);
                    max = Math.Max(max, centers[n]);
                }
            }

            if (max == min)
            {
                max = min + 1;
            }

            for (n = 0; n < nb; n++)
            {
                int alpha = 255 * (centers[n] - weightedAverage) / (max - min);
                int beta = 255 * (centers[n] - min) / (max - min);
                this.Dqm[n].Alpha = Clip(alpha, -127, 127);
                this.Dqm[n].Beta = Clip(beta, 0, 255);
            }
        }

        private void SmoothSegmentMap()
        {
            int w = this.MbW, h = this.MbH;
            var tmp = new byte[w * h];
            Span<int> cnt = stackalloc int[NumMbSegments];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    cnt.Clear();
                    int i = x + (w * y);
                    int majority = this.MbSegment[i];
                    cnt[this.MbSegment[i - w - 1]]++;
                    cnt[this.MbSegment[i - w]]++;
                    cnt[this.MbSegment[i - w + 1]]++;
                    cnt[this.MbSegment[i - 1]]++;
                    cnt[this.MbSegment[i + 1]]++;
                    cnt[this.MbSegment[i + w - 1]]++;
                    cnt[this.MbSegment[i + w]]++;
                    cnt[this.MbSegment[i + w + 1]]++;
                    for (int n = 0; n < NumMbSegments; n++)
                    {
                        if (cnt[n] >= 5)
                        {
                            majority = n;
                            break;
                        }
                    }

                    tmp[i] = (byte)majority;
                }
            }

            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    this.MbSegment[x + (w * y)] = tmp[x + (y * w)];
                }
            }
        }

        // VP8SetSegmentParams
        public void SetSegmentParams(
            float quality)
        {
            int numSegments = this.NumSegments;
            double amp = 0.9 * this.config.SnsStrength / 100.0 / 128.0;
            double q = quality / 100.0;
            double cBase = this.config.EmulateJpegSize ? QualityToJpegCompression(q, this.alpha / 255.0) : QualityToCompression(q);
            for (int i = 0; i < numSegments; i++)
            {
                double expn = 1.0 - (amp * this.Dqm[i].Alpha);
                double c = Math.Pow(cBase, expn);
                int qi = (int)(127.0 * (1.0 - c));
                this.Dqm[i].Quant = Clip(qi, 0, 127);
            }

            this.baseQuant = this.Dqm[0].Quant;
            for (int i = numSegments; i < NumMbSegments; i++)
            {
                this.Dqm[i].Quant = this.baseQuant;
            }

            int dqUvAcValue = (this.uvAlpha - 64) * (6 - (-4)) / (100 - 30);
            dqUvAcValue = dqUvAcValue * this.config.SnsStrength / 100;
            dqUvAcValue = Clip(dqUvAcValue, -4, 6);
            int dqUvDcValue = -4 * this.config.SnsStrength / 100;
            dqUvDcValue = Clip(dqUvDcValue, -15, 15);
            this.dqY1Dc = 0;
            this.dqY2Dc = 0;
            this.dqY2Ac = 0;
            this.dqUvDc = dqUvDcValue;
            this.dqUvAc = dqUvAcValue;
            this.SetupFilterStrength();
            if (numSegments > 1)
            {
                this.SimplifySegments();
            }

            this.SetupMatrices();
        }

        private static double QualityToCompression(
            double c)
        {
            double linearC = (c < 0.75) ? c * (2.0 / 3.0) : (2.0 * c) - 1.0;
            return Math.Pow(linearC, 1 / 3.0);
        }

        private static double QualityToJpegCompression(
            double c,
            double alpha)
        {
            const double amin = 0.30;
            const double amax = 0.85;
            const double expMin = 0.4;
            const double expMax = 0.9;
            const double slope = (expMin - expMax) / (amax - amin);
            double expn = (alpha > amax) ? expMin : (alpha < amin) ? expMax : expMax + (slope * (alpha - amin));
            return Math.Pow(c, expn);
        }

        private void SetupFilterStrength()
        {
            int level0 = 5 * this.config.FilterStrength;
            for (int i = 0; i < NumMbSegments; i++)
            {
                var m = this.Dqm[i];
                int qstep = EncAcTable[Clip(m.Quant, 0, 127)] >> 2;
                int baseStrength = FilterStrengthFromDelta(this.filterSharpness, qstep);
                int f = baseStrength * level0 / (256 + m.Beta);
                m.FStrength = (f < 2) ? 0 : (f > 63) ? 63 : f;
            }

            this.filterLevel = this.Dqm[0].FStrength;
            this.filterSimple = this.config.FilterType == 0;
            this.filterSharpness = this.config.FilterSharpness;
        }

        private static int FilterStrengthFromDelta(
            int sharpness,
            int delta)
        {
            int pos = (delta < 64) ? delta : 64 - 1;
            return LevelsFromDelta[(sharpness * 64) + pos];
        }

        private void SimplifySegments()
        {
            Span<int> map = [0, 1, 2, 3];
            int numSegments = Math.Min(this.NumSegments, NumMbSegments);
            int numFinal = 1;
            for (int s1 = 1; s1 < numSegments; s1++)
            {
                var segment1 = this.Dqm[s1];
                bool found = false;
                int s2;
                for (s2 = 0; s2 < numFinal; s2++)
                {
                    var segment2 = this.Dqm[s2];
                    if ((segment1.Quant == segment2.Quant) && (segment1.FStrength == segment2.FStrength))
                    {
                        found = true;
                        break;
                    }
                }

                map[s1] = s2;
                if (!found)
                {
                    if (numFinal != s1)
                    {
                        this.Dqm[s1].CopyTo(this.Dqm[numFinal]);
                    }

                    numFinal++;
                }
            }

            if (numFinal < numSegments)
            {
                for (int i = (this.MbW * this.MbH) - 1; i >= 0; i--)
                {
                    this.MbSegment[i] = (byte)map[this.MbSegment[i]];
                }

                this.NumSegments = numFinal;
                for (int i = numFinal; i < numSegments; i++)
                {
                    this.Dqm[numFinal - 1].CopyTo(this.Dqm[i]);
                }
            }
        }

        private void SetupMatrices()
        {
            int tlambdaScale = (this.method >= 4) ? this.config.SnsStrength : 0;
            for (int i = 0; i < this.NumSegments; i++)
            {
                var m = this.Dqm[i];
                int q = m.Quant;
                m.Y1.Q[0] = EncDcTable[Clip(q + this.dqY1Dc, 0, 127)];
                m.Y1.Q[1] = EncAcTable[Clip(q, 0, 127)];
                m.Y2.Q[0] = (ushort)(EncDcTable[Clip(q + this.dqY2Dc, 0, 127)] * 2);
                m.Y2.Q[1] = EncAcTable2[Clip(q + this.dqY2Ac, 0, 127)];
                m.Uv.Q[0] = EncDcTable[Clip(q + this.dqUvDc, 0, 117)];
                m.Uv.Q[1] = EncAcTable[Clip(q + this.dqUvAc, 0, 127)];
                int qI4 = ExpandMatrix(m.Y1, 0);
                int qI16 = ExpandMatrix(m.Y2, 1);
                int qUv = ExpandMatrix(m.Uv, 2);
                m.LambdaI4 = Math.Max((3 * qI4 * qI4) >> 7, 1);
                m.LambdaI16 = Math.Max(3 * qI16 * qI16, 1);
                m.LambdaUv = Math.Max((3 * qUv * qUv) >> 6, 1);
                m.LambdaMode = Math.Max((1 * qI4 * qI4) >> 7, 1);
                m.LambdaTrellisI4 = Math.Max((7 * qI4 * qI4) >> 3, 1);
                m.LambdaTrellisI16 = Math.Max((qI16 * qI16) >> 2, 1);
                m.LambdaTrellisUv = Math.Max((qUv * qUv) << 1, 1);
                m.TLambda = Math.Max((tlambdaScale * qI4) >> 5, 1);
                m.MinDisto = 20 * m.Y1.Q[0];
                m.MaxEdge = 0;
                m.I4Penalty = 1000L * qI4 * qI4;
            }
        }

        private static ReadOnlySpan<byte> BiasMatrices => [96, 110, 96, 108, 110, 115];

        private static ReadOnlySpan<byte> FreqSharpening => [0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90];

        private static int ExpandMatrix(
            Matrix m,
            int type)
        {
            for (int i = 0; i < 2; i++)
            {
                int bias = BiasMatrices[(type * 2) + ((i > 0) ? 1 : 0)];
                m.Iq[i] = (ushort)((1 << QFix) / m.Q[i]);
                m.Bias[i] = (uint)(bias << (QFix - 8));
                m.ZThresh[i] = (uint)(((1 << QFix) - 1 - m.Bias[i]) / m.Iq[i]);
            }

            for (int i = 2; i < 16; i++)
            {
                m.Q[i] = m.Q[1];
                m.Iq[i] = m.Iq[1];
                m.Bias[i] = m.Bias[1];
                m.ZThresh[i] = m.ZThresh[1];
            }

            int sum = 0;
            for (int i = 0; i < 16; i++)
            {
                m.Sharpen[i] = (type == 0) ? (ushort)((FreqSharpening[i] * m.Q[i]) >> 11) : (ushort)0;
                m.Packed[i] = m.Q[i] | ((ulong)m.Iq[i] << 16) | ((ulong)m.Sharpen[i] << 32);
                sum += m.Q[i];
            }

            return (sum + 8) >> 4;
        }

        // VP8AdjustFilterStrength (without the autofilter's statistics)
        private void AdjustFilterStrength()
        {
            if (this.config.FilterStrength > 0)
            {
                int maxLevel = 0;
                for (int s = 0; s < NumMbSegments; s++)
                {
                    var m = this.Dqm[s];
                    int delta = (m.MaxEdge * m.Y2.Q[1]) >> 3;
                    int level = FilterStrengthFromDelta(this.filterSharpness, delta);
                    if (level > m.FStrength)
                    {
                        m.FStrength = level;
                    }

                    if (maxLevel < m.FStrength)
                    {
                        maxLevel = m.FStrength;
                    }
                }

                this.filterLevel = maxLevel;
            }
        }
    }
}
