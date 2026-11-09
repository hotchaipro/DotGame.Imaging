// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/io_dec.c, src/dsp/upsampling.c and src/dsp/yuv.h), modified by Hot
// Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;

namespace DotGame.Imaging.WebP;

// The rows to RGBA: each pair of rows with the chroma rows above and below them, fancy upsampled
// (EmitFancyRGB), and YUV to RGB with libwebp's 14-bit arithmetic (yuv.h)
internal static partial class Vp8Decoder
{
    private const int YuvFix2 = 6;
    private const int YuvMask2 = (256 << YuvFix2) - 1;

    private ref struct Output
    {
        private readonly Span<byte> rgba;
        private readonly int width;
        private readonly int height;

        // The last luma row and chroma row of the rows emitted so far, for the next rows' first pair
        private readonly byte[] tmpY;
        private readonly byte[] tmpU;
        private readonly byte[] tmpV;

        public Output(
            int width,
            int height,
            Span<byte> rgba)
        {
            this.rgba = rgba;
            this.width = width;
            this.height = height;
            int uvWidth = (width + 1) / 2;
            this.tmpY = new byte[width + RowPadding];
            this.tmpU = new byte[uvWidth + RowPadding];
            this.tmpV = new byte[uvWidth + RowPadding];
        }

        // EmitFancyRGB: rows y to y + count of the image, from the cache rows at yRow and uvRow
        public void Emit(
            byte[] cy,
            int yRow,
            int yStride,
            byte[] cu,
            byte[] cv,
            int uvRow,
            int uvStride,
            int y,
            int count)
        {
            int stride = this.width * 4;
            int dst = y * stride;
            int yEnd = y + count;
            int uvWidth = (this.width + 1) / 2;
            var curY = cy.AsSpan();
            int curYAt = yRow;
            int curUvAt = uvRow;
            ReadOnlySpan<byte> topU = this.tmpU;
            ReadOnlySpan<byte> topV = this.tmpV;
            if (y == 0)
            {
                UpsampleLinePair(cy.AsSpan(curYAt, this.width), default, cu.AsSpan(curUvAt, uvWidth), cv.AsSpan(curUvAt, uvWidth), cu.AsSpan(curUvAt, uvWidth), cv.AsSpan(curUvAt, uvWidth), this.rgba.Slice(dst, stride), default, this.width);
            }
            else
            {
                UpsampleLinePair(this.tmpY, cy.AsSpan(curYAt, this.width), topU, topV, cu.AsSpan(curUvAt, uvWidth), cv.AsSpan(curUvAt, uvWidth), this.rgba.Slice(dst - stride, stride), this.rgba.Slice(dst, stride), this.width);
            }

            for (; y + 2 < yEnd; y += 2)
            {
                int topUvAt = curUvAt;
                curUvAt += uvStride;
                dst += 2 * stride;
                curYAt += 2 * yStride;
                UpsampleLinePair(cy.AsSpan(curYAt - yStride, this.width), cy.AsSpan(curYAt, this.width), cu.AsSpan(topUvAt, uvWidth), cv.AsSpan(topUvAt, uvWidth), cu.AsSpan(curUvAt, uvWidth), cv.AsSpan(curUvAt, uvWidth), this.rgba.Slice(dst - stride, stride), this.rgba.Slice(dst, stride), this.width);
            }

            curYAt += yStride;
            if (yEnd < this.height)
            {
                curY.Slice(curYAt, this.width).CopyTo(this.tmpY);
                cu.AsSpan(curUvAt, uvWidth).CopyTo(this.tmpU);
                cv.AsSpan(curUvAt, uvWidth).CopyTo(this.tmpV);
            }
            else if ((yEnd & 1) == 0)
            {
                UpsampleLinePair(cy.AsSpan(curYAt, this.width), default, cu.AsSpan(curUvAt, uvWidth), cv.AsSpan(curUvAt, uvWidth), cu.AsSpan(curUvAt, uvWidth), cv.AsSpan(curUvAt, uvWidth), this.rgba.Slice(dst + stride, stride), default, this.width);
            }
        }
    }

