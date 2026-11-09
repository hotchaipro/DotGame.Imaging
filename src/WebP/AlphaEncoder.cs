// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/alpha_enc.c, src/utils/filters_utils.c, src/utils/
// quant_levels_utils.c and src/dsp/filters.c's forward filters), modified by Hot Chai Games LLC: written
// in C#. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;

namespace DotGame.Imaging.WebP;

/// <summary>The lossy encoder's alpha plane, as an ALPH chunk's payload.</summary>
internal static class AlphaEncoder
{
    private const int FilterNone = 0;
    private const int FilterHorizontal = 1;
    private const int FilterVertical = 2;
    private const int FilterGradient = 3;
    private const int FilterLast = 4;
    private const int FilterTryNone = 1 << FilterNone;
    private const int FilterTryAll = (1 << FilterLast) - 1;

    /// <summary>
    /// Encodes the alpha plane: quality 0 to 100 (100 keeps every level), method 0 (raw) or 1
    /// (lossless), filtering 0 (none), 1 (fast) or 2 (best), effort the lossy method (0 to 6); with
    /// threads, the lossless encoder's trials in parallel.
    /// </summary>
    public static byte[] Encode(
        byte[] alpha,
        int width,
        int height,
        int quality,
        int method,
        int filtering,
        int effort,
        bool useThreads)
    {
        bool reduceLevels = quality < 100;
        int dataSize = width * height;
        var quantAlpha = alpha.AsSpan(0, dataSize).ToArray();
        if (reduceLevels)
        {
            int alphaLevels = (quality <= 70) ? (2 + (quality / 5)) : (16 + ((quality - 70) * 8));
            QuantizeLevels(quantAlpha, alphaLevels);
        }

        // CompressAlphaJob's mapping: 0 none, 1 fast, 2 (or more) best; raw is never filtered
        int tryMap;
        if ((method == 0) || (filtering == 0))
        {
            tryMap = FilterTryNone;
        }
        else if (filtering == 1)
        {
            // GetFilterMap's WEBP_FILTER_FAST
            int numColors = GetNumColors(quantAlpha);
            int filter = (numColors <= 16) ? FilterNone : EstimateBestFilter(quantAlpha, width, height);
            tryMap = 1 << filter;
            if ((effort > 3) || (numColors > 192))
            {
                tryMap |= FilterTryNone;
            }
        }
        else
        {
            tryMap = FilterTryAll;
        }

        byte[]? best = null;
        if (tryMap != FilterTryNone)
        {
            var filtered = new byte[dataSize];
            for (int filter = FilterNone; tryMap != 0; filter++, tryMap >>= 1)
            {
                if ((tryMap & 1) != 0)
                {
                    var trial = EncodeInternal(quantAlpha, width, height, method, filter, reduceLevels, effort, filtered, useThreads);
                    if ((best is null) || (trial.Length < best.Length))
                    {
                        best = trial;
                    }
                }
            }
        }
        else
        {
            best = EncodeInternal(quantAlpha, width, height, method, FilterNone, reduceLevels, effort, null, useThreads);
        }

        return best!;
    }

    // EncodeAlphaInternal: the header byte, then the (filtered) levels, raw or lossless
    private static byte[] EncodeInternal(
        byte[] data,
        int width,
        int height,
        int method,
        int filter,
        bool reduceLevels,
        int effort,
        byte[]? tmpAlpha,
        bool useThreads)
    {
        int dataSize = width * height;
        byte[] alphaSrc = data;
        if (filter != FilterNone)
        {
            Filter(filter, data, width, height, tmpAlpha!);
            alphaSrc = tmpAlpha!;
        }

        byte[]? output = null;
        if (method != 0)
        {
            output = EncodeLossless(alphaSrc, width, height, effort, !reduceLevels, useThreads);
            if (output.Length > dataSize)
            {
                method = 0;
                output = null;
            }
        }

        int header = method | (filter << 2);
        if (reduceLevels)
        {
            header |= 1 << 4;
        }

        int size = (output is null) ? dataSize : output.Length;
        var result = new byte[1 + size];
        result[0] = (byte)header;
        if (output is null)
        {
            alphaSrc.AsSpan(0, dataSize).CopyTo(result.AsSpan(1));
        }
        else
        {
            output.CopyTo(result, 1);
        }

        return result;
    }

    // EncodeLossless: the levels in the green of an ARGB image, as a VP8L image stream (no header)
    private static byte[] EncodeLossless(
        byte[] data,
        int width,
        int height,
        int effort,
        bool useQuality100,
        bool useThreads)
    {
        var argb = new uint[width * height];
        for (int i = 0; i < argb.Length; i++)
        {
            argb[i] = (uint)data[i] << 8;
        }

        float quality = (useQuality100 && (effort == 6)) ? 100 : 8f * effort;
        return LosslessStream(argb, width, height, effort, quality, useThreads);
    }

    // The VP8L encoder's image stream (VP8LEncodeStream with lossless, exact, method = effort and
    // this quality)
    private static byte[] LosslessStream(
        uint[] argb,
        int width,
        int height,
        int method,
        float quality,
        bool useThreads)
    {
        return Vp8LEncoder.EncodeImageStream(width, height, argb, new Vp8LEncoderOptions { Quality = quality, Method = method, Exact = true, UseThreads = useThreads });
    }

    private static int GetNumColors(
        byte[] data)
    {
        Span<bool> color = stackalloc bool[256];
        color.Clear();
        foreach (byte b in data)
        {
            color[b] = true;
        }

        int colors = 0;
        for (int j = 0; j < 256; j++)
        {
            if (color[j])
            {
                colors++;
            }
        }

        return colors;
    }

