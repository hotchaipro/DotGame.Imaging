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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    private sealed partial class Encoder
    {
        // ReconstructIntra16
        private uint ReconstructIntra16(
            Iterator it,
            ModeScore rd,
            byte[] yuvOut,
            int mode)
        {
            int reference = I16ModeOffsets[mode];
            var dqm = this.Dqm[this.MbSegment[it.Mb]];
            uint nz = 0;
            var tmp = this.reconTmp;
            var dcTmp = this.dcTmp;
            var scan = Scan;
            for (int n = 0; n < 16; n += 2)
            {
                FTransform2(it.YuvIn, YOffEnc + scan[n], it.YuvP, reference + scan[n], tmp.AsSpan(n * 16));
            }

            FTransformWht(tmp, dcTmp);
            nz |= (QuantizeBlock(dcTmp, rd.YDcLevels, dqm.Y2) ? 1u : 0u) << 24;
            if (it.DoTrellis)
            {
                it.NzToBytes();
                for (int y = 0, n = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++, n++)
                    {
                        int ctx = it.TopNz[x] + it.LeftNz[y];
                        bool nonZero = this.TrellisQuantizeBlock(tmp, n * 16, rd.YAcLevels, n * 16, ctx, TypeI16Ac, dqm.Y1, dqm.LambdaTrellisI16);
                        it.TopNz[x] = it.LeftNz[y] = nonZero ? 1 : 0;
                        rd.YAcLevels[n * 16] = 0;
                        nz |= (nonZero ? 1u : 0u) << n;
                    }
                }
            }
            else
            {
                for (int n = 0; n < 16; n += 2)
                {
                    tmp[n * 16] = tmp[(n + 1) * 16] = 0;
                    nz |= (uint)Quantize2Blocks(tmp.AsSpan(n * 16, 32), rd.YAcLevels.AsSpan(n * 16, 32), dqm.Y1) << n;
                }
            }

            TransformWht(dcTmp, tmp);
            for (int n = 0; n < 16; n += 2)
            {
                ITransform(it.YuvP, reference + scan[n], tmp.AsSpan(n * 16), yuvOut, YOffEnc + scan[n], doTwo: true);
            }

            return nz;
        }

        // ReconstructIntra4
        private bool ReconstructIntra4(
            Iterator it,
            short[] levels,
            int levelsAt,
            int src,
            Place yuvOut,
            int mode)
        {
            int reference = I4ModeOffsets[mode];
            var dqm = this.Dqm[this.MbSegment[it.Mb]];
            var tmp = this.reconTmp4;
            if (SimdKernels)
            {
                // (the kernels straight, without the span wrappers)
                ref byte prediction = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(it.YuvP), reference);
                ref short coeffs = ref MemoryMarshal.GetArrayDataReference(tmp);
                FTransformSimd(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(it.YuvIn), src), ref prediction, ref coeffs, two: false);
                bool nonZero;
                if (it.DoTrellis)
                {
                    int ctx = it.TopNz[it.I4 & 3] + it.LeftNz[it.I4 >> 2];
                    nonZero = this.TrellisQuantizeBlock(tmp, 0, levels, levelsAt, ctx, TypeI4Ac, dqm.Y1, dqm.LambdaTrellisI4);
                }
                else
                {
                    nonZero = QuantizeBlockSimd(ref coeffs, ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(levels), levelsAt), dqm.Y1);
                }

                ITransformSimd(ref prediction, ref coeffs, ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(yuvOut.Array), yuvOut.At), two: false);
                return nonZero;
            }

            FTransform(it.YuvIn, src, it.YuvP, reference, tmp);
            bool nz;
            if (it.DoTrellis)
            {
                int x = it.I4 & 3, y = it.I4 >> 2;
                int ctx = it.TopNz[x] + it.LeftNz[y];
                nz = this.TrellisQuantizeBlock(tmp, 0, levels, levelsAt, ctx, TypeI4Ac, dqm.Y1, dqm.LambdaTrellisI4);
            }
            else
            {
                nz = QuantizeBlock(tmp, levels.AsSpan(levelsAt, 16), dqm.Y1);
            }

            ITransform(it.YuvP, reference, tmp, yuvOut.Array, yuvOut.At, doTwo: false);
            return nz;
        }

        // QuantizeSingle: one DC, with its error
        private static int QuantizeSingle(
            Span<short> v,
            int at,
            Matrix mtx)
        {
            int value = v[at];
            bool sign = value < 0;
            if (sign)
            {
                value = -value;
            }

            if (value > (int)mtx.ZThresh[0])
            {
                int qv = QuantDiv((uint)value, mtx.Iq[0], mtx.Bias[0]) * mtx.Q[0];
                int err = value - qv;
                v[at] = (short)(sign ? -qv : qv);
                return (sign ? -err : err) >> 1;
            }

            v[at] = 0;
            return (sign ? -value : value) >> 1;
        }

        // CorrectDCValues: the chroma DCs with the diffused errors of their neighbors
        private void CorrectDcValues(
            Iterator it,
            Matrix mtx,
            Span<short> tmp,
            ModeScore rd)
        {
            var topDerr = this.TopDerr!;
            for (int ch = 0; ch <= 1; ch++)
            {
                int top = (it.X * 4) + (ch * 2);
                int left = ch * 2;
                var c = tmp.Slice(ch * 4 * 16);
                c[0] += (short)(((7 * topDerr[top]) + (8 * it.LeftDerr[left])) >> 3);
                int err0 = QuantizeSingle(c, 0, mtx);
                c[16] += (short)(((7 * topDerr[top + 1]) + (8 * err0)) >> 3);
                int err1 = QuantizeSingle(c, 16, mtx);
                c[32] += (short)(((7 * err0) + (8 * it.LeftDerr[left + 1])) >> 3);
                int err2 = QuantizeSingle(c, 32, mtx);
                c[48] += (short)(((7 * err1) + (8 * err2)) >> 3);
                int err3 = QuantizeSingle(c, 48, mtx);
                rd.Derr[(ch * 3) + 0] = (sbyte)err1;
                rd.Derr[(ch * 3) + 1] = (sbyte)err2;
                rd.Derr[(ch * 3) + 2] = (sbyte)err3;
            }
        }

        private void StoreDiffusionErrors(
            Iterator it,
            ModeScore rd)
        {
            var topDerr = this.TopDerr!;
            for (int ch = 0; ch <= 1; ch++)
            {
                int top = (it.X * 4) + (ch * 2);
                int left = ch * 2;
                it.LeftDerr[left] = rd.Derr[(ch * 3) + 0];
                it.LeftDerr[left + 1] = (sbyte)((3 * rd.Derr[(ch * 3) + 2]) >> 2);
                topDerr[top] = rd.Derr[(ch * 3) + 1];
                topDerr[top + 1] = (sbyte)(rd.Derr[(ch * 3) + 2] - it.LeftDerr[left + 1]);
            }
        }

        // ReconstructUV
        private uint ReconstructUv(
            Iterator it,
            ModeScore rd,
            Place yuvOut,
            int mode)
        {
            int reference = UvModeOffsets[mode];
            var dqm = this.Dqm[this.MbSegment[it.Mb]];
            uint nz = 0;
            var tmp = this.reconTmp;
            var scanUv = ScanUv;
            for (int n = 0; n < 8; n += 2)
            {
                FTransform2(it.YuvIn, UOffEnc + scanUv[n], it.YuvP, reference + scanUv[n], tmp.AsSpan(n * 16));
            }

            if (this.TopDerr is not null)
            {
                this.CorrectDcValues(it, dqm.Uv, tmp, rd);
            }

            for (int n = 0; n < 8; n += 2)
            {
                nz |= (uint)Quantize2Blocks(tmp.AsSpan(n * 16, 32), rd.UvLevels.AsSpan(n * 16, 32), dqm.Uv) << n;
            }

            for (int n = 0; n < 8; n += 2)
            {
                ITransform(it.YuvP, reference + scanUv[n], tmp.AsSpan(n * 16), yuvOut.Array, yuvOut.At + scanUv[n], doTwo: true);
            }

            return nz << 16;
        }

        private static void StoreMaxDelta(
            SegmentInfo dqm,
            short[] dcs)
        {
            int v0 = Math.Abs((int)dcs[1]);
            int v1 = Math.Abs((int)dcs[2]);
            int v2 = Math.Abs((int)dcs[4]);
            int maxV = (v1 > v0) ? v1 : v0;
            maxV = (v2 > maxV) ? v2 : maxV;
            if (maxV > dqm.MaxEdge)
            {
                dqm.MaxEdge = maxV;
            }
        }
    }
}
