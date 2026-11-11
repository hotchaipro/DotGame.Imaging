// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Imaging.WebP;

namespace DotGame.Imaging.Tests;

/// <summary>
/// The lossless decoder's predictors, four pixels at a time (vectors, on Arm64 for the predictors
/// that read the pixel to the left) against a pixel at a time. Images rarely use every predictor
/// over runs long enough for the vector paths, so these cover each one directly.
/// </summary>
public sealed class WebPPredictorTests
{
    public static readonly TheoryData<int> Predictors = new() { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

    [Theory]
    [MemberData(nameof(Predictors))]
    public void PredictorAdd_GivesTheSamePixelsFourAtATime(
        int predictor)
    {
        const int count = 37;

        // The row above with a pixel either side (above left of the first, above right of the
        // last), then the residuals with the pixel left of the first after them, then the output
        var random = new Random(predictor);
        int upper = 1;
        int input = upper + count + 2;
        int output = input + count + 1;
        var pixels = new uint[output + count];
        for (int k = 0; k < output; k++)
        {
            pixels[k] = (uint)random.Next() ^ ((uint)random.Next() << 16);
        }

        var expected = (uint[])pixels.Clone();
        Vp8LDecoder.PredictorAdd(predictor, pixels, input, upper, count, output);
        for (int k = 0; k < count; k++)
        {
            Vp8LDecoder.PredictorAdd(predictor, expected, input + k, upper + k, 1, output + k);
        }

        Assert.Equal(expected, pixels);
    }
}