    // UpsampleRgbaLinePair_C: two rows (the bottom one may be missing), each pixel's chroma from the
    // four nearest chroma samples, weighted 9:3:3:1
    private static void UpsampleLinePair(
        ReadOnlySpan<byte> topY,
        ReadOnlySpan<byte> bottomY,
        ReadOnlySpan<byte> topU,
        ReadOnlySpan<byte> topV,
        ReadOnlySpan<byte> curU,
        ReadOnlySpan<byte> curV,
        Span<byte> topDst,
        Span<byte> bottomDst,
        int length)
    {
        bool hasBottom = !bottomY.IsEmpty;
        int lastPixelPair = (length - 1) >> 1;
        uint tlUv = (uint)(topU[0] | (topV[0] << 16));
        uint lUv = (uint)(curU[0] | (curV[0] << 16));
        {
            uint uv0 = ((3 * tlUv) + lUv + 0x00020002u) >> 2;
            YuvToRgba(topY[0], (int)(uv0 & 0xff), (int)(uv0 >> 16), topDst);
        }

        if (hasBottom)
        {
            uint uv0 = ((3 * lUv) + tlUv + 0x00020002u) >> 2;
            YuvToRgba(bottomY[0], (int)(uv0 & 0xff), (int)(uv0 >> 16), bottomDst);
        }

        // Blocks of 16 pixels in vectors, then the rest here, from where they stopped
        int xStart = 1;
        if (VectorUpsampling && (((((length + 1) >> 1) - 1) >> 3) > 0))
        {
            xStart = UpsampleLinePairVector(topY, bottomY, topU, topV, curU, curV, topDst, bottomDst, length);
            tlUv = (uint)(topU[xStart - 1] | (topV[xStart - 1] << 16));
            lUv = (uint)(curU[xStart - 1] | (curV[xStart - 1] << 16));
        }

        for (int x = xStart; x <= lastPixelPair; x++)
        {
            uint tUv = (uint)(topU[x] | (topV[x] << 16));
            uint uv = (uint)(curU[x] | (curV[x] << 16));
            uint avg = tlUv + tUv + lUv + uv + 0x00080008u;
            uint diag12 = (avg + (2 * (tUv + lUv))) >> 3;
            uint diag03 = (avg + (2 * (tlUv + uv))) >> 3;
            {
                uint uv0 = (diag12 + tlUv) >> 1;
                uint uv1 = (diag03 + tUv) >> 1;
                YuvToRgba(topY[(2 * x) - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16), topDst.Slice(((2 * x) - 1) * 4));
                YuvToRgba(topY[2 * x], (int)(uv1 & 0xff), (int)(uv1 >> 16), topDst.Slice(2 * x * 4));
            }

            if (hasBottom)
            {
                uint uv0 = (diag03 + lUv) >> 1;
                uint uv1 = (diag12 + uv) >> 1;
                YuvToRgba(bottomY[(2 * x) - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16), bottomDst.Slice(((2 * x) - 1) * 4));
                YuvToRgba(bottomY[2 * x], (int)(uv1 & 0xff), (int)(uv1 >> 16), bottomDst.Slice(2 * x * 4));
            }

            tlUv = tUv;
            lUv = uv;
        }

        if ((length & 1) == 0)
        {
            {
                uint uv0 = ((3 * tlUv) + lUv + 0x00020002u) >> 2;
                YuvToRgba(topY[length - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16), topDst.Slice((length - 1) * 4));
            }

            if (hasBottom)
            {
                uint uv0 = ((3 * lUv) + tlUv + 0x00020002u) >> 2;
                YuvToRgba(bottomY[length - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16), bottomDst.Slice((length - 1) * 4));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MultHi(
        int v,
        int coefficient)
    {
        return (v * coefficient) >> 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clip8Yuv(
        int v)
    {
        return ((v & ~YuvMask2) == 0) ? (v >> YuvFix2) : (v < 0) ? 0 : 255;
    }

    // VP8YuvToRgba
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void YuvToRgba(
        int y,
        int u,
        int v,
        Span<byte> rgba)
    {
        rgba[0] = (byte)Clip8Yuv(MultHi(y, 19077) + MultHi(v, 26149) - 14234);
        rgba[1] = (byte)Clip8Yuv(MultHi(y, 19077) - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
        rgba[2] = (byte)Clip8Yuv(MultHi(y, 19077) + MultHi(u, 33050) - 17685);
        rgba[3] = 0xff;
    }
}
