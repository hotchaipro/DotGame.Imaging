// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/picture_csp_enc.c, picture_tools_enc.c and src/dsp/yuv.h),
// modified by Hot Chai Games LLC: written in C#, from RGBA only. libwebp carries this notice:
//   Copyright 2014 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.WebP;

/// <summary>
/// RGBA to the lossy encoder's YUV 4:2:0 and alpha planes, as libwebp's WebPPictureImportRGBA does
/// (ImportYUVAFromRGBA: luma per pixel, chroma from each 2x2 block's gamma-averaged color, weighted by
/// alpha where it isn't opaque), or with sharp YUV; and WebPCleanupTransparentArea.
/// </summary>
internal sealed class YuvPicture
{
    public int Width;
    public int Height;
    public byte[] Y = [];
    public byte[] U = [];
    public byte[] V = [];
    public byte[]? A;
    public int YStride;
    public int UvStride;

    private const int YuvFix = 16;
    private const int YuvHalf = 1 << (YuvFix - 1);
    private const int GammaTabFix = 7;
    private const int GammaTabScale = 1 << GammaTabFix;
    private const int GammaTabRounder = GammaTabScale >> 1;
    private const int AlphaFix = 19;

    /// <summary>The planes of RGBA pixels (width x height, 4 bytes each).</summary>
    public static YuvPicture FromRgba(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        bool sharpYuv)
    {
        var picture = new YuvPicture
        {
            Width = width,
            Height = height,
            YStride = width,
            UvStride = (width + 1) >> 1,
        };
        int uvHeight = (height + 1) >> 1;
        picture.Y = new byte[width * height];
        picture.U = new byte[picture.UvStride * uvHeight];
        picture.V = new byte[picture.UvStride * uvHeight];
        int stride = 4 * width;
        bool hasAlpha = HasTransparency(rgba);
        if (hasAlpha)
        {
            picture.A = new byte[width * height];
        }

        if (sharpYuv && (width >= 4) && (height >= 4))
        {
            SharpYuv.Convert(rgba, stride, width, height, picture);
            if (hasAlpha)
            {
                ExtractAlpha(rgba.Slice(3), stride, width, height, picture.A!, width);
            }

            return picture;
        }

        int uvWidth = (width + 1) >> 1;
        var tmp = new ushort[4 * uvWidth];
        int src = 0;
        int dstY = 0;
        int dstUv = 0;
        int dstA = 0;
        for (int y = 0; y < (height >> 1); y++)
        {
            bool rowsHaveAlpha = hasAlpha;
            ConvertRowToY(rgba.Slice(src), picture.Y.AsSpan(dstY, width), width);
            ConvertRowToY(rgba.Slice(src + stride), picture.Y.AsSpan(dstY + width, width), width);
            dstY += 2 * width;
            if (hasAlpha)
            {
                rowsHaveAlpha &= !ExtractAlpha(rgba.Slice(src + 3), stride, width, 2, picture.A.AsSpan(dstA), width);
                dstA += 2 * width;
            }

            if (!rowsHaveAlpha)
            {
                AccumulateRgb(rgba.Slice(src), stride, tmp, width);
            }
            else
            {
                AccumulateRgba(rgba.Slice(src), stride, tmp, width);
            }

            ConvertRowsToUv(tmp, picture.U.AsSpan(dstUv, uvWidth), picture.V.AsSpan(dstUv, uvWidth), uvWidth);
            dstUv += uvWidth;
            src += 2 * stride;
        }

        if ((height & 1) != 0)
        {
            bool rowHasAlpha = hasAlpha;
            ConvertRowToY(rgba.Slice(src), picture.Y.AsSpan(dstY, width), width);
            if (rowHasAlpha)
            {
                rowHasAlpha &= !ExtractAlpha(rgba.Slice(src + 3), 0, width, 1, picture.A.AsSpan(dstA), 0);
            }

            if (!rowHasAlpha)
            {
                AccumulateRgb(rgba.Slice(src), 0, tmp, width);
            }
            else
            {
                AccumulateRgba(rgba.Slice(src), 0, tmp, width);
            }

            ConvertRowsToUv(tmp, picture.U.AsSpan(dstUv, uvWidth), picture.V.AsSpan(dstUv, uvWidth), uvWidth);
        }

        return picture;
    }

