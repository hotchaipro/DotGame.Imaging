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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

// The coding passes: the statistics passes, the probabilities they give, and the residuals coded
// straight into the partitions (methods 0 to 2) or into the token buffer (3 to 6)
internal static partial class Vp8Encoder
{
    private const int HeaderSizeEstimate = 12 + 8 + 10;
    private const double DqLimit = 0.4;
    private const ulong Partition0SizeLimit = ((ulong)MaxPartition0Size - 2048) << 11;
    private const int SkipProbaThreshold = 250;
    private const int MinTokenCount = 96;

    private static ReadOnlySpan<byte> Cat3 => [173, 148, 140];
    private static ReadOnlySpan<byte> Cat4 => [176, 155, 140, 135];
    private static ReadOnlySpan<byte> Cat5 => [180, 157, 141, 134, 130];
    private static ReadOnlySpan<byte> Cat6 => [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];
    private static ReadOnlySpan<byte> AverageBytesPerMb => [50, 24, 16, 9, 7, 5, 3, 2];

    private static float Clamp(
        float v,
        float min,
        float max)
    {
        return (v < min) ? min : (v > max) ? max : v;
    }

    // PassStats: the convergence of a size or PSNR search
    private sealed class PassStats
    {
        public bool IsFirst = true;
        public float Dq = 10f;
        public float Q;
        public float LastQ;
        public float QMin;
        public float QMax;
        public double Value;
        public double LastValue;
        public double Target;
        public bool DoSizeSearch;

        public PassStats(
            Vp8EncoderOptions config)
        {
            this.DoSizeSearch = config.TargetSize != 0;
            this.QMin = 1f * config.QMin;
            this.QMax = 1f * config.QMax;
            this.Q = this.LastQ = Clamp(config.Quality, this.QMin, this.QMax);
            this.Target = this.DoSizeSearch ? config.TargetSize : (config.TargetPsnr > 0) ? config.TargetPsnr : 40.0;
        }

