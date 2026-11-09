// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;

namespace DotGame.Imaging.Tests;

/// <summary>
/// The test images in Images/, copied beside the tests when they build.
/// </summary>
internal static class TestImages
{
    public static byte[] Read(
        string path)
    {
        return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", path));
    }
}
