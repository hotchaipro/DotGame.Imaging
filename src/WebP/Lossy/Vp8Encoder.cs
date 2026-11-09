// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/webp_enc.c, config_enc.c, syntax_enc.c and vp8i_enc.h),
// modified by Hot Chai Games LLC: written in C#, single-threaded (cwebp's -mt gives the same bytes), from
// RGBA. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DotGame.Imaging.WebP;

/// <summary>
/// The lossy encoder's settings: the WebPConfig fields lossy encoding reads, with cwebp's defaults
/// (WebPConfigInit's, quality 75).
/// </summary>
internal sealed class Vp8EncoderOptions
{
    /// <summary>-q: 0 (smallest) to 100 (best).</summary>
    public float Quality { get; init; } = 75;

    /// <summary>-m: 0 (fastest) to 6 (slowest, smallest).</summary>
    public int Method { get; init; } = 4;

    /// <summary>-sns: spatial noise shaping, 0 to 100.</summary>
    public int SnsStrength { get; init; } = 50;

    /// <summary>-f: filter strength, 0 (off) to 100.</summary>
    public int FilterStrength { get; init; } = 60;

    /// <summary>-sharpness: 0 to 7.</summary>
    public int FilterSharpness { get; init; }

    /// <summary>-strong (1) or -nostrong (0, the simple filter).</summary>
    public int FilterType { get; init; } = 1;

    /// <summary>-segments: 1 to 4.</summary>
    public int Segments { get; init; } = 4;

    /// <summary>-pass: 1 to 10 (more only matter with a target size or PSNR).</summary>
    public int Pass { get; init; } = 1;

    /// <summary>-partition_limit: 0 to 100.</summary>
    public int PartitionLimit { get; init; }

    /// <summary>-partitions (log2 of the token partitions): 0 to 3.</summary>
    public int Partitions { get; init; }

    /// <summary>-pre: bit 0 smooths the segment map, bit 2 is sharp YUV. Dithering (bit 1) isn't supported.</summary>
    public int Preprocessing { get; init; }

    /// <summary>-exact: keep the RGB of transparent pixels.</summary>
    public bool Exact { get; init; }

    /// <summary>-sharp_yuv.</summary>
    public bool UseSharpYuv { get; init; }

    /// <summary>-alpha_method: 0 (raw) or 1 (lossless).</summary>
    public int AlphaCompression { get; init; } = 1;

    /// <summary>-alpha_filter: 0 (none), 1 (fast) or 2 (best).</summary>
    public int AlphaFiltering { get; init; } = 1;

    /// <summary>-alpha_q: 0 to 100.</summary>
    public int AlphaQuality { get; init; } = 100;

    /// <summary>-size: a target size in bytes, 0 for none.</summary>
    public int TargetSize { get; init; }

    /// <summary>-psnr: a target PSNR, 0 for none.</summary>
    public float TargetPsnr { get; init; }

    /// <summary>-qrange: the quality range a target searches.</summary>
    public int QMin { get; init; }

    /// <summary>-qrange's upper end.</summary>
    public int QMax { get; init; } = 100;

    /// <summary>-jpeg_like.</summary>
    public bool EmulateJpegSize { get; init; }

    /// <summary>
    /// -mt: the alpha encoded on another thread while the frame is (and the lossless alpha's trials in
    /// parallel). The bytes are the same either way.
    /// </summary>
    public bool UseThreads { get; init; }
}

/// <summary>
/// The lossy WebP encoder: RGBA to a VP8 key frame (and the alpha in an ALPH chunk), as cwebp writes
/// it, byte for byte.
/// </summary>
internal static partial class Vp8Encoder
{
    private const int NumMbSegments = 4;
    private const int NumTypes = 4;
    private const int NumBands = 8;
    private const int NumCtx = 3;
    private const int NumProbas = 11;
    private const int MaxVariableLevel = 67;
    private const int NumBModes = 10;
    private const int NumPredModes = 4;
    private const int MaxLfLevels = 64;
    private const int ErrorDiffusionQuality = 98;
    private const long MaxCost = 0x7fffffffffffffL;
    private const int MaxPartition0Size = 1 << 19;