    private static int GradientPredictor(
        int a,
        int b,
        int c)
    {
        int g = a + b - c;
        return ((g & ~0xff) == 0) ? g : (g < 0) ? 0 : 255;
    }

    // WebPEstimateBestFilter
    private static int EstimateBestFilter(
        byte[] data,
        int width,
        int height)
    {
        const int smax = 16;
        Span<bool> bins = stackalloc bool[FilterLast * smax];
        bins.Clear();
        for (int j = 2; j < height - 1; j += 2)
        {
            int p = j * width;
            int mean = data[p];
            for (int i = 2; i < width - 1; i += 2)
            {
                int v = data[p + i];
                int diff0 = Math.Abs(v - mean) >> 4;
                int diff1 = Math.Abs(v - data[p + i - 1]) >> 4;
                int diff2 = Math.Abs(v - data[p + i - width]) >> 4;
                int gradPred = GradientPredictor(data[p + i - 1], data[p + i - width], data[p + i - width - 1]);
                int diff3 = Math.Abs(v - gradPred) >> 4;
                bins[(FilterNone * smax) + diff0] = true;
                bins[(FilterHorizontal * smax) + diff1] = true;
                bins[(FilterVertical * smax) + diff2] = true;
                bins[(FilterGradient * smax) + diff3] = true;
                mean = ((3 * mean) + v + 2) >> 2;
            }
        }

        int bestFilter = FilterNone;
        int bestScore = int.MaxValue;
        for (int filter = FilterNone; filter < FilterLast; filter++)
        {
            int score = 0;
            for (int i = 0; i < smax; i++)
            {
                if (bins[(filter * smax) + i])
                {
                    score += i;
                }
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestFilter = filter;
            }
        }

        return bestFilter;
    }

    // The forward filters (dsp/filters.c): each sample minus its prediction
    private static void Filter(
        int filter,
        byte[] input,
        int width,
        int height,
        byte[] output)
    {
        output[0] = input[0];
        for (int i = 1; i < width; i++)
        {
            output[i] = (byte)(input[i] - input[i - 1]);
        }

        for (int row = 1; row < height; row++)
        {
            int at = row * width;
            int prev = at - width;
            switch (filter)
            {
                case FilterHorizontal:
                    output[at] = (byte)(input[at] - input[prev]);
                    for (int i = 1; i < width; i++)
                    {
                        output[at + i] = (byte)(input[at + i] - input[at + i - 1]);
                    }

                    break;
                case FilterVertical:
                    for (int i = 0; i < width; i++)
                    {
                        output[at + i] = (byte)(input[at + i] - input[prev + i]);
                    }

                    break;
                default:
                    output[at] = (byte)(input[at] - input[prev]);
                    for (int i = 1; i < width; i++)
                    {
                        int pred = GradientPredictor(input[at + i - 1], input[prev + i], input[prev + i - 1]);
                        output[at + i] = (byte)(input[at + i] - pred);
                    }

                    break;
            }
        }
    }

    // QuantizeLevels: the levels reduced to numLevels by a few k-means iterations
    private static void QuantizeLevels(
        byte[] data,
        int numLevels)
    {
        const int numSymbols = 256;
        const int maxIter = 6;
        Span<int> freq = stackalloc int[numSymbols];
        Span<int> qLevel = stackalloc int[numSymbols];
        Span<double> invQLevel = stackalloc double[numSymbols];
        Span<double> qSum = stackalloc double[numSymbols];
        Span<double> qCount = stackalloc double[numSymbols];
        freq.Clear();
        qLevel.Clear();
        invQLevel.Clear();
        int minS = 255, maxS = 0;
        double lastErr = 1e38;
        double errThreshold = 1e-4 * data.Length;
        int numLevelsIn = 0;
        foreach (byte v in data)
        {
            if (freq[v] == 0)
            {
                numLevelsIn++;
            }

            if (minS > v)
            {
                minS = v;
            }

            if (maxS < v)
            {
                maxS = v;
            }

            freq[v]++;
        }

        if (numLevelsIn <= numLevels)
        {
            return;
        }

        for (int i = 0; i < numLevels; i++)
        {
            invQLevel[i] = minS + ((double)(maxS - minS) * i / (numLevels - 1));
        }

        qLevel[minS] = 0;
        qLevel[maxS] = numLevels - 1;
        for (int iter = 0; iter < maxIter; iter++)
        {
            qSum.Clear();
            qCount.Clear();
            int slot = 0;
            for (int s = minS; s <= maxS; s++)
            {
                while ((slot < numLevels - 1) && (2 * s > invQLevel[slot] + invQLevel[slot + 1]))
                {
                    slot++;
                }

                if (freq[s] > 0)
                {
                    qSum[slot] += s * freq[s];
                    qCount[slot] += freq[s];
                }

                qLevel[s] = slot;
            }

            if (numLevels > 2)
            {
                for (slot = 1; slot < numLevels - 1; slot++)
                {
                    double count = qCount[slot];
                    if (count > 0)
                    {
                        invQLevel[slot] = qSum[slot] / count;
                    }
                }
            }

            double err = 0;
            for (int s = minS; s <= maxS; s++)
            {
                double error = s - invQLevel[qLevel[s]];
                err += freq[s] * error * error;
            }

            if (lastErr - err < errThreshold)
            {
                break;
            }

            lastErr = err;
        }

        Span<byte> map = stackalloc byte[numSymbols];
        for (int s = minS; s <= maxS; s++)
        {
            map[s] = (byte)(invQLevel[qLevel[s]] + 0.5);
        }

        for (int n = 0; n < data.Length; n++)
        {
            data[n] = map[data[n]];
        }
    }
}
