// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DotGame.Imaging.Jpeg;
using DotGame.Imaging.Png;
using DotGame.Imaging.WebP;

namespace DotGame.Imaging.Fuzz;

/// <summary>
/// The decoders the fuzzer runs: one format's codec, or <see cref="ImageDecoder"/>, which tells the
/// format from the bytes.
/// </summary>
internal enum Format
{
    Png,
    Jpeg,
    WebP,
    Any,
}

/// <summary>
/// Decodes one input the way an app would, and checks what the decoders promise: they throw only
/// <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/> for bad bytes, never read or
/// write past the input or the pixels (the buffers end at a guard page), write every byte of the
/// pixels, and give the same pixels every time.
/// </summary>
internal sealed class Target
{
    // The most pixels an image can have to be decoded; 8192 x 8192, the decoders' limit, takes 256 MB
    // a buffer
    public const int MaxPixelsLimit = 2048 * 2048;

    private readonly Format format;
    private readonly GuardedBuffer input = new(4 << 20);
    private readonly GuardedBuffer first = new(MaxPixelsLimit * 4);
    private readonly GuardedBuffer second = new(MaxPixelsLimit * 4);

    private readonly int maxPixels;

    /// <param name="format">The decoder to run.</param>
    /// <param name="maxPixels">
    /// Images with more pixels get their size read but aren't decoded, which keeps each run short: a
    /// damaged image is decoded whole all the same, its missing data as gray, as libjpeg does.
    /// </param>
    public Target(
        Format format,
        int maxPixels)
    {
        this.format = format;
        this.maxPixels = Math.Min(maxPixels, MaxPixelsLimit);
    }

    public int MaxInput => this.input.Capacity;

    /// <summary>
    /// Decodes <paramref name="data"/>, and returns null if the decoders kept their promises, or what
    /// went wrong (a short signature that tells one bug from another, then the details).
    /// </summary>
    /// <param name="data"></param>
    /// <param name="hash">The decoded pixels' hash, or 0 if they didn't decode.</param>
    public Failure? Run(
        ReadOnlySpan<byte> data,
        out ulong hash)
    {
        hash = 0;
        Span<byte> bytes = this.input.End(data.Length);
        data.CopyTo(bytes);
        bool premultiply = (data.Length & 1) != 0;

        bool sized;
        int width;
        int height;
        int byteCount;
        try
        {
            sized = this.TryGetDecodedSize(bytes, out width, out height, out byteCount);
        }
        catch (Exception exception)
        {
            return Failure.FromException("TryGetDecodedSize", exception);
        }

        if (!sized)
        {
            // An image whose header doesn't read can't be decoded: Decode must say so, not decode it
            return this.CheckRejected(bytes, premultiply);
        }

        if ((width <= 0) || (height <= 0) || (byteCount != (long)width * height * 4))
        {
            return new Failure("size", $"TryGetDecodedSize gave {width} x {height}, {byteCount} bytes.");
        }

        if ((long)width * height > this.maxPixels)
        {
            return null;
        }

        Span<byte> a = this.first.End(byteCount);
        Span<byte> b = this.second.End(byteCount);
        a.Clear();
        b.Fill(0xFF);
        Exception? firstError = this.Decode(bytes, a, premultiply);
        Exception? secondError = this.Decode(bytes, b, premultiply);
        if (firstError != null)
        {
            if (!IsDocumented(firstError))
            {
                return Failure.FromException("Decode", firstError);
            }

            if ((secondError == null) || (secondError.GetType() != firstError.GetType()))
            {
                return new Failure("nondeterministic-error", $"The first decode threw {firstError.GetType().Name}, the second {secondError?.GetType().Name ?? "nothing"}.");
            }

            return null;
        }

        if (secondError != null)
        {
            return Failure.FromException("Decode (second)", secondError);
        }

        if (!a.SequenceEqual(b))
        {
            int at = a.CommonPrefixLength(b);
            return new Failure("pixels-differ", $"Two decodes of the {width} x {height} image differ, from byte {at} (pixel {at / 4}): a pixel the decoder didn't write, or one it read from memory it didn't set.");
        }

        hash = Hash(a);
        return null;
    }

    private bool TryGetDecodedSize(
        ReadOnlySpan<byte> data,
        out int width,
        out int height,
        out int byteCount)
    {
        return this.format switch
        {
            Format.Png => PngCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out width, out height, out byteCount),
            Format.Jpeg => JpegCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out width, out height, out byteCount),
            Format.WebP => WebPCodec.TryGetDecodedSize(data, PixelLayout.Rgba32, out width, out height, out byteCount),
            _ => ImageDecoder.TryGetDecodedSize(data, PixelLayout.Rgba32, out width, out height, out byteCount),
        };
    }

    private Exception? Decode(
        ReadOnlySpan<byte> data,
        Span<byte> pixels,
        bool premultiply)
    {
        try
        {
            switch (this.format)
            {
                case Format.Png:
                    PngCodec.Decode(data, pixels, PixelLayout.Rgba32, premultiply);
                    break;
                case Format.Jpeg:
                    JpegCodec.Decode(data, pixels, PixelLayout.Rgba32);
                    break;
                case Format.WebP:
                    WebPCodec.Decode(data, pixels, PixelLayout.Rgba32, premultiply);
                    break;
                default:
                    ImageDecoder.Decode(data, pixels, PixelLayout.Rgba32, premultiply);
                    break;
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private Failure? CheckRejected(
        ReadOnlySpan<byte> data,
        bool premultiply)
    {
        // With no size there's no right buffer; an empty one must be refused, as damaged bytes or as
        // the wrong size, and nothing written
        Exception? error = this.Decode(data, this.first.End(0), premultiply);
        if (error == null)
        {
            return new Failure("decoded-unsized", "Decode accepted an image whose size TryGetDecodedSize couldn't read.");
        }

        if (!IsDocumented(error) && (error is not ArgumentException))
        {
            return Failure.FromException("Decode (unsized)", error);
        }

        return null;
    }

    /// <summary>
    /// A 64-bit FNV-1a hash of the bytes, eight at a time.
    /// </summary>
    public static ulong Hash(
        ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037UL;
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (ulong word in words)
        {
            hash = (hash ^ word) * 1099511628211UL;
        }

        for (int i = words.Length * 8; i < data.Length; i++)
        {
            hash = (hash ^ data[i]) * 1099511628211UL;
        }

        return hash;
    }

    private static bool IsDocumented(
        Exception exception)
    {
        return (exception.GetType() == typeof(InvalidDataException)) || (exception.GetType() == typeof(NotSupportedException));
    }
}

/// <summary>
/// A broken promise: its signature (the same for the same bug) and what happened.
/// </summary>
internal sealed record Failure(
    string Signature,
    string Details)
{
    public static Failure FromException(
        string call,
        Exception exception)
    {
        // The signature is the exception's type and where in the decoders it was thrown
        string where = "?";
        var trace = new StackTrace(exception, false);
        for (int i = 0; i < trace.FrameCount; i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            if ((method?.DeclaringType?.Namespace != null) && method.DeclaringType.Namespace.StartsWith("DotGame.Imaging", StringComparison.Ordinal)
                && !method.DeclaringType.Namespace.StartsWith("DotGame.Imaging.Fuzz", StringComparison.Ordinal))
            {
                where = $"{method.DeclaringType.Name}.{method.Name}";
                break;
            }
        }

        return new Failure($"{exception.GetType().Name}@{where}", $"{call} threw {exception}");
    }
}
