// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (sharpyuv/sharpyuv.c, sharpyuv_dsp.c and sharpyuv_gamma.c's sRGB path),
// modified by Hot Chai Games LLC: written in C#, for 8-bit RGB to 8-bit YUV with the WebP matrix only.
// libwebp carries this notice:
//   Copyright 2022 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;

namespace DotGame.Imaging.WebP;

/// <summary>
/// Sharp RGB to YUV 4:2:0 (cwebp's -sharp_yuv): the luma and chroma refined over a few iterations so
/// the upsampled result is closer to the source than plain averaging gives.
/// </summary>
internal static partial class SharpYuv
{
    private const int NumIterations = 4;
    private const int YuvFix = 16;
    private const int YuvHalf = 1 << (YuvFix - 1);
    private const int PrecisionShift = 2;            // GetPrecisionShift(8)
    private const int BitDepth = 8 + PrecisionShift; // the working precision
    private const int MaxY = (1 << BitDepth) - 1;

    // kWebpMatrix, with the constants scaled by the precision shift
    private const int YR = 16839, YG = 33059, YB = 6420, YC = (16 << 16) << PrecisionShift;
    private const int UR = -9719, UG = -19081, UB = 28800, UC = (128 << 16) << PrecisionShift;
    private const int VR = 28800, VG = -24116, VB = -4684, VC = (128 << 16) << PrecisionShift;

    /// <summary>Converts the RGBA (the alpha ignored) into the picture's Y, U and V.</summary>
    public static void Convert(
        ReadOnlySpan<byte> rgba,
        int stride,
        int width,
        int height,
        YuvPicture picture)
    {
        int w = (width + 1) & ~1;
        int h = (height + 1) & ~1;
        int uvW = w >> 1;
        int uvH = h >> 1;
        ulong prevDiffYSum = ulong.MaxValue;
        var tmp = new ushort[w * 3 * 2];
        var bestY = new ushort[w * h];
        var targetY = new ushort[w * h];
        var bestRgbY = new ushort[w * 2];
        var bestUv = new short[uvW * 3 * uvH];
        var targetUv = new short[uvW * 3 * uvH];
        var bestRgbUv = new short[uvW * 3];
        ulong diffYThreshold = (ulong)(3.0 * w * h);
        int src1 = 0, src2 = 3 * w;
        for (int j = 0, yAt = 0, uvAt = 0; j < height; j += 2, yAt += 2 * w, uvAt += 3 * uvW)
        {
            bool isLastRow = j == height - 1;
            ImportOneRow(rgba.Slice(j * stride), width, tmp, src1, w);
            if (!isLastRow)
            {
                ImportOneRow(rgba.Slice((j + 1) * stride), width, tmp, src2, w);
            }
            else
            {
                tmp.AsSpan(src1, 3 * w).CopyTo(tmp.AsSpan(src2));
            }

            StoreGray(tmp, src1, bestY, yAt, w);
            StoreGray(tmp, src2, bestY, yAt + w, w);
            UpdateW(tmp, src1, targetY, yAt, w);
            UpdateW(tmp, src2, targetY, yAt + w, w);
            UpdateChroma(tmp, src1, src2, targetUv, uvAt, uvW);
            targetUv.AsSpan(uvAt, 3 * uvW).CopyTo(bestUv.AsSpan(uvAt));
        }

        for (int iter = 0; iter < NumIterations; iter++)
        {
            int curUv = 0, prevUv = 0;
            ulong diffYSum = 0;
            int j = 0, yAt = 0, uvAt = 0;
            do
            {
                int nextUv = curUv + ((j < h - 2) ? 3 * uvW : 0);
                InterpolateTwoRows(bestY, yAt, bestUv, prevUv, curUv, nextUv, w, tmp, src1, src2);
                prevUv = curUv;
                curUv = nextUv;
                UpdateW(tmp, src1, bestRgbY, 0, w);
                UpdateW(tmp, src2, bestRgbY, w, w);
                UpdateChroma(tmp, src1, src2, bestRgbUv, 0, uvW);
                diffYSum += UpdateY(targetY, yAt, bestRgbY, bestY, 2 * w);
                for (int i = 0; i < 3 * uvW; i++)
                {
                    bestUv[uvAt + i] += (short)(targetUv[uvAt + i] - bestRgbUv[i]);
                }

                yAt += 2 * w;
                uvAt += 3 * uvW;
                j += 2;
            }
            while (j < h);

            if (iter > 0)
            {
                if (diffYSum < diffYThreshold)
                {
                    break;
                }

                if (diffYSum > prevDiffYSum)
                {
                    break;
                }
            }

            prevDiffYSum = diffYSum;
        }

        ConvertWrgbToYuv(bestY, bestUv, picture, width, height);
    }