    /// <summary>
    /// Encodes width x height RGBA pixels (straight alpha) as a WebP file.
    /// </summary>
    public static void Encode(
        IBufferWriter<byte> writer,
        int width,
        int height,
        ReadOnlySpan<byte> rgba,
        Vp8EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        if ((width <= 0) || (height <= 0) || (width > 16383) || (height > 16383))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A lossy WebP image is 1 to 16383 pixels across and down.");
        }

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException("The pixels aren't width x height RGBA.", nameof(rgba));
        }

        var picture = YuvPicture.FromRgba(rgba, width, height, options.UseSharpYuv || ((options.Preprocessing & 4) != 0));
        if (!options.Exact)
        {
            picture.CleanupTransparentArea();
        }

        var encoder = new Encoder(options, picture);
        encoder.Analyze();
        Task<byte[]>? alphaJob = null;
        if (encoder.HasAlpha)
        {
            if (options.UseThreads)
            {
                // (VP8EncStartAlpha's worker: the alpha reads only the alpha plane)
                alphaJob = Task.Run(() => AlphaEncoder.Encode(picture.A!, width, height, options.AlphaQuality, options.AlphaCompression, options.AlphaFiltering, options.Method, useThreads: true));
            }
            else
            {
                encoder.AlphaData = AlphaEncoder.Encode(picture.A!, width, height, options.AlphaQuality, options.AlphaCompression, options.AlphaFiltering, options.Method, useThreads: false);
            }
        }

        if (!encoder.UseTokens)
        {
            encoder.EncodeLoop();
        }
        else
        {
            encoder.TokenLoop();
        }

        if (alphaJob is not null)
        {
            encoder.AlphaData = alphaJob.GetAwaiter().GetResult();
        }

        encoder.Write(writer);
    }

    private static void Validate(
        Vp8EncoderOptions o)
    {
        if ((o.Quality < 0) || (o.Quality > 100) || (o.Method < 0) || (o.Method > 6) || (o.Segments < 1) || (o.Segments > 4)
            || (o.SnsStrength < 0) || (o.SnsStrength > 100) || (o.FilterStrength < 0) || (o.FilterStrength > 100)
            || (o.FilterSharpness < 0) || (o.FilterSharpness > 7) || (o.FilterType < 0) || (o.FilterType > 1)
            || (o.Pass < 1) || (o.Pass > 10) || (o.QMin < 0) || (o.QMax > 100) || (o.QMin > o.QMax)
            || (o.Preprocessing < 0) || (o.Preprocessing > 7) || (o.Partitions < 0) || (o.Partitions > 3)
            || (o.PartitionLimit < 0) || (o.PartitionLimit > 100) || (o.AlphaCompression < 0) || (o.AlphaCompression > 1)
            || (o.AlphaFiltering < 0) || (o.AlphaQuality < 0) || (o.AlphaQuality > 100) || (o.TargetSize < 0) || (o.TargetPsnr < 0))
        {
            throw new ArgumentException("The encoder options aren't valid.", nameof(o));
        }

        if ((o.Preprocessing & 2) != 0)
        {
            throw new NotSupportedException("Dithering (preprocessing 2) isn't supported.");
        }
    }

    // VP8Matrix
    private sealed class Matrix
    {
        public readonly ushort[] Q = new ushort[16];
        public readonly ushort[] Iq = new ushort[16];
        public readonly uint[] Bias = new uint[16];
        public readonly uint[] ZThresh = new uint[16];
        public readonly ushort[] Sharpen = new ushort[16];

        // Q, Iq (<< 16) and Sharpen (<< 32) together, for the trellis
        public readonly ulong[] Packed = new ulong[16];

        public void CopyTo(
            Matrix other)
        {
            this.Q.CopyTo(other.Q, 0);
            this.Iq.CopyTo(other.Iq, 0);
            this.Bias.CopyTo(other.Bias, 0);
            this.ZThresh.CopyTo(other.ZThresh, 0);
            this.Sharpen.CopyTo(other.Sharpen, 0);
            this.Packed.CopyTo(other.Packed, 0);
        }
    }

    // VP8SegmentInfo
    private sealed class SegmentInfo
    {
        public readonly Matrix Y1 = new();
        public readonly Matrix Y2 = new();
        public readonly Matrix Uv = new();
        public int Alpha;
        public int Beta;
        public int Quant;
        public int FStrength;
        public int MaxEdge;
        public int MinDisto;
        public int LambdaI16;
        public int LambdaI4;
        public int LambdaUv;
        public int LambdaMode;
        public int LambdaTrellis;
        public int TLambda;
        public int LambdaTrellisI16;
        public int LambdaTrellisI4;
        public int LambdaTrellisUv;
        public long I4Penalty;

        public void CopyTo(
            SegmentInfo other)
        {
            this.Y1.CopyTo(other.Y1);
            this.Y2.CopyTo(other.Y2);
            this.Uv.CopyTo(other.Uv);
            other.Alpha = this.Alpha;
            other.Beta = this.Beta;
            other.Quant = this.Quant;
            other.FStrength = this.FStrength;
            other.MaxEdge = this.MaxEdge;
            other.MinDisto = this.MinDisto;
            other.LambdaI16 = this.LambdaI16;
            other.LambdaI4 = this.LambdaI4;
            other.LambdaUv = this.LambdaUv;
            other.LambdaMode = this.LambdaMode;
            other.LambdaTrellis = this.LambdaTrellis;
            other.TLambda = this.TLambda;
            other.LambdaTrellisI16 = this.LambdaTrellisI16;
            other.LambdaTrellisI4 = this.LambdaTrellisI4;
            other.LambdaTrellisUv = this.LambdaTrellisUv;
            other.I4Penalty = this.I4Penalty;
        }
    }

    // VP8ModeScore
    private sealed class ModeScore
    {
        public long D;
        public long SD;
        public long H;
        public long R;
        public long Score;
        public readonly short[] YDcLevels = new short[16];
        public readonly short[] YAcLevels = new short[16 * 16];
        public readonly short[] UvLevels = new short[8 * 16];
        public int ModeI16;
        public readonly byte[] ModesI4 = new byte[16];
        public int ModeUv;
        public uint Nz;
        public readonly sbyte[] Derr = new sbyte[2 * 3];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Init()
        {
            this.D = 0;
            this.SD = 0;
            this.R = 0;
            this.H = 0;
            this.Nz = 0;
            this.Score = MaxCost;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyScoreFrom(
            ModeScore src)
        {
            this.D = src.D;
            this.SD = src.SD;
            this.R = src.R;
            this.H = src.H;
            this.Nz = src.Nz;
            this.Score = src.Score;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddScore(
            ModeScore src)
        {
            this.D += src.D;
            this.SD += src.SD;
            this.R += src.R;
            this.H += src.H;
            this.Nz |= src.Nz;
            this.Score += src.Score;
        }

        public void CopyAllFrom(
            ModeScore src)
        {
            this.CopyScoreFrom(src);
            src.YDcLevels.CopyTo(this.YDcLevels, 0);
            src.YAcLevels.CopyTo(this.YAcLevels, 0);
            src.UvLevels.CopyTo(this.UvLevels, 0);
            this.ModeI16 = src.ModeI16;
            src.ModesI4.CopyTo(this.ModesI4, 0);
            this.ModeUv = src.ModeUv;
            src.Derr.CopyTo(this.Derr, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRdScore(
            int lambda)
        {
            this.Score = ((this.R + this.H) * lambda) + (256 * (this.D + this.SD));
        }
    }

    private sealed partial class Encoder
    {
        private readonly Vp8EncoderOptions config;
        public readonly YuvPicture Pic;
        public readonly int MbW;
        public readonly int MbH;
        public readonly int PredsW;

        // Filter and segment headers
        private bool filterSimple = true;
        private int filterLevel;
        private int filterSharpness;
        public int NumSegments;
        private bool updateMap;
        private int segmentHeaderSize;

        private readonly int profile;
        public readonly int NumParts;
        private BitWriter bw = new(0);
        private readonly BitWriter[] parts;
        private readonly TokenBuffer tokens = new();

        public readonly SegmentInfo[] Dqm = [new(), new(), new(), new()];
        private int baseQuant;
        private int alpha;
        private int uvAlpha;
        private int dqY1Dc;
        private int dqY2Dc;
        private int dqY2Ac;
        private int dqUvDc;
        private int dqUvAc;

        // VP8EncProba
        private readonly byte[] segmentProbas = [255, 255, 255];
        private int skipProba;
        private readonly byte[] coeffs = new byte[NumTypes * NumBands * NumCtx * NumProbas];
        private readonly uint[] stats = new uint[NumTypes * NumBands * NumCtx * NumProbas];
        private readonly ushort[] levelCost = new ushort[NumTypes * NumBands * NumCtx * (MaxVariableLevel + 1)];
        private bool probaDirty;
        private bool useSkipProba;
        private int nbSkip;

        private readonly int method;
        private readonly int rdOptLevel;
        private int maxI4HeaderBits;
        private readonly long mbHeaderLimit;
        private readonly bool doSearch;
        public readonly bool UseTokens;

        // Per macroblock: the modes and the contexts
        public readonly byte[] MbType;
        public readonly byte[] MbUvMode;
        public readonly byte[] MbSkip;
        public readonly byte[] MbSegment;
        public readonly byte[] MbAlpha;
        public readonly byte[] Preds;     // (4 mbW + 1) x (4 mbH + 1), from -predsW - 1
        public readonly int PredsBase;
        public readonly uint[] Nz;        // mbW + 1, from -1
        public readonly byte[] YTop;
        public readonly byte[] UvTop;
        public readonly sbyte[]? TopDerr;  // mbW x [u/v][top/left]

        public bool HasAlpha;
        public byte[]? AlphaData;

        public Encoder(
            Vp8EncoderOptions config,
            YuvPicture picture)
        {
            this.config = config;
            this.Pic = picture;
            bool useFilter = config.FilterStrength > 0;
            this.MbW = (picture.Width + 15) >> 4;
            this.MbH = (picture.Height + 15) >> 4;
            this.PredsW = (4 * this.MbW) + 1;
            int predsH = (4 * this.MbH) + 1;
            this.Preds = new byte[this.PredsW * predsH];
            this.PredsBase = 1 + this.PredsW;
            this.Nz = new uint[this.MbW + 1];
            int count = this.MbW * this.MbH;
            this.MbType = new byte[count];
            this.MbUvMode = new byte[count];
            this.MbSkip = new byte[count];
            this.MbSegment = new byte[count];
            this.MbAlpha = new byte[count];
            this.YTop = new byte[this.MbW * 16];
            this.UvTop = new byte[this.MbW * 16];
            if ((config.Quality <= ErrorDiffusionQuality) || (config.Pass > 1))
            {
                this.TopDerr = new sbyte[this.MbW * 4];
            }

            this.NumParts = 1 << config.Partitions;
            this.profile = useFilter ? ((config.FilterType == 1) ? 0 : 1) : 2;

            // MapConfigToTools
            this.method = config.Method;
            int limit = 100 - config.PartitionLimit;
            this.rdOptLevel = (this.method >= 6) ? 3 : (this.method >= 5) ? 2 : (this.method >= 3) ? 1 : 0;
            this.maxI4HeaderBits = 256 * 16 * 16 * (limit * limit) / (100 * 100);
            this.mbHeaderLimit = (long)256 * 510 * 8 * 1024 / (this.MbW * this.MbH);
            this.doSearch = (config.TargetSize > 0) || (config.TargetPsnr > 0);
            this.UseTokens = this.rdOptLevel >= 1;
            if (this.UseTokens)
            {
                this.NumParts = 1;
            }

            this.parts = new BitWriter[this.NumParts];

            // VP8DefaultProbas
            CoeffsProba0.CopyTo(this.coeffs);
            this.probaDirty = true;

            // ResetSegmentHeader, ResetFilterHeader
            this.NumSegments = config.Segments;
            this.updateMap = this.NumSegments > 1;

            // ResetBoundaryPredictions: the row above and the column left are DC (0), as allocated
            this.HasAlpha = picture.A is not null;
        }

        // VP8EncWrite and its headers (syntax_enc.c)
        public void Write(
            IBufferWriter<byte> writer)
        {
            this.GeneratePartition0();
            long vp8Size = 10 + this.bw.Size + (3 * (this.NumParts - 1));
            for (int p = 0; p < this.NumParts; p++)
            {
                vp8Size += this.parts[p].Size;
            }

            int pad = (int)(vp8Size & 1);
            vp8Size += pad;
            long riffSize = 4 + 8 + vp8Size;
            if (this.HasAlpha)
            {
                riffSize += 8 + 10;
                int alphaSize = this.AlphaData!.Length;
                riffSize += 8 + alphaSize + (alphaSize & 1);
            }

            if (riffSize > 0xfffffffeL)
            {
                throw new ArgumentException("The image is too large for a WebP file.");
            }

            int size0 = this.bw.Size;
            if (size0 >= MaxPartition0Size)
            {
                throw new NotSupportedException("The image's first VP8 partition is too large (try a higher partition limit).");
            }

            Span<byte> header = stackalloc byte[30];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)riffSize);
            "WEBP"u8.CopyTo(header.Slice(8));
            writer.Write(header.Slice(0, 12));
            if (this.HasAlpha)
            {
                "VP8X"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), 10);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8), 0x10);
                PutLe24(header.Slice(12), this.Pic.Width - 1);
                PutLe24(header.Slice(15), this.Pic.Height - 1);
                writer.Write(header.Slice(0, 18));
                var alphaData = this.AlphaData!;
                "ALPH"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)alphaData.Length);
                writer.Write(header.Slice(0, 8));
                writer.Write(alphaData);
                if ((alphaData.Length & 1) != 0)
                {
                    writer.Write([(byte)0]);
                }
            }

            "VP8 "u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), (uint)vp8Size);
            writer.Write(header.Slice(0, 8));

            // The frame header: a key frame, the profile, shown, and the first partition's size
            uint bits = (uint)(0 | (this.profile << 1) | (1 << 4) | (size0 << 5));
            header[0] = (byte)bits;
            header[1] = (byte)(bits >> 8);
            header[2] = (byte)(bits >> 16);
            header[3] = 0x9d;
            header[4] = 0x01;
            header[5] = 0x2a;
            header[6] = (byte)(this.Pic.Width & 0xff);
            header[7] = (byte)(this.Pic.Width >> 8);
            header[8] = (byte)(this.Pic.Height & 0xff);
            header[9] = (byte)(this.Pic.Height >> 8);
            writer.Write(header.Slice(0, 10));
            writer.Write(this.bw.Bytes);

            // EmitPartitionsSize
            for (int p = 0; p < this.NumParts - 1; p++)
            {
                int partSize = this.parts[p].Size;
                if (partSize >= (1 << 24))
                {
                    throw new NotSupportedException("A VP8 partition is too large.");
                }

                writer.Write([(byte)partSize, (byte)(partSize >> 8), (byte)(partSize >> 16)]);
            }

            for (int p = 0; p < this.NumParts; p++)
            {
                writer.Write(this.parts[p].Bytes);
            }

            if (pad != 0)
            {
                writer.Write([(byte)0]);
            }
        }

        private static void PutLe24(
            Span<byte> b,
            int v)
        {
            b[0] = (byte)v;
            b[1] = (byte)(v >> 8);
            b[2] = (byte)(v >> 16);
        }

        // GeneratePartition0
        private void GeneratePartition0()
        {
            var w = this.bw = new BitWriter((this.MbW * this.MbH * 7) / 8);
            w.PutBitUniform(false);
            w.PutBitUniform(false);

            // PutSegmentHeader
            if (w.PutBitUniform(this.NumSegments > 1))
            {
                w.PutBitUniform(this.updateMap);
                if (w.PutBitUniform(true))
                {
                    w.PutBitUniform(true);
                    for (int s = 0; s < NumMbSegments; s++)
                    {
                        w.PutSignedBits(this.Dqm[s].Quant, 7);
                    }

                    for (int s = 0; s < NumMbSegments; s++)
                    {
                        w.PutSignedBits(this.Dqm[s].FStrength, 6);
                    }
                }

                if (this.updateMap)
                {
                    for (int s = 0; s < 3; s++)
                    {
                        if (w.PutBitUniform(this.segmentProbas[s] != 255))
                        {
                            w.PutBits(this.segmentProbas[s], 8);
                        }
                    }
                }
            }

            // PutFilterHeader (no i4x4 level delta)
            w.PutBitUniform(this.filterSimple);
            w.PutBits((uint)this.filterLevel, 6);
            w.PutBits((uint)this.filterSharpness, 3);
            w.PutBitUniform(false);

            w.PutBits((uint)((this.NumParts == 8) ? 3 : (this.NumParts == 4) ? 2 : (this.NumParts == 2) ? 1 : 0), 2);

            // PutQuant
            w.PutBits((uint)this.baseQuant, 7);
            w.PutSignedBits(this.dqY1Dc, 4);
            w.PutSignedBits(this.dqY2Dc, 4);
            w.PutSignedBits(this.dqY2Ac, 4);
            w.PutSignedBits(this.dqUvDc, 4);
            w.PutSignedBits(this.dqUvAc, 4);
            w.PutBitUniform(false);
            this.WriteProbas(w);
            this.CodeIntraModes(w);
            w.Finish();
        }

        // VP8WriteProbas
        private void WriteProbas(
            BitWriter w)
        {
            var defaults = CoeffsProba0;
            var update = CoeffsUpdateProba;
            for (int i = 0; i < this.coeffs.Length; i++)
            {
                byte p0 = this.coeffs[i];
                if (w.PutBit(p0 != defaults[i], update[i]))
                {
                    w.PutBits(p0, 8);
                }
            }

            if (w.PutBitUniform(this.useSkipProba))
            {
                w.PutBits((uint)this.skipProba, 8);
            }
        }

        // VP8CodeIntraModes
        private void CodeIntraModes(
            BitWriter w)
        {
            var bmodes = BModesProba;
            for (int n = 0; n < this.MbW * this.MbH; n++)
            {
                int x = n % this.MbW;
                int y = n / this.MbW;
                int preds = this.PredsBase + (y * 4 * this.PredsW) + (x * 4);
                if (this.updateMap)
                {
                    // PutSegment
                    int s = this.MbSegment[n];
                    int p = 0;
                    if (w.PutBit(s >= 2, this.segmentProbas[0]))
                    {
                        p = 1;
                    }

                    w.PutBit((s & 1) != 0, this.segmentProbas[1 + p]);
                }

                if (this.useSkipProba)
                {
                    w.PutBit(this.MbSkip[n] != 0, this.skipProba);
                }

                if (w.PutBit(this.MbType[n] != 0, 145))
                {
                    // PutI16Mode
                    int mode = this.Preds[preds];
                    if (w.PutBit((mode == TmPred) || (mode == HPred), 156))
                    {
                        w.PutBit(mode == TmPred, 128);
                    }
                    else
                    {
                        w.PutBit(mode == VPred, 163);
                    }
                }
                else
                {
                    int topPred = preds - this.PredsW;
                    for (int yy = 0; yy < 4; yy++)
                    {
                        int left = this.Preds[preds - 1];
                        for (int xx = 0; xx < 4; xx++)
                        {
                            var probas = bmodes.Slice(((this.Preds[topPred + xx] * NumBModes) + left) * 9, 9);
                            left = PutI4Mode(w, this.Preds[preds + xx], probas);
                        }

                        topPred = preds;
                        preds += this.PredsW;
                    }
                }

                // PutUVMode
                int uvMode = this.MbUvMode[n];
                if (w.PutBit(uvMode != DcPred, 142))
                {
                    if (w.PutBit(uvMode != VPred, 114))
                    {
                        w.PutBit(uvMode != HPred, 183);
                    }
                }
            }
        }

        private static int PutI4Mode(
            BitWriter w,
            int mode,
            ReadOnlySpan<byte> prob)
        {
            if (w.PutBit(mode != BDcPred, prob[0]))
            {
                if (w.PutBit(mode != BTmPred, prob[1]))
                {
                    if (w.PutBit(mode != BVePred, prob[2]))
                    {
                        if (!w.PutBit(mode >= BLdPred, prob[3]))
                        {
                            if (w.PutBit(mode != BHePred, prob[4]))
                            {
                                w.PutBit(mode != BRdPred, prob[5]);
                            }
                        }
                        else if (w.PutBit(mode != BLdPred, prob[6]))
                        {
                            if (w.PutBit(mode != BVlPred, prob[7]))
                            {
                                w.PutBit(mode != BHdPred, prob[8]);
                            }
                        }
                    }
                }
            }

            return mode;
        }
    }

    // The prediction modes (common_dec.h)
    private const int BDcPred = 0;
    private const int BTmPred = 1;
    private const int BVePred = 2;
    private const int BHePred = 3;
    private const int BRdPred = 4;
    private const int BVrPred = 5;
    private const int BLdPred = 6;
    private const int BVlPred = 7;
    private const int BHdPred = 8;
    private const int BHuPred = 9;
    private const int DcPred = BDcPred;
    private const int VPred = BVePred;
    private const int HPred = BHePred;
    private const int TmPred = BTmPred;
}