    // Whether any pixel's alpha isn't 255 (CheckNonOpaque)
    private static bool HasTransparency(
        ReadOnlySpan<byte> rgba)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(rgba);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var opaque = Vector128.Create(0xff000000u);
            ref uint p = ref MemoryMarshal.GetReference(pixels);
            for (; i + 4 <= pixels.Length; i += 4)
            {
                if ((Vector128.LoadUnsafe(ref p, (nuint)i) & opaque) != opaque)
                {
                    return true;
                }
            }
        }

        for (; i < pixels.Length; i++)
        {
            if ((pixels[i] >> 24) != 0xff)
            {
                return true;
            }
        }

        return false;
    }

    // ExtractAlpha_C: the rows' alpha bytes; true if they're all 255
    private static bool ExtractAlpha(
        ReadOnlySpan<byte> alphaSource,
        int sourceStride,
        int width,
        int height,
        Span<byte> alpha,
        int alphaStride)
    {
        int mask = 0xff;
        ref byte src = ref MemoryMarshal.GetReference(alphaSource);
        ref byte dst = ref MemoryMarshal.GetReference(alpha);
        for (int j = 0; j < height; j++)
        {
            ref byte s = ref Unsafe.Add(ref src, j * sourceStride);
            ref byte d = ref Unsafe.Add(ref dst, j * alphaStride);
            for (int i = 0; i < width; i++)
            {
                byte value = Unsafe.Add(ref s, 4 * i);
                Unsafe.Add(ref d, i) = value;
                mask &= value;
            }
        }

        return mask == 0xff;
    }

    public static int RgbToY(
        int r,
        int g,
        int b,
        int rounding)
    {
        int luma = (16839 * r) + (33059 * g) + (6420 * b);
        return (luma + rounding + (16 << YuvFix)) >> YuvFix;
    }

    private static int ClipUv(
        int uv,
        int rounding)
    {
        uv = (uv + rounding + (128 << (YuvFix + 2))) >> (YuvFix + 2);
        return ((uv & ~0xff) == 0) ? uv : (uv < 0) ? 0 : 255;
    }

    public static int RgbToU(
        int r,
        int g,
        int b,
        int rounding)
    {
        return ClipUv((-9719 * r) - (19081 * g) + (28800 * b), rounding);
    }

    public static int RgbToV(
        int r,
        int g,
        int b,
        int rounding)
    {
        return ClipUv((28800 * r) - (24116 * g) - (4684 * b), rounding);
    }

    // ConvertRGBToY: four pixels a vector (each channel in its 32-bit lane)
    private static void ConvertRowToY(
        ReadOnlySpan<byte> rgba,
        Span<byte> y,
        int width)
    {
        ref byte src = ref MemoryMarshal.GetReference(rgba);
        ref byte dst = ref MemoryMarshal.GetReference(y);
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref uint pixels = ref Unsafe.As<byte, uint>(ref src);
            for (; i + 16 <= width; i += 16)
            {
                var y0 = LumaOf4(Vector128.LoadUnsafe(ref pixels, (nuint)i));
                var y1 = LumaOf4(Vector128.LoadUnsafe(ref pixels, (nuint)(i + 4)));
                var y2 = LumaOf4(Vector128.LoadUnsafe(ref pixels, (nuint)(i + 8)));
                var y3 = LumaOf4(Vector128.LoadUnsafe(ref pixels, (nuint)(i + 12)));
                Vector128.Narrow(Vector128.Narrow(y0, y1), Vector128.Narrow(y2, y3)).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        for (; i < width; i++)
        {
            ref byte p = ref Unsafe.Add(ref src, 4 * i);
            Unsafe.Add(ref dst, i) = (byte)RgbToY(p, Unsafe.Add(ref p, 1), Unsafe.Add(ref p, 2), YuvHalf);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> LumaOf4(
        Vector128<uint> pixels)
    {
        var mask = Vector128.Create(0xffu);
        var r = pixels & mask;
        var g = (pixels >>> 8) & mask;
        var b = (pixels >>> 16) & mask;
        var luma = (r * 16839u) + (g * 33059u) + (b * 6420u) + Vector128.Create((uint)(YuvHalf + (16 << YuvFix)));
        return luma >>> YuvFix;
    }

    private static void ConvertRowsToUv(
        ReadOnlySpan<ushort> rgb,
        Span<byte> u,
        Span<byte> v,
        int width)
    {
        ref ushort src = ref MemoryMarshal.GetReference(rgb);
        ref byte du = ref MemoryMarshal.GetReference(u);
        ref byte dv = ref MemoryMarshal.GetReference(v);
        for (int i = 0; i < width; i++)
        {
            ref ushort p = ref Unsafe.Add(ref src, 4 * i);
            int r = p, g = Unsafe.Add(ref p, 1), b = Unsafe.Add(ref p, 2);
            Unsafe.Add(ref du, i) = (byte)RgbToU(r, g, b, YuvHalf << 2);
            Unsafe.Add(ref dv, i) = (byte)RgbToV(r, g, b, YuvHalf << 2);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GammaToLinear(
        int v)
    {
        return Unsafe.Add(ref GammaToLinearRef, v);
    }

    private static ref ushort GammaToLinearRef => ref MemoryMarshal.GetReference(Vp8Encoder.GammaToLinearSpan);

    // Interpolate and LinearToGamma
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LinearToGamma(
        uint baseValue,
        int shift)
    {
        int v = (int)(baseValue << shift);
        int tabPos = v >> (GammaTabFix + 2);
        int x = v & ((GammaTabScale << 2) - 1);
        ref int tab = ref MemoryMarshal.GetReference(Vp8Encoder.LinearToGammaSpan);
        int v0 = Unsafe.Add(ref tab, tabPos);
        int v1 = Unsafe.Add(ref tab, tabPos + 1);
        int y = (v1 * x) + (v0 * ((GammaTabScale << 2) - x));
        return (y + GammaTabRounder) >> GammaTabFix;
    }

    // SUM4 and SUM2 of one channel at p (stride: the next row's offset)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sum4(
        ref byte p,
        int step,
        int stride)
    {
        return LinearToGamma(GammaToLinear(p) + GammaToLinear(Unsafe.Add(ref p, step)) + GammaToLinear(Unsafe.Add(ref p, stride)) + GammaToLinear(Unsafe.Add(ref p, stride + step)), 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sum2(
        ref byte p,
        int stride)
    {
        return LinearToGamma(GammaToLinear(p) + GammaToLinear(Unsafe.Add(ref p, stride)), 1);
    }

    private static void AccumulateRgb(
        ReadOnlySpan<byte> rgba,
        int stride,
        Span<ushort> dst,
        int width)
    {
        ref byte src = ref MemoryMarshal.GetReference(rgba);
        ref ushort d = ref MemoryMarshal.GetReference(dst);
        int i;
        for (i = 0; i < (width >> 1); i++)
        {
            ref byte p = ref Unsafe.Add(ref src, 8 * i);
            ref ushort o = ref Unsafe.Add(ref d, 4 * i);
            o = (ushort)Sum4(ref p, 4, stride);
            Unsafe.Add(ref o, 1) = (ushort)Sum4(ref Unsafe.Add(ref p, 1), 4, stride);
            Unsafe.Add(ref o, 2) = (ushort)Sum4(ref Unsafe.Add(ref p, 2), 4, stride);
        }

        if ((width & 1) != 0)
        {
            ref byte p = ref Unsafe.Add(ref src, 8 * i);
            ref ushort o = ref Unsafe.Add(ref d, 4 * i);
            o = (ushort)Sum2(ref p, stride);
            Unsafe.Add(ref o, 1) = (ushort)Sum2(ref Unsafe.Add(ref p, 1), stride);
            Unsafe.Add(ref o, 2) = (ushort)Sum2(ref Unsafe.Add(ref p, 2), stride);
        }
    }

    // LinearToGammaWeighted: one channel at p (its pixel's alpha at a), weighted by alpha
    private static int LinearToGammaWeightedAt(
        ref byte p,
        ref byte a,
        uint totalA,
        int step,
        int stride)
    {
        uint sum = (a * GammaToLinear(p))
            + (Unsafe.Add(ref a, step) * GammaToLinear(Unsafe.Add(ref p, step)))
            + (Unsafe.Add(ref a, stride) * GammaToLinear(Unsafe.Add(ref p, stride)))
            + (Unsafe.Add(ref a, stride + step) * GammaToLinear(Unsafe.Add(ref p, stride + step)));
        return LinearToGamma((sum * Vp8Encoder.InvAlphaAt((int)totalA)) >> (AlphaFix - 2), 0);
    }

    private static void AccumulateRgba(
        ReadOnlySpan<byte> rgba,
        int stride,
        Span<ushort> dst,
        int width)
    {
        ref byte src = ref MemoryMarshal.GetReference(rgba);
        ref ushort d = ref MemoryMarshal.GetReference(dst);
        int i;
        for (i = 0; i < (width >> 1); i++)
        {
            ref byte p = ref Unsafe.Add(ref src, 8 * i);
            ref ushort o = ref Unsafe.Add(ref d, 4 * i);
            ref byte alpha = ref Unsafe.Add(ref p, 3);
            uint a = (uint)(alpha + Unsafe.Add(ref alpha, stride) + Unsafe.Add(ref alpha, 4) + Unsafe.Add(ref alpha, 4 + stride));
            int r, g, b;
            if ((a == 4 * 0xff) || (a == 0))
            {
                r = Sum4(ref p, 4, stride);
                g = Sum4(ref Unsafe.Add(ref p, 1), 4, stride);
                b = Sum4(ref Unsafe.Add(ref p, 2), 4, stride);
            }
            else
            {
                r = LinearToGammaWeightedAt(ref p, ref alpha, a, 4, stride);
                g = LinearToGammaWeightedAt(ref Unsafe.Add(ref p, 1), ref alpha, a, 4, stride);
                b = LinearToGammaWeightedAt(ref Unsafe.Add(ref p, 2), ref alpha, a, 4, stride);
            }

            o = (ushort)r;
            Unsafe.Add(ref o, 1) = (ushort)g;
            Unsafe.Add(ref o, 2) = (ushort)b;
            Unsafe.Add(ref o, 3) = (ushort)a;
        }

        if ((width & 1) != 0)
        {
            ref byte p = ref Unsafe.Add(ref src, 8 * i);
            ref ushort o = ref Unsafe.Add(ref d, 4 * i);
            ref byte alpha = ref Unsafe.Add(ref p, 3);
            uint a = 2u * (uint)(alpha + Unsafe.Add(ref alpha, stride));
            int r, g, b;
            if ((a == 4 * 0xff) || (a == 0))
            {
                r = Sum2(ref p, stride);
                g = Sum2(ref Unsafe.Add(ref p, 1), stride);
                b = Sum2(ref Unsafe.Add(ref p, 2), stride);
            }
            else
            {
                r = LinearToGammaWeightedAt(ref p, ref alpha, a, 0, stride);
                g = LinearToGammaWeightedAt(ref Unsafe.Add(ref p, 1), ref alpha, a, 0, stride);
                b = LinearToGammaWeightedAt(ref Unsafe.Add(ref p, 2), ref alpha, a, 0, stride);
            }

            o = (ushort)r;
            Unsafe.Add(ref o, 1) = (ushort)g;
            Unsafe.Add(ref o, 2) = (ushort)b;
            Unsafe.Add(ref o, 3) = (ushort)a;
        }
    }

    // WebPCleanupTransparentArea, on YUV: fully transparent 8x8 blocks flattened (to the first such
    // block's values in a run), and the transparent pixels of partly transparent ones set to the
    // average luma of the others
    public void CleanupTransparentArea()
    {
        const int Size = 8;
        const int Size2 = Size / 2;
        if (this.A is null)
        {
            return;
        }

        int width = this.Width;
        int height = this.Height;
        int yp = 0, up = 0, ap = 0;
        int v0 = 0, v1 = 0, v2 = 0;
        int x, y;
        for (y = 0; y + Size <= height; y += Size)
        {
            bool needReset = true;
            for (x = 0; x + Size <= width; x += Size)
            {
                if (this.SmoothenBlock(ap + x, yp + x, Size, Size))
                {
                    if (needReset)
                    {
                        v0 = this.Y[yp + x];
                        v1 = this.U[up + (x >> 1)];
                        v2 = this.V[up + (x >> 1)];
                        needReset = false;
                    }

                    Flatten(this.Y, yp + x, v0, this.YStride, Size);
                    Flatten(this.U, up + (x >> 1), v1, this.UvStride, Size2);
                    Flatten(this.V, up + (x >> 1), v2, this.UvStride, Size2);
                }
                else
                {
                    needReset = true;
                }
            }

            if (x < width)
            {
                this.SmoothenBlock(ap + x, yp + x, width - x, Size);
            }

            ap += Size * width;
            yp += Size * this.YStride;
            up += Size2 * this.UvStride;
        }

        if (y < height)
        {
            int subHeight = height - y;
            for (x = 0; x + Size <= width; x += Size)
            {
                this.SmoothenBlock(ap + x, yp + x, Size, subHeight);
            }

            if (x < width)
            {
                this.SmoothenBlock(ap + x, yp + x, width - x, subHeight);
            }
        }
    }

    private static void Flatten(
        byte[] plane,
        int at,
        int value,
        int stride,
        int size)
    {
        for (int y = 0; y < size; y++)
        {
            plane.AsSpan(at + (y * stride), size).Fill((byte)value);
        }
    }

    private bool SmoothenBlock(
        int a,
        int yAt,
        int width,
        int height)
    {
        int sum = 0, count = 0;
        var alpha = this.A!;
        ref byte alphaRef = ref MemoryMarshal.GetArrayDataReference(alpha);
        ref byte lumaRef = ref MemoryMarshal.GetArrayDataReference(this.Y);
        for (int y = 0; y < height; y++)
        {
            ref byte ar = ref Unsafe.Add(ref alphaRef, a + (y * this.Width));
            ref byte yr = ref Unsafe.Add(ref lumaRef, yAt + (y * this.YStride));
            int x = 0;
            if (width == 8)
            {
                // A row all opaque or all transparent at once
                ulong row = Unsafe.ReadUnaligned<ulong>(ref ar);
                if (row == ulong.MaxValue)
                {
                    ulong luma = Unsafe.ReadUnaligned<ulong>(ref yr);
                    luma = (luma & 0x00ff00ff00ff00ffUL) + ((luma >> 8) & 0x00ff00ff00ff00ffUL);
                    luma += luma >> 16;
                    luma += luma >> 32;
                    sum += (int)(luma & 0xffff);
                    count += 8;
                    continue;
                }

                if (row == 0)
                {
                    continue;
                }
            }

            for (; x < width; x++)
            {
                if (Unsafe.Add(ref ar, x) != 0)
                {
                    count++;
                    sum += Unsafe.Add(ref yr, x);
                }
            }
        }

        if ((count > 0) && (count < width * height))
        {
            byte average = (byte)(sum / count);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (alpha[a + (y * this.Width) + x] == 0)
                    {
                        this.Y[yAt + (y * this.YStride) + x] = average;
                    }
                }
            }
        }

        return count == 0;
    }
}