        // ComputeNextQ
        public void ComputeNextQ()
        {
            float dq;
            if (this.IsFirst)
            {
                dq = (this.Value > this.Target) ? -this.Dq : this.Dq;
                this.IsFirst = false;
            }
            else if (this.Value != this.LastValue)
            {
                double slope = (this.Target - this.Value) / (this.LastValue - this.Value);
                dq = (float)(slope * (this.LastQ - this.Q));
            }
            else
            {
                dq = 0;
            }

            this.Dq = Clamp(dq, -30f, 30f);
            this.LastQ = this.Q;
            this.LastValue = this.Value;
            this.Q = Clamp(this.Q + this.Dq, this.QMin, this.QMax);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RecordStats(
        bool bit,
        ref uint stats,
        int at)
    {
        ref uint slot = ref Unsafe.Add(ref stats, at);
        uint p = slot;
        if (p >= 0xfffe0000u)
        {
            p = ((p + 1u) >> 1) & 0x7fff7fffu;
        }

        p += 0x00010000u + (bit ? 1u : 0u);
        slot = p;
        return bit;
    }

    private static double GetPsnr(
        ulong mse,
        ulong size)
    {
        return ((mse > 0) && (size > 0)) ? 10.0 * Math.Log10(255.0 * 255.0 * size / mse) : 99;
    }

    private sealed partial class Encoder
    {
        private readonly ModeScore info = new();

        private static int CalcSkipProba(
            long nb,
            long total)
        {
            return (int)((total != 0) ? (total - nb) * 255 / total : 255);
        }

        // FinalizeSkipProba
        private long FinalizeSkipProba()
        {
            int nbMbs = this.MbW * this.MbH;
            int nbEvents = this.nbSkip;
            this.skipProba = CalcSkipProba(nbEvents, nbMbs);
            this.useSkipProba = this.skipProba < SkipProbaThreshold;
            long size = 256;
            if (this.useSkipProba)
            {
                size += ((long)nbEvents * BitCost(true, (byte)this.skipProba)) + ((long)(nbMbs - nbEvents) * BitCost(false, (byte)this.skipProba));
                size += 8 * 256;
            }

            return size;
        }

        private static int CalcTokenProba(
            int nb,
            int total)
        {
            return (nb != 0) ? (255 - (nb * 255 / total)) : 255;
        }

        private static int BranchCost(
            int nb,
            int total,
            int proba)
        {
            return (nb * BitCost(true, (byte)proba)) + ((total - nb) * BitCost(false, (byte)proba));
        }

        // FinalizeTokenProbas
        private long FinalizeTokenProbas()
        {
            bool hasChanged = false;
            long size = 0;
            var update = CoeffsUpdateProba;
            var defaults = CoeffsProba0;
            for (int i = 0; i < this.coeffs.Length; i++)
            {
                uint stats = this.stats[i];
                int nb = (int)(stats & 0xffff);
                int total = (int)((stats >> 16) & 0xffff);
                int updateProba = update[i];
                int oldP = defaults[i];
                int newP = CalcTokenProba(nb, total);
                int oldCost = BranchCost(nb, total, oldP) + BitCost(false, (byte)updateProba);
                int newCost = BranchCost(nb, total, newP) + BitCost(true, (byte)updateProba) + (8 * 256);
                bool useNewP = oldCost > newCost;
                size += BitCost(useNewP, (byte)updateProba);
                if (useNewP)
                {
                    this.coeffs[i] = (byte)newP;
                    hasChanged |= newP != oldP;
                    size += 8 * 256;
                }
                else
                {
                    this.coeffs[i] = (byte)oldP;
                }
            }

            this.probaDirty = hasChanged;
            return size;
        }

        private static int GetProba(
            int a,
            int b)
        {
            int total = a + b;
            return (total == 0) ? 255 : ((255 * a) + (total / 2)) / total;
        }

        // SetSegmentProbas
        private void SetSegmentProbas()
        {
            Span<int> p = stackalloc int[NumMbSegments];
            int count = this.MbW * this.MbH;
            for (int n = 0; n < count; n++)
            {
                p[this.MbSegment[n]]++;
            }

            if (this.NumSegments > 1)
            {
                var probas = this.segmentProbas;
                probas[0] = (byte)GetProba(p[0] + p[1], p[2] + p[3]);
                probas[1] = (byte)GetProba(p[0], p[1]);
                probas[2] = (byte)GetProba(p[2], p[3]);
                this.updateMap = (probas[0] != 255) || (probas[1] != 255) || (probas[2] != 255);
                if (!this.updateMap)
                {
                    this.MbSegment.AsSpan().Clear();
                }

                this.segmentHeaderSize =
                    (p[0] * (BitCost(false, probas[0]) + BitCost(false, probas[1])))
                    + (p[1] * (BitCost(false, probas[0]) + BitCost(true, probas[1])))
                    + (p[2] * (BitCost(true, probas[0]) + BitCost(false, probas[2])))
                    + (p[3] * (BitCost(true, probas[0]) + BitCost(true, probas[2])));
            }
            else
            {
                this.updateMap = false;
                this.segmentHeaderSize = 0;
            }
        }

        // PutCoeffs, with the writer's state in locals
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool PutCoeffs(
            BitWriter bw,
            int ctx,
            in Residual res)
        {
            int n = res.First;
            int type = res.CoeffType;
            int last = res.Last;
            ref byte probas = ref MemoryMarshal.GetArrayDataReference(this.coeffs);
            ref byte bands = ref MemoryMarshal.GetReference(EncBands);
            ref short coeffs = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(res.Coeffs), res.CoeffsAt);
            int range = bw.Range, value = bw.Value, nbBits = bw.NbBits;
            int p = ProbaAt(type, n, ctx);
            if (!BitWriter.Put(bw, ref range, ref value, ref nbBits, last >= 0, Unsafe.Add(ref probas, p)))
            {
                bw.Range = range;
                bw.Value = value;
                bw.NbBits = nbBits;
                return false;
            }

            while (n < 16)
            {
                int c = Unsafe.Add(ref coeffs, n++);
                bool sign = c < 0;
                int v = sign ? -c : c;
                if (!BitWriter.Put(bw, ref range, ref value, ref nbBits, v != 0, Unsafe.Add(ref probas, p + 1)))
                {
                    p = ProbaAt(type, Unsafe.Add(ref bands, n), 0);
                    continue;
                }

                if (!BitWriter.Put(bw, ref range, ref value, ref nbBits, v > 1, Unsafe.Add(ref probas, p + 2)))
                {
                    p = ProbaAt(type, Unsafe.Add(ref bands, n), 1);
                }
                else
                {
                    // The larger levels are rare: their writes out of line, with the state in the writer
                    bw.Range = range;
                    bw.Value = value;
                    bw.NbBits = nbBits;
                    PutLargeLevel(bw, v, ref Unsafe.Add(ref probas, p));
                    range = bw.Range;
                    value = bw.Value;
                    nbBits = bw.NbBits;
                    p = ProbaAt(type, Unsafe.Add(ref bands, n), 2);
                }

                // VP8PutBitUniform: a probability of a half
                BitWriter.Put(bw, ref range, ref value, ref nbBits, sign, 128);
                if ((n == 16) || !BitWriter.Put(bw, ref range, ref value, ref nbBits, n <= last, Unsafe.Add(ref probas, p)))
                {
                    break;
                }
            }

            bw.Range = range;
            bw.Value = value;
            bw.NbBits = nbBits;
            return true;
        }

        // A level above 1 (the bits after "more than one")
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PutLargeLevel(
            BitWriter bw,
            int v,
            ref byte p)
        {
            if (!bw.PutBit(v > 4, Unsafe.Add(ref p, 3)))
            {
                if (bw.PutBit(v != 2, Unsafe.Add(ref p, 4)))
                {
                    bw.PutBit(v == 4, Unsafe.Add(ref p, 5));
                }
            }
            else if (!bw.PutBit(v > 10, Unsafe.Add(ref p, 6)))
            {
                if (!bw.PutBit(v > 6, Unsafe.Add(ref p, 7)))
                {
                    bw.PutBit(v == 6, 159);
                }
                else
                {
                    bw.PutBit(v >= 9, 165);
                    bw.PutBit((v & 1) == 0, 145);
                }
            }
            else
            {
                int mask;
                ReadOnlySpan<byte> tab;
                if (v < 3 + (8 << 1))
                {
                    bw.PutBit(false, Unsafe.Add(ref p, 8));
                    bw.PutBit(false, Unsafe.Add(ref p, 9));
                    v -= 3 + (8 << 0);
                    mask = 1 << 2;
                    tab = Cat3;
                }
                else if (v < 3 + (8 << 2))
                {
                    bw.PutBit(false, Unsafe.Add(ref p, 8));
                    bw.PutBit(true, Unsafe.Add(ref p, 9));
                    v -= 3 + (8 << 1);
                    mask = 1 << 3;
                    tab = Cat4;
                }
                else if (v < 3 + (8 << 3))
                {
                    bw.PutBit(true, Unsafe.Add(ref p, 8));
                    bw.PutBit(false, Unsafe.Add(ref p, 10));
                    v -= 3 + (8 << 2);
                    mask = 1 << 4;
                    tab = Cat5;
                }
                else
                {
                    bw.PutBit(true, Unsafe.Add(ref p, 8));
                    bw.PutBit(true, Unsafe.Add(ref p, 10));
                    v -= 3 + (8 << 3);
                    mask = 1 << 10;
                    tab = Cat6;
                }

                int t = 0;
                while (mask != 0)
                {
                    bw.PutBit((v & mask) != 0, tab[t++]);
                    mask >>= 1;
                }
            }
        }

        // CodeResiduals
        private void CodeResiduals(
            BitWriter bw,
            Iterator it,
            ModeScore rd)
        {
            bool i16 = this.MbType[it.Mb] == 1;
            it.NzToBytes();
            Residual res;
            if (i16)
            {
                res = Residual.Init(0, TypeI16Dc);
                res.SetCoeffs(rd.YDcLevels, 0);
                it.TopNz[8] = it.LeftNz[8] = this.PutCoeffs(bw, it.TopNz[8] + it.LeftNz[8], res) ? 1 : 0;
                res = Residual.Init(1, TypeI16Ac);
            }
            else
            {
                res = Residual.Init(0, TypeI4Ac);
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int ctx = it.TopNz[x] + it.LeftNz[y];
                    res.SetCoeffs(rd.YAcLevels, (x + (y * 4)) * 16);
                    it.TopNz[x] = it.LeftNz[y] = this.PutCoeffs(bw, ctx, res) ? 1 : 0;
                }
            }

            res = Residual.Init(0, TypeChromaA);
            for (int ch = 0; ch <= 2; ch += 2)
            {
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                        res.SetCoeffs(rd.UvLevels, ((ch * 2) + x + (y * 2)) * 16);
                        it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = this.PutCoeffs(bw, ctx, res) ? 1 : 0;
                    }
                }
            }

            it.BytesToNz();
        }
    }
}