    private static int RgbToGray(
        long r,
        long g,
        long b)
    {
        long luma = (13933 * r) + (46871 * g) + (4732 * b) + YuvHalf;
        return (int)(luma >> YuvFix);
    }

    // ToLinearSrgb at the working precision: the table directly
    private static uint GammaToLinear(
        int v)
    {
        return GammaToLinearTabS[v];
    }

    // FromLinearSrgb: FixedPointInterpolation(value, kLinearToGammaTabS, 16 - 9, BitDepth - 16)
    private static uint LinearToGamma(
        uint value)
    {
        const int shiftRight = 16 - 9;
        const int valueShift = 16 - BitDepth;
        var tab = LinearToGammaTabS;
        uint tabPos = value >> shiftRight;
        uint x = value - (tabPos << shiftRight);
        uint v0 = tab[(int)tabPos] >> valueShift;
        uint v1 = tab[(int)tabPos + 1] >> valueShift;
        uint v2 = (v1 - v0) * x;
        const uint half = 1 << (shiftRight - 1);
        return v0 + ((v2 + half) >> shiftRight);
    }

    private static uint ScaleDown(
        int a,
        int b,
        int c,
        int d)
    {
        uint la = GammaToLinear(a);
        uint lb = GammaToLinear(b);
        uint lc = GammaToLinear(c);
        uint ld = GammaToLinear(d);
        return LinearToGamma((la + lb + lc + ld + 2) >> 2);
    }

    private static void UpdateW(
        ushort[] src,
        int s,
        ushort[] dst,
        int d,
        int w)
    {
        for (int i = 0; i < w; i++)
        {
            uint r = GammaToLinear(src[s + i]);
            uint g = GammaToLinear(src[s + w + i]);
            uint b = GammaToLinear(src[s + (2 * w) + i]);
            int y = RgbToGray(r, g, b);
            dst[d + i] = (ushort)LinearToGamma((uint)y);
        }
    }

    private static void UpdateChroma(
        ushort[] src,
        int src1,
        int src2,
        short[] dst,
        int d,
        int uvW)
    {
        for (int i = 0; i < uvW; i++)
        {
            int r = (int)ScaleDown(src[src1 + (2 * i)], src[src1 + (2 * i) + 1], src[src2 + (2 * i)], src[src2 + (2 * i) + 1]);
            int g = (int)ScaleDown(src[src1 + (2 * uvW) + (2 * i)], src[src1 + (2 * uvW) + (2 * i) + 1], src[src2 + (2 * uvW) + (2 * i)], src[src2 + (2 * uvW) + (2 * i) + 1]);
            int b = (int)ScaleDown(src[src1 + (4 * uvW) + (2 * i)], src[src1 + (4 * uvW) + (2 * i) + 1], src[src2 + (4 * uvW) + (2 * i)], src[src2 + (4 * uvW) + (2 * i) + 1]);
            int gray = RgbToGray(r, g, b);
            dst[d + i] = (short)(r - gray);
            dst[d + uvW + i] = (short)(g - gray);
            dst[d + (2 * uvW) + i] = (short)(b - gray);
        }
    }

    private static void StoreGray(
        ushort[] rgb,
        int s,
        ushort[] y,
        int d,
        int w)
    {
        for (int i = 0; i < w; i++)
        {
            y[d + i] = (ushort)RgbToGray(rgb[s + i], rgb[s + w + i], rgb[s + (2 * w) + i]);
        }
    }

    private static ushort ClipY(
        int y)
    {
        return ((y & ~MaxY) == 0) ? (ushort)y : (y < 0) ? (ushort)0 : (ushort)MaxY;
    }

    private static ushort Filter2(
        int a,
        int b,
        int w0)
    {
        int v0 = ((a * 3) + b + 2) >> 2;
        return ClipY(v0 + w0);
    }

    // ImportOneRow: the row's R, G and B planes at the working precision, odd widths replicated
    private static void ImportOneRow(
        ReadOnlySpan<byte> row,
        int width,
        ushort[] dst,
        int d,
        int w)
    {
        for (int i = 0; i < width; i++)
        {
            dst[d + i] = (ushort)(row[(4 * i) + 0] << PrecisionShift);
            dst[d + w + i] = (ushort)(row[(4 * i) + 1] << PrecisionShift);
            dst[d + (2 * w) + i] = (ushort)(row[(4 * i) + 2] << PrecisionShift);
        }

        if ((width & 1) != 0)
        {
            dst[d + width] = dst[d + width - 1];
            dst[d + w + width] = dst[d + w + width - 1];
            dst[d + (2 * w) + width] = dst[d + (2 * w) + width - 1];
        }
    }

