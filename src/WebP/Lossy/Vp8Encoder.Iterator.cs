// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/iterator_enc.c), modified by Hot Chai Games LLC: written in C#.
// libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    private static ReadOnlySpan<byte> TopLeftI4 => [17, 21, 25, 29, 13, 17, 21, 25, 9, 13, 17, 21, 5, 9, 13, 17];

    // The left samples' layout: y from index 1 (y[-1] at 0), u from 33, v from 49 (v[-1] is u[15])
    private const int YLeft = 1;
    private const int ULeft = YLeft + 32;
    private const int VLeft = ULeft + 16;

    /// <summary>The macroblock iterator (VP8EncIterator).</summary>
    private sealed class Iterator
    {
        public readonly Encoder Enc;
        public int X;
        public int Y;
        public byte[] YuvIn = new byte[YuvSizeEnc];
        public byte[] YuvOut = new byte[YuvSizeEnc];
        public byte[] YuvOut2 = new byte[YuvSizeEnc];
        public readonly byte[] YuvP = new byte[PredSizeEnc];
        public int Part;                 // the token partition of this row
        public int Preds;                // index in Enc's preds of this macroblock's first
        public int Mb;                   // this macroblock's index
        public readonly byte[] I4Boundary = new byte[40];
        public int I4Top;
        public int I4;
        public readonly int[] TopNz = new int[9];
        public readonly int[] LeftNz = new int[9];
        public bool DoTrellis;
        public int CountDown;
        public readonly sbyte[] LeftDerr = new sbyte[4];  // [u/v][2]
        public readonly bool HasTopDerr;
        public readonly byte[] Left = new byte[64];

        // The top samples: the encoder's rows (at x), or a scratch copy from the picture (analysis)
        public byte[] YTopArray;
        public int YTopAt;
        public byte[] UvTopArray;
        public int UvTopAt;

        public Iterator(
            Encoder enc)
        {
            this.Enc = enc;
            this.HasTopDerr = enc.TopDerr is not null;
            this.YTopArray = enc.YTop;
            this.UvTopArray = enc.UvTop;
            this.Reset();
        }

        // VP8IteratorReset
        private void Reset()
        {
            this.SetRow(0);
            this.CountDown = this.Enc.MbW * this.Enc.MbH;
            this.Enc.YTop.AsSpan().Fill(127);
            this.Enc.UvTop.AsSpan().Fill(127);
            this.Enc.Nz.AsSpan(1).Clear();
            if (this.Enc.TopDerr is not null)
            {
                this.Enc.TopDerr.AsSpan().Clear();
            }

            this.DoTrellis = false;
        }

        // InitLeft
        private void InitLeft()
        {
            byte corner = (this.Y > 0) ? (byte)129 : (byte)127;
            this.Left[YLeft - 1] = corner;
            this.Left[ULeft - 1] = corner;
            this.Left[VLeft - 1] = corner;
            this.Left.AsSpan(YLeft, 16).Fill(129);
            this.Left.AsSpan(ULeft, 8).Fill(129);
            this.Left.AsSpan(VLeft, 8).Fill(129);
            this.LeftNz[8] = 0;
            if (this.HasTopDerr)
            {
                this.LeftDerr.AsSpan().Clear();
            }
        }

        // VP8IteratorSetRow
        public void SetRow(
            int y)
        {
            this.X = 0;
            this.Y = y;
            this.Part = y & (this.Enc.NumParts - 1);
            this.Preds = this.Enc.PredsBase + (y * 4 * this.Enc.PredsW);
            this.Mb = y * this.Enc.MbW;
            this.YTopArray = this.Enc.YTop;
            this.YTopAt = 0;
            this.UvTopArray = this.Enc.UvTop;
            this.UvTopAt = 0;
            this.InitLeft();
        }

        public int NzIndex => 1 + this.X;

        // VP8IteratorImport: the macroblock's source samples, edges replicated; with scratch (the
        // analysis), the left and top samples from the picture too
        public void Import(
            byte[]? scratch)
        {
            var pic = this.Enc.Pic;
            int x = this.X, y = this.Y;
            int ysrc = ((y * pic.YStride) + x) * 16;
            int uvsrc = ((y * pic.UvStride) + x) * 8;
            int w = Math.Min(pic.Width - (x * 16), 16);
            int h = Math.Min(pic.Height - (y * 16), 16);
            int uvW = (w + 1) >> 1;
            int uvH = (h + 1) >> 1;
            ImportBlock(pic.Y, ysrc, pic.YStride, this.YuvIn, YOffEnc, w, h, 16);
            ImportBlock(pic.U, uvsrc, pic.UvStride, this.YuvIn, UOffEnc, uvW, uvH, 8);
            ImportBlock(pic.V, uvsrc, pic.UvStride, this.YuvIn, VOffEnc, uvW, uvH, 8);
            if (scratch is null)
            {
                return;
            }

            if (x == 0)
            {
                this.InitLeft();
            }
            else
            {
                if (y == 0)
                {
                    this.Left[YLeft - 1] = 127;
                    this.Left[ULeft - 1] = 127;
                    this.Left[VLeft - 1] = 127;
                }
                else
                {
                    this.Left[YLeft - 1] = pic.Y[ysrc - 1 - pic.YStride];
                    this.Left[ULeft - 1] = pic.U[uvsrc - 1 - pic.UvStride];
                    this.Left[VLeft - 1] = pic.V[uvsrc - 1 - pic.UvStride];
                }

                ImportLine(pic.Y, ysrc - 1, pic.YStride, this.Left, YLeft, h, 16);
                ImportLine(pic.U, uvsrc - 1, pic.UvStride, this.Left, ULeft, uvH, 8);
                ImportLine(pic.V, uvsrc - 1, pic.UvStride, this.Left, VLeft, uvH, 8);
            }

            this.YTopArray = scratch;
            this.YTopAt = 0;
            this.UvTopArray = scratch;
            this.UvTopAt = 16;
            if (y == 0)
            {
                scratch.AsSpan(0, 32).Fill(127);
            }
            else
            {
                ImportLine(pic.Y, ysrc - pic.YStride, 1, scratch, 0, w, 16);
                ImportLine(pic.U, uvsrc - pic.UvStride, 1, scratch, 16, uvW, 8);
                ImportLine(pic.V, uvsrc - pic.UvStride, 1, scratch, 16 + 8, uvW, 8);
            }
        }

        private static void ImportBlock(
            byte[] src,
            int s,
            int srcStride,
            byte[] dst,
            int d,
            int w,
            int h,
            int size)
        {
            for (int i = 0; i < h; i++)
            {
                src.AsSpan(s, w).CopyTo(dst.AsSpan(d));
                if (w < size)
                {
                    dst.AsSpan(d + w, size - w).Fill(dst[d + w - 1]);
                }

                d += Bps;
                s += srcStride;
            }

            for (int i = h; i < size; i++)
            {
                dst.AsSpan(d - Bps, size).CopyTo(dst.AsSpan(d));
                d += Bps;
            }
        }

        private static void ImportLine(
            byte[] src,
            int s,
            int srcStride,
            byte[] dst,
            int d,
            int length,
            int totalLength)
        {
            int i;
            for (i = 0; i < length; i++, s += srcStride)
            {
                dst[d + i] = src[s];
            }

            for (; i < totalLength; i++)
            {
                dst[d + i] = dst[d + length - 1];
            }
        }

        // VP8IteratorNzToBytes
        public void NzToBytes()
        {
            uint tnz = this.Enc.Nz[this.NzIndex];
            uint lnz = this.Enc.Nz[this.NzIndex - 1];
            var t = this.TopNz;
            var l = this.LeftNz;
            t[0] = Bit(tnz, 12);
            t[1] = Bit(tnz, 13);
            t[2] = Bit(tnz, 14);
            t[3] = Bit(tnz, 15);
            t[4] = Bit(tnz, 18);
            t[5] = Bit(tnz, 19);
            t[6] = Bit(tnz, 22);
            t[7] = Bit(tnz, 23);
            t[8] = Bit(tnz, 24);
            l[0] = Bit(lnz, 3);
            l[1] = Bit(lnz, 7);
            l[2] = Bit(lnz, 11);
            l[3] = Bit(lnz, 15);
            l[4] = Bit(lnz, 17);
            l[5] = Bit(lnz, 19);
            l[6] = Bit(lnz, 21);
            l[7] = Bit(lnz, 23);
        }

        private static int Bit(
            uint nz,
            int n)
        {
            return (int)((nz >> n) & 1);
        }

        // VP8IteratorBytesToNz
        public void BytesToNz()
        {
            var t = this.TopNz;
            var l = this.LeftNz;
            uint nz = 0;
            nz |= (uint)((t[0] << 12) | (t[1] << 13));
            nz |= (uint)((t[2] << 14) | (t[3] << 15));
            nz |= (uint)((t[4] << 18) | (t[5] << 19));
            nz |= (uint)((t[6] << 22) | (t[7] << 23));
            nz |= (uint)(t[8] << 24);
            nz |= (uint)((l[0] << 3) | (l[1] << 7));
            nz |= (uint)(l[2] << 11);
            nz |= (uint)((l[4] << 17) | (l[6] << 21));
            this.Enc.Nz[this.NzIndex] = nz;
        }

        // VP8IteratorSaveBoundary
        public void SaveBoundary()
        {
            var enc = this.Enc;
            var output = this.YuvOut;
            if (this.X < enc.MbW - 1)
            {
                for (int i = 0; i < 16; i++)
                {
                    this.Left[YLeft + i] = output[YOffEnc + 15 + (i * Bps)];
                }

                for (int i = 0; i < 8; i++)
                {
                    this.Left[ULeft + i] = output[UOffEnc + 7 + (i * Bps)];
                    this.Left[VLeft + i] = output[UOffEnc + 15 + (i * Bps)];
                }

                this.Left[YLeft - 1] = this.YTopArray[this.YTopAt + 15];
                this.Left[ULeft - 1] = this.UvTopArray[this.UvTopAt + 0 + 7];
                this.Left[VLeft - 1] = this.UvTopArray[this.UvTopAt + 8 + 7];
            }

            if (this.Y < enc.MbH - 1)
            {
                output.AsSpan(YOffEnc + (15 * Bps), 16).CopyTo(this.YTopArray.AsSpan(this.YTopAt));
                output.AsSpan(UOffEnc + (7 * Bps), 16).CopyTo(this.UvTopArray.AsSpan(this.UvTopAt));
            }
        }

        // VP8IteratorNext
        public bool Next()
        {
            if (++this.X == this.Enc.MbW)
            {
                this.SetRow(++this.Y);
            }
            else
            {
                this.Preds += 4;
                this.Mb += 1;
                this.YTopAt += 16;
                this.UvTopAt += 16;
            }

            return 0 < --this.CountDown;
        }

        public void SetIntra16Mode(
            int mode)
        {
            int p = this.Preds;
            for (int y = 0; y < 4; y++)
            {
                this.Enc.Preds.AsSpan(p, 4).Fill((byte)mode);
                p += this.Enc.PredsW;
            }

            this.Enc.MbType[this.Mb] = 1;
        }

        public void SetIntra4Mode(
            ReadOnlySpan<byte> modes)
        {
            int p = this.Preds;
            for (int y = 0; y < 4; y++)
            {
                modes.Slice(y * 4, 4).CopyTo(this.Enc.Preds.AsSpan(p));
                p += this.Enc.PredsW;
            }

            this.Enc.MbType[this.Mb] = 0;
        }

        // VP8IteratorStartI4
        public void StartI4()
        {
            var enc = this.Enc;
            this.I4 = 0;
            this.I4Top = TopLeftI4[0];
            for (int i = 0; i < 17; i++)
            {
                this.I4Boundary[i] = this.Left[YLeft + 15 - i];
            }

            for (int i = 0; i < 16; i++)
            {
                this.I4Boundary[17 + i] = this.YTopArray[this.YTopAt + i];
            }

            if (this.X < enc.MbW - 1)
            {
                for (int i = 16; i < 16 + 4; i++)
                {
                    this.I4Boundary[17 + i] = this.YTopArray[this.YTopAt + i];
                }
            }
            else
            {
                for (int i = 16; i < 16 + 4; i++)
                {
                    this.I4Boundary[17 + i] = this.I4Boundary[17 + 15];
                }
            }

            this.NzToBytes();
        }

        // VP8IteratorRotateI4
        public bool RotateI4(
            byte[] yuvOut)
        {
            int blk = YOffEnc + Scan[this.I4];
            int top = this.I4Top;
            for (int i = 0; i <= 3; i++)
            {
                this.I4Boundary[top - 4 + i] = yuvOut[blk + i + (3 * Bps)];
            }

            if ((this.I4 & 3) != 3)
            {
                for (int i = 0; i <= 2; i++)
                {
                    this.I4Boundary[top + i] = yuvOut[blk + 3 + ((2 - i) * Bps)];
                }
            }
            else
            {
                for (int i = 0; i <= 3; i++)
                {
                    this.I4Boundary[top + i] = this.I4Boundary[top + i + 4];
                }
            }

            this.I4++;
            if (this.I4 == 16)
            {
                return false;
            }

            this.I4Top = TopLeftI4[this.I4];
            return true;
        }

        public void SwapOut()
        {
            (this.YuvOut, this.YuvOut2) = (this.YuvOut2, this.YuvOut);
        }
    }
}
