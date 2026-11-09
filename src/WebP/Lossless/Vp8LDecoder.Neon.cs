// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dsp/lossless_neon.c, the predictors that read the pixel to the
// left), modified by Hot Chai Games LLC: written in C# with System.Runtime.Intrinsics, each pixel's
// result merged into the vector stored rather than stored a lane at a time. libwebp carries this
// notice:
//   Copyright 2016 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LDecoder
{
    // The predictors that read the pixel to the left, four pixels at a time with Arm64's vectors, as
    // libwebp's NEON code does: the left pixel is kept in the lane of the pixel being made (each
    // result rotated up a lane for the next), so nothing leaves the vector registers. Returns how
    // many pixels it made (a multiple of 4); the caller makes the rest.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PredictorAddNeon(
        int predictor,
        ref uint inp,
        ref uint up,
        ref uint outp,
        int count,
        uint left)
    {
        int i = 0;
        var mask1 = Vector128.Create(0u, uint.MaxValue, 0u, 0u).AsByte();
        var mask2 = Vector128.Create(0u, 0u, uint.MaxValue, 0u).AsByte();
        var mask3 = Vector128.Create(0u, 0u, 0u, uint.MaxValue).AsByte();
        var l = Vector128.Create(left).AsByte();
        switch (predictor)
        {
            case 5:
                // The average of the average of left and top right, and top
                for (; i + 4 <= count; i += 4)
                {
                    var src = Load(ref inp, i);
                    var t = Load(ref up, i);
                    var tr = Load(ref up, i + 1);
                    var r0 = AdvSimd.FusedAddHalving(AdvSimd.FusedAddHalving(l, tr), t) + src;
                    l = Rotate(r0);
                    var r1 = AdvSimd.FusedAddHalving(AdvSimd.FusedAddHalving(l, tr), t) + src;
                    l = Rotate(r1);
                    var r2 = AdvSimd.FusedAddHalving(AdvSimd.FusedAddHalving(l, tr), t) + src;
                    l = Rotate(r2);
                    var r3 = AdvSimd.FusedAddHalving(AdvSimd.FusedAddHalving(l, tr), t) + src;
                    l = Rotate(r3);
                    Store(ref outp, i, Merge(r0, r1, r2, r3, mask1, mask2, mask3));
                }

                break;

            case 6:
            case 7:
                {
                    // The average of left and top left, or of left and top
                    ref uint other = ref Unsafe.Add(ref up, (predictor == 6) ? -1 : 0);
                    for (; i + 4 <= count; i += 4)
                    {
                        var src = Load(ref inp, i);
                        var top = Load(ref other, i);
                        var r0 = AdvSimd.FusedAddHalving(l, top) + src;
                        l = Rotate(r0);
                        var r1 = AdvSimd.FusedAddHalving(l, top) + src;
                        l = Rotate(r1);
                        var r2 = AdvSimd.FusedAddHalving(l, top) + src;
                        l = Rotate(r2);
                        var r3 = AdvSimd.FusedAddHalving(l, top) + src;
                        l = Rotate(r3);
                        Store(ref outp, i, Merge(r0, r1, r2, r3, mask1, mask2, mask3));
                    }

                    break;
                }

            case 10:
                // The average of the averages of left and top left, and of top and top right
                for (; i + 4 <= count; i += 4)
                {
                    var src = Load(ref inp, i);
                    var tl = Load(ref up, i - 1);
                    var avgTTR = AdvSimd.FusedAddHalving(Load(ref up, i), Load(ref up, i + 1));
                    var r0 = AdvSimd.FusedAddHalving(avgTTR, AdvSimd.FusedAddHalving(l, tl)) + src;
                    l = Rotate(r0);
                    var r1 = AdvSimd.FusedAddHalving(avgTTR, AdvSimd.FusedAddHalving(l, tl)) + src;
                    l = Rotate(r1);
                    var r2 = AdvSimd.FusedAddHalving(avgTTR, AdvSimd.FusedAddHalving(l, tl)) + src;
                    l = Rotate(r2);
                    var r3 = AdvSimd.FusedAddHalving(avgTTR, AdvSimd.FusedAddHalving(l, tl)) + src;
                    l = Rotate(r3);
                    Store(ref outp, i, Merge(r0, r1, r2, r3, mask1, mask2, mask3));
                }

                break;

            case 11:
                // Select: top or left, whichever's channels are nearer top left's in all
                for (; i + 4 <= count; i += 4)
                {
                    var t = Load(ref up, i);
                    var tl = Load(ref up, i - 1);
                    var pb = SumAbsoluteDifferences(t, tl);
                    var src = Load(ref inp, i);
                    var sumTin = t + src;
                    var r0 = Select11(l, tl, pb, src, sumTin);
                    l = Rotate(r0);
                    var r1 = Select11(l, tl, pb, src, sumTin);
                    l = Rotate(r1);
                    var r2 = Select11(l, tl, pb, src, sumTin);
                    l = Rotate(r2);
                    var r3 = Select11(l, tl, pb, src, sumTin);
                    l = Rotate(r3);
                    Store(ref outp, i, Merge(r0, r1, r2, r3, mask1, mask2, mask3));
                }

                break;

            case 12:
                {
                    // Left + top - top left, each channel clamped: in 16-bit lanes, two pixels a half
                    var l16 = AdvSimd.ZeroExtendWideningLower(Vector64.Create(left).AsByte());
                    for (; i + 4 <= count; i += 4)
                    {
                        var src = Load(ref inp, i);
                        var tl = Load(ref up, i - 1);
                        var t = Load(ref up, i);
                        var diffLow = AdvSimd.SubtractWideningLower(t.GetLower(), tl.GetLower()).AsInt16();
                        var diffHigh = AdvSimd.SubtractWideningUpper(t, tl).AsInt16();
                        var r0 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(l16.AsInt16() + diffLow) + src.GetLower();
                        l16 = Rotate16(AdvSimd.ZeroExtendWideningLower(r0));
                        var r1 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(l16.AsInt16() + diffLow) + src.GetLower();
                        l16 = Rotate16(AdvSimd.ZeroExtendWideningLower(r1));
                        var r2 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(l16.AsInt16() + diffHigh) + src.GetUpper();
                        l16 = Rotate16(AdvSimd.ZeroExtendWideningLower(r2));
                        var r3 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(l16.AsInt16() + diffHigh) + src.GetUpper();
                        l16 = Rotate16(AdvSimd.ZeroExtendWideningLower(r3));
                        Store(ref outp, i, MergeHalves(r0, r1, r2, r3));
                    }

                    return i;
                }

            case 13:
                // The average of left and top, plus half its difference from top left, clamped
                for (; i + 4 <= count; i += 4)
                {
                    var src = Load(ref inp, i);
                    var t = Load(ref up, i);
                    var tl = Load(ref up, i - 1);
                    var r0 = Predict13(l, t, tl, src, high: false);
                    l = Rotate(Vector128.Create(r0, r0));
                    var r1 = Predict13(l, t, tl, src, high: false);
                    l = Rotate(Vector128.Create(r1, r1));
                    var r2 = Predict13(l, t, tl, src, high: true);
                    l = Rotate(Vector128.Create(r2, r2));
                    var r3 = Predict13(l, t, tl, src, high: true);
                    l = Rotate(Vector128.Create(r3, r3));
                    Store(ref outp, i, MergeHalves(r0, r1, r2, r3));
                }

                break;
        }

        return i;
    }

    // The pixels moved up a lane, the last to the first (libwebp's ROTATE32_LEFT)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Rotate(
        Vector128<byte> pixels)
    {
        return AdvSimd.ExtractVector128(pixels, pixels, 12);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Rotate16(
        Vector128<ushort> pixels)
    {
        return AdvSimd.ExtractVector128(pixels, pixels, 4);
    }

    // Lane 0 of r0, 1 of r1, 2 of r2 and 3 of r3: the four pixels made
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Merge(
        Vector128<byte> r0,
        Vector128<byte> r1,
        Vector128<byte> r2,
        Vector128<byte> r3,
        Vector128<byte> mask1,
        Vector128<byte> mask2,
        Vector128<byte> mask3)
    {
        return AdvSimd.BitwiseSelect(mask3, r3, AdvSimd.BitwiseSelect(mask2, r2, AdvSimd.BitwiseSelect(mask1, r1, r0)));
    }

    // The same from half vectors: pixel 0 in lane 0 of r0, 1 in lane 1 of r1, 2 in lane 0 of r2, 3 in
    // lane 1 of r3
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> MergeHalves(
        Vector64<byte> r0,
        Vector64<byte> r1,
        Vector64<byte> r2,
        Vector64<byte> r3)
    {
        var high = Vector64.Create(0u, uint.MaxValue).AsByte();
        return Vector128.Create(AdvSimd.BitwiseSelect(high, r1, r0), AdvSimd.BitwiseSelect(high, r3, r2));
    }

    // Each pixel's sum of its channels' absolute differences
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> SumAbsoluteDifferences(
        Vector128<byte> a,
        Vector128<byte> b)
    {
        return AdvSimd.AddPairwiseWidening(AdvSimd.AddPairwiseWidening(AdvSimd.AbsoluteDifference(a, b)));
    }

    // Predictor 11 in each lane: top (sumTin, in + top) where left is no nearer top left than top is
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Select11(
        Vector128<byte> l,
        Vector128<byte> tl,
        Vector128<uint> pb,
        Vector128<byte> src,
        Vector128<byte> sumTin)
    {
        var sumLin = l + src;
        var pa = SumAbsoluteDifferences(l, tl);
        var mask = AdvSimd.CompareLessThanOrEqual(pa, pb).AsByte();
        return AdvSimd.BitwiseSelect(mask, sumTin, sumLin);
    }

    // Predictor 13 for the half (two pixels) holding the lane being made: the average of left and
    // top, plus half its difference from top left (rounded toward zero, by the top left adjusted a
    // step where it's above the average), clamped
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector64<byte> Predict13(
        Vector128<byte> l,
        Vector128<byte> t,
        Vector128<byte> tl,
        Vector128<byte> src,
        bool high)
    {
        var avg = AdvSimd.FusedAddHalving(l, t);
        var cmp = AdvSimd.CompareGreaterThan(tl, avg);
        var tl1 = tl + cmp;
        var half = AdvSimd.FusedSubtractHalving(avg, tl1);
        var diff = (high ? half.GetUpper() : half.GetLower()).AsSByte();
        var avg16 = AdvSimd.ZeroExtendWideningLower(high ? avg.GetUpper() : avg.GetLower()).AsInt16();
        var delta = AdvSimd.ExtractNarrowingSaturateUnsignedLower(AdvSimd.AddWideningLower(avg16, diff));
        return (high ? src.GetUpper() : src.GetLower()) + delta;
    }
}