    private static void FilterRow(
        short[] uv,
        int a,
        int b,
        int len,
        ushort[] bestY,
        int y,
        ushort[] output,
        int o)
    {
        for (int i = 0; i < len; i++, a++, b++)
        {
            int v0 = ((uv[a] * 9) + (uv[a + 1] * 3) + (uv[b] * 3) + uv[b + 1] + 8) >> 4;
            int v1 = ((uv[a + 1] * 9) + (uv[a] * 3) + (uv[b + 1] * 3) + uv[b] + 8) >> 4;
            output[o + (2 * i)] = ClipY0(bestY[y + (2 * i)] + v0);
            output[o + (2 * i) + 1] = ClipY0(bestY[y + (2 * i) + 1] + v1);
        }
    }

    // sharpyuv_dsp.c's clip
    private static ushort ClipY0(
        int v)
    {
        return (v < 0) ? (ushort)0 : (v > MaxY) ? (ushort)MaxY : (ushort)v;
    }

    private static void InterpolateTwoRows(
        ushort[] bestY,
        int y,
        short[] uv,
        int prevUv,
        int curUv,
        int nextUv,
        int w,
        ushort[] output,
        int out1,
        int out2)
    {
        int uvW = w >> 1;
        int len = (w - 1) >> 1;
        for (int k = 0; k < 3; k++)
        {
            output[out1] = Filter2(uv[curUv], uv[prevUv], bestY[y]);
            output[out2] = Filter2(uv[curUv], uv[nextUv], bestY[y + w]);
            FilterRow(uv, curUv, prevUv, len, bestY, y + 1, output, out1 + 1);
            FilterRow(uv, curUv, nextUv, len, bestY, y + w + 1, output, out2 + 1);
            if ((w & 1) == 0)
            {
                output[out1 + w - 1] = Filter2(uv[curUv + uvW - 1], uv[prevUv + uvW - 1], bestY[y + w - 1]);
                output[out2 + w - 1] = Filter2(uv[curUv + uvW - 1], uv[nextUv + uvW - 1], bestY[y + w - 1 + w]);
            }

            out1 += w;
            out2 += w;
            prevUv += uvW;
            curUv += uvW;
            nextUv += uvW;
        }
    }

    // SharpYuvUpdateY
    private static ulong UpdateY(
        ushort[] reference,
        int r,
        ushort[] src,
        ushort[] dst,
        int len)
    {
        ulong diff = 0;
        for (int i = 0; i < len; i++)
        {
            int diffY = reference[r + i] - src[i];
            int newY = dst[r + i] + diffY;
            dst[r + i] = ClipY0(newY);
            diff += (ulong)Math.Abs(diffY);
        }

        return diff;
    }

    private static int RgbToYuvComponent(
        int r,
        int g,
        int b,
        int c0,
        int c1,
        int c2,
        int c3)
    {
        const int sfix = PrecisionShift;
        const int rounder = 1 << (YuvFix + sfix - 1);
        int luma = (c0 * r) + (c1 * g) + (c2 * b) + c3 + rounder;
        return luma >> (YuvFix + sfix);
    }

    private static byte Clip8(
        int v)
    {
        return ((v & ~0xff) == 0) ? (byte)v : (v < 0) ? (byte)0 : (byte)255;
    }

    // ConvertWRGBToYUV
    private static void ConvertWrgbToYuv(
        ushort[] bestY,
        short[] bestUv,
        YuvPicture picture,
        int width,
        int height)
    {
        int w = (width + 1) & ~1;
        int h = (height + 1) & ~1;
        int uvW = w >> 1;
        int uvH = h >> 1;
        int y = 0, uv = 0;
        for (int j = 0; j < height; j++)
        {
            int row = j * picture.YStride;
            for (int i = 0; i < width; i++)
            {
                int off = uv + (i >> 1);
                int gray = bestY[y + i];
                int r = bestUv[off] + gray;
                int g = bestUv[off + uvW] + gray;
                int b = bestUv[off + (2 * uvW)] + gray;
                picture.Y[row + i] = Clip8((short)RgbToYuvComponent(r, g, b, YR, YG, YB, YC));
            }

            y += w;
            uv += (j & 1) * 3 * uvW;
        }

        uv = 0;
        for (int j = 0; j < uvH; j++)
        {
            int row = j * picture.UvStride;
            for (int i = 0; i < uvW; i++)
            {
                int r = bestUv[uv + i];
                int g = bestUv[uv + uvW + i];
                int b = bestUv[uv + (2 * uvW) + i];
                picture.U[row + i] = Clip8((short)RgbToYuvComponent(r, g, b, UR, UG, UB, UC));
                picture.V[row + i] = Clip8((short)RgbToYuvComponent(r, g, b, VR, VG, VB, VC));
            }

            uv += 3 * uvW;
        }
    }
}
