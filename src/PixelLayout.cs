// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Imaging;

/// <summary>
/// How the pixels in a buffer are laid out: the order of their channels and the bytes each takes.
/// </summary>
public enum PixelLayout
{
    /// <summary>
    /// Four bytes a pixel, red, green, blue and alpha in that order, 8 bits each; rows from the top,
    /// pixels from the left, with nothing between rows.
    /// </summary>
    Rgba32,
}
