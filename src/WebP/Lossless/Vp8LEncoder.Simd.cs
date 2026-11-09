// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Vector forms of libwebp 1.6.0's lossless encoder's predictors (src/dsp/lossless_enc_sse2.c's
// PredictorSub functions do the same with SSE2), written by Hot Chai Games LLC in C#. libwebp carries this
// notice:
//   Copyright 2015 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8LEncoder
{
    // Average2 of four pixels at once: each byte's floor average
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Average2(
        Vector128<uint> a,
        Vector128<uint> b)
    {
        return (((a ^ b) & Vector128.Create(0xfefefefeu)) >> 1) + (a & b);
    }

    // Each pixel's four channels summed, in its lane (at most 4 x 255)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> SumBytes(
        Vector128<uint> v)
    {
        var mask = Vector128.Create(0x00ff00ffu);
        var pairs = (v & mask) + ((v >> 8) & mask);
        return (pairs & Vector128.Create(0xffffu)) + (pairs >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> AbsoluteDifference(
        Vector128<uint> a,
        Vector128<uint> b)
    {
        var ab = a.AsByte();
        var bb = b.AsByte();
        return (Vector128.Max(ab, bb) - Vector128.Min(ab, bb)).AsUInt32();
    }

    // ClampedAddSubtractFull of four pixels: each byte a + b - c, clamped to [0, 255]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ClampedAddSubtractFull(
        Vector128<uint> a,
        Vector128<uint> b,
        Vector128<uint> c)
    {
        var (aLow, aHigh) = Vector128.Widen(a.AsByte());
        var (bLow, bHigh) = Vector128.Widen(b.AsByte());
        var (cLow, cHigh) = Vector128.Widen(c.AsByte());
        var zero = Vector128<short>.Zero;
        var max = Vector128.Create((short)255);
        var low = Vector128.Min(Vector128.Max(aLow.AsInt16() + bLow.AsInt16() - cLow.AsInt16(), zero), max);
        var high = Vector128.Min(Vector128.Max(aHigh.AsInt16() + bHigh.AsInt16() - cHigh.AsInt16(), zero), max);
        return Vector128.Narrow(low.AsUInt16(), high.AsUInt16()).AsUInt32();
    }

    // ClampedAddSubtractHalf of four pixels: each byte of ave = Average2(a, b), then ave + (ave - c) / 2
    // (the division rounding toward zero), clamped to [0, 255]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ClampedAddSubtractHalf(
        Vector128<uint> a,
        Vector128<uint> b,
        Vector128<uint> c)
    {
        var ave = Average2(a, b);
        var (aveLow, aveHigh) = Vector128.Widen(ave.AsByte());
        var (cLow, cHigh) = Vector128.Widen(c.AsByte());
        var zero = Vector128<short>.Zero;
        var max = Vector128.Create((short)255);
        var dLow = aveLow.AsInt16() - cLow.AsInt16();
        var dHigh = aveHigh.AsInt16() - cHigh.AsInt16();

        // d / 2 toward zero: (d + (d < 0)) >> 1
        var halfLow = (dLow - (dLow >> 15)) >> 1;
        var halfHigh = (dHigh - (dHigh >> 15)) >> 1;
        var low = Vector128.Min(Vector128.Max(aveLow.AsInt16() + halfLow, zero), max);
        var high = Vector128.Min(Vector128.Max(aveHigh.AsInt16() + halfHigh, zero), max);
        return Vector128.Narrow(low.AsUInt16(), high.AsUInt16()).AsUInt32();
    }

    // Select of four pixels: top unless the left is nearer the gradient's prediction
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Select(
        Vector128<uint> top,
        Vector128<uint> left,
        Vector128<uint> topLeft)
    {
        // Σ|left - topLeft| - Σ|top - topLeft| <= 0 chooses top
        var leftDistance = SumBytes(AbsoluteDifference(left, topLeft));
        var topDistance = SumBytes(AbsoluteDifference(top, topLeft));
        var chooseTop = Vector128.LessThanOrEqual(leftDistance.AsInt32(), topDistance.AsInt32()).AsUInt32();
        return Vector128.ConditionalSelect(chooseTop, top, left);
    }

    // A predictor of four pixels at once, from the pixel to the left (l) and the row above at x - 1
    // (tl), x (t) and x + 1 (tr)
    private interface IPredictor
    {
        static abstract Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr);
    }

    private struct Predictor0 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Vector128.Create(ArgbBlack);
    }

    private struct Predictor1 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => l;
    }

    private struct Predictor2 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => t;
    }

    private struct Predictor3 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => tr;
    }

    private struct Predictor4 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => tl;
    }

    private struct Predictor5 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Average2(Average2(l, tr), t);
    }

    private struct Predictor6 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Average2(l, tl);
    }

    private struct Predictor7 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Average2(l, t);
    }

    private struct Predictor8 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Average2(tl, t);
    }

    private struct Predictor9 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Average2(t, tr);
    }

    private struct Predictor10 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Average2(Average2(l, tl), Average2(t, tr));
    }

    private struct Predictor11 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => Select(t, l, tl);
    }

    private struct Predictor12 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => ClampedAddSubtractFull(l, t, tl);
    }

    private struct Predictor13 : IPredictor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<uint> Predict(
            Vector128<uint> l,
            Vector128<uint> tl,
            Vector128<uint> t,
            Vector128<uint> tr) => ClampedAddSubtractHalf(l, t, tl);
    }

    // The residuals of current[start..start+count) in vectors of four: every lane reads current at
    // x - 1 and the row above from x - 1 to x + 1, which the callers' rows hold (they're a pixel wider
    // than the image, and start is at least 1). Returns how many it did.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int PredictorSubVector<T>(
        ReadOnlySpan<uint> current,
        ReadOnlySpan<uint> upper,
        int start,
        int count,
        Span<uint> output)
        where T : struct, IPredictor
    {
        ref uint c = ref MemoryMarshal.GetReference(current);
        ref uint u = ref MemoryMarshal.GetReference(upper);
        ref uint o = ref MemoryMarshal.GetReference(output);
        int i = 0;
        for (; i + 4 <= count; i += 4)
        {
            nuint x = (nuint)(start + i);
            var l = Vector128.LoadUnsafe(ref c, x - 1);
            var pixel = Vector128.LoadUnsafe(ref c, x);
            var tl = Vector128.LoadUnsafe(ref u, x - 1);
            var t = Vector128.LoadUnsafe(ref u, x);
            var tr = Vector128.LoadUnsafe(ref u, x + 1);
            var predict = T.Predict(l, tl, t, tr);
            (pixel.AsByte() - predict.AsByte()).AsUInt32().StoreUnsafe(ref o, (nuint)i);
        }

        return i;
    }

    // VP8LPredictorsSub, with vectors where the runtime has them
    private static int PredictorSubVector(
        int mode,
        ReadOnlySpan<uint> current,
        ReadOnlySpan<uint> upper,
        int start,
        int count,
        Span<uint> output)
    {
        if (!Vector128.IsHardwareAccelerated || (count < 4) || (start < 1) || (upper.Length < start + count + 1) || (current.Length < start + count))
        {
            return 0;
        }

        return mode switch
        {
            0 => PredictorSubVector<Predictor0>(current, upper, start, count, output),
            1 => PredictorSubVector<Predictor1>(current, upper, start, count, output),
            2 => PredictorSubVector<Predictor2>(current, upper, start, count, output),
            3 => PredictorSubVector<Predictor3>(current, upper, start, count, output),
            4 => PredictorSubVector<Predictor4>(current, upper, start, count, output),
            5 => PredictorSubVector<Predictor5>(current, upper, start, count, output),
            6 => PredictorSubVector<Predictor6>(current, upper, start, count, output),
            7 => PredictorSubVector<Predictor7>(current, upper, start, count, output),
            8 => PredictorSubVector<Predictor8>(current, upper, start, count, output),
            9 => PredictorSubVector<Predictor9>(current, upper, start, count, output),
            10 => PredictorSubVector<Predictor10>(current, upper, start, count, output),
            11 => PredictorSubVector<Predictor11>(current, upper, start, count, output),
            12 => PredictorSubVector<Predictor12>(current, upper, start, count, output),
            _ => PredictorSubVector<Predictor13>(current, upper, start, count, output),
        };
    }
}
