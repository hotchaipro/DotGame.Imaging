// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, the coefficients kept in
// zigzag order, and the x64 forms new.
//
// The forward DCTs, the quantization by reciprocals and the dummy blocks follow jfdctint.c,
// jfdctfst.c, jcdctmgr.c and jccoefct.c, which carry these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1996, Thomas G. Lane.
//   libjpeg-turbo Modifications:
//   Copyright (C) 1999-2006, MIYASAKA Masaru.
//   Copyright 2009 Pierre Ossman <ossman@cendio.se> for Cendio AB
//   Copyright (C) 2011, 2014-2015, 2022, 2024, D. R. Commander.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // jfdctint.c's constants, 13 fraction bits
    private const short Fix0298631336 = 2446;
    private const short Fix0390180644 = 3196;
    private const short Fix0541196100 = 4433;
    private const short Fix0765366865 = 6270;
    private const short Fix0899976223 = 7373;
    private const short Fix1175875602 = 9633;
    private const short Fix1501321110 = 12299;
    private const short Fix1847759065 = 15137;
    private const short Fix1961570560 = 16069;
    private const short Fix2053119869 = 16819;
    private const short Fix2562915447 = 20995;
    private const short Fix3072711026 = 25172;

    // jfdctfst.c's constants, 8 fraction bits
    private const int FastFix0382683433 = 98;
    private const int FastFix0541196100 = 139;
    private const int FastFix0707106781 = 181;
    private const int FastFix1306562965 = 334;

    // The divisor table's sections: reciprocals, corrections, shifts (the product's beyond 16 bits),
    // the shifts negated (for a variable left shift) and the scales 2^(16 - shift) (for a multiply-high)
    private const int Corrections = 64;
    private const int Shifts = 128;
    private const int NegatedShifts = 192;
    private const int Scales = 256;

    // jcdctmgr.c's AA&N scale factors, 14 fraction bits, which the fast DCT's divisors include
    private static ReadOnlySpan<short> AanScales =>
    [
        16384, 22725, 21407, 19266, 16384, 12873, 8867, 4520,
        22725, 31521, 29692, 26722, 22725, 17855, 12299, 6270,
        21407, 29692, 27969, 25172, 21407, 16819, 11585, 5906,
        19266, 26722, 25172, 22654, 19266, 15137, 10426, 5315,
        16384, 22725, 21407, 19266, 16384, 12873, 8867, 4520,
        12873, 17855, 16819, 15137, 12873, 10114, 6967, 3552,
        8867, 12299, 11585, 10426, 8867, 6967, 4799, 2446,
        4520, 6270, 5906, 5315, 4520, 3552, 2446, 1247,
    ];

    // start_pass_fdctmgr: each table's divisors, the quantization values times 8 (the integer DCT) or
    // times the AA&N scales (the fast one), as reciprocals, corrections and shifts; and whether the
    // table needs the scalar quantization, as libjpeg-turbo's SIMD forms do when a shift isn't positive
    private static ushort[] MakeDivisors(
        ushort[] quantization,
        JpegEncoderDct dct,
        out bool scalar)
    {
        var divisors = new ushort[64 * 5];
        scalar = false;
        for (int i = 0; i < 64; i++)
        {
            int divisor = (dct == JpegEncoderDct.Fast)
                ? (((quantization[i] * AanScales[i]) + (1 << 10)) >> 11)
                : (quantization[i] << 3);
            ComputeReciprocal(divisor, divisors, i);
            int shift = (short)divisors[Shifts + i];
            divisors[NegatedShifts + i] = (ushort)(short)-shift;
            divisors[Scales + i] = (shift > 0) ? (ushort)(1 << (16 - shift)) : (ushort)0;
            scalar |= shift <= 0;
        }

        return divisors;
    }

    // compute_reciprocal: a divisor as a 16-bit reciprocal, a rounding correction and a shift, so
    // (x + correction) * reciprocal >> (16 + shift) is x / divisor rounded
    private static void ComputeReciprocal(
        int divisor,
        ushort[] table,
        int i)
    {
        if (divisor <= 1)
        {
            // Unquantized (the fast DCT's at quality 100): a shift of -16, so the product is the value
            table[i] = 1;
            table[Corrections + i] = 0;
            table[Shifts + i] = unchecked((ushort)-16);
            return;
        }

        int b = 31 - BitOperations.LeadingZeroCount((uint)divisor);
        int r = 16 + b;
        uint fq = (uint)((1L << r) / divisor);
        uint fr = (uint)((1L << r) % divisor);
        int c = divisor / 2;
        if (fr == 0)
        {
            fq >>= 1;
            r--;
        }
        else if (fr <= (uint)(divisor / 2))
        {
            c++;
        }
        else
        {
            fq++;
        }

        table[i] = (ushort)fq;
        table[Corrections + i] = (ushort)c;
        table[Shifts + i] = (ushort)(r - 16);
    }

    /// <summary>
    /// The DCT of each block of a component's iMCU row, quantized, into its coefficients at block row
    /// rowBase, with jccoefct.c's dummy blocks: those past the right edge are zero with the last real
    /// block's DC, and the rows past the bottom zero with each MCU's DC from the row above.
    /// </summary>
    private void TransformRow(
        Component c,
        int row,
        int rowBase)
    {
        var coefficients = c.Coefficients;
        var table = this.divisors[c.QuantTable];
        bool scalarQuantize = this.scalarQuantize[c.QuantTable];
        bool fast = this.options.Dct == JpegEncoderDct.Fast;
        int rowBlocks = c.BlocksPerRow * 64;
        for (int br = 0; br < c.V; br++)
        {
            int target = (rowBase + br) * rowBlocks;
            if ((row * c.V) + br < c.HeightInBlocks)
            {
                ref byte samples = ref c.Plane[br * 8 * c.PlaneStride];
                ref short block = ref coefficients[target];
                if (AdvSimd.Arm64.IsSupported && !scalarQuantize)
                {
                    for (int bx = 0; bx < c.WidthInBlocks; bx++)
                    {
                        TransformBlockArm64(ref Unsafe.Add(ref samples, bx * 8), c.PlaneStride, table, fast, ref Unsafe.Add(ref block, bx * 64));
                    }
                }
                else
                {
                    for (int bx = 0; bx < c.WidthInBlocks; bx++)
                    {
                        TransformBlock(ref Unsafe.Add(ref samples, bx * 8), c.PlaneStride, table, fast, scalarQuantize, ref Unsafe.Add(ref block, bx * 64));
                    }
                }

                short lastDc = coefficients[target + ((c.WidthInBlocks - 1) * 64)];
                for (int bx = c.WidthInBlocks; bx < c.BlocksPerRow; bx++)
                {
                    var dummy = coefficients.AsSpan(target + (bx * 64), 64);
                    dummy.Clear();
                    dummy[0] = lastDc;
                }
            }
            else
            {
                coefficients.AsSpan(target, rowBlocks).Clear();
                int above = target - rowBlocks;
                for (int m = 0; m < this.mcusPerRow; m++)
                {
                    short lastDc = coefficients[above + (((m * c.H) + c.H - 1) * 64)];
                    for (int bx = m * c.H; bx < (m + 1) * c.H; bx++)
                    {
                        coefficients[target + (bx * 64)] = lastDc;
                    }
                }
            }
        }
    }

    // forward_DCT for a block: convsamp, the DCT, quantize, and the coefficients put in zigzag order
    private static void TransformBlock(
        ref byte samples,
        int stride,
        ushort[] table,
        bool fast,
        bool scalarQuantize,
        ref short output)
    {
        Span<short> workspace = stackalloc short[64];
        Span<short> natural = stackalloc short[64];
        if (Sse2.IsSupported && !fast && !scalarQuantize)
        {
            TransformBlockSse2(ref samples, stride, table, natural);
        }
        else
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    workspace[(y * 8) + x] = (short)(Unsafe.Add(ref samples, (y * stride) + x) - 128);
                }
            }

            if (fast)
            {
                FastDct(workspace);
            }
            else
            {
                IntegerDct(workspace);
            }

            Quantize(workspace, table, natural);
        }

        for (int k = 0; k < 64; k++)
        {
            Unsafe.Add(ref output, k) = natural[NaturalOrder[k]];
        }
    }

    // quantize: each coefficient divided by its divisor, rounded, with the sign restored
    private static void Quantize(
        ReadOnlySpan<short> workspace,
        ushort[] table,
        Span<short> block)
    {
        for (int i = 0; i < 64; i++)
        {
            int temp = workspace[i];
            uint recip = table[i];
            uint corr = table[Corrections + i];
            int shift = (short)table[Shifts + i] + 16;
            if (temp < 0)
            {
                temp = -(int)((((uint)-temp + corr) * recip) >> shift);
            }
            else
            {
                temp = (int)((((uint)temp + corr) * recip) >> shift);
            }

            block[i] = (short)temp;
        }
    }

    // jpeg_fdct_islow: the accurate integer DCT, rows then columns, the result 8 times a true DCT's
    private static void IntegerDct(
        Span<short> data)
    {
        for (int i = 0; i < 64; i += 8)
        {
            int tmp0 = data[i] + data[i + 7];
            int tmp7 = data[i] - data[i + 7];
            int tmp1 = data[i + 1] + data[i + 6];
            int tmp6 = data[i + 1] - data[i + 6];
            int tmp2 = data[i + 2] + data[i + 5];
            int tmp5 = data[i + 2] - data[i + 5];
            int tmp3 = data[i + 3] + data[i + 4];
            int tmp4 = data[i + 3] - data[i + 4];

            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;

            data[i] = (short)((tmp10 + tmp11) << 2);
            data[i + 4] = (short)((tmp10 - tmp11) << 2);
            int z1 = (tmp12 + tmp13) * Fix0541196100;
            data[i + 2] = (short)((z1 + (tmp13 * Fix0765366865) + (1 << 10)) >> 11);
            data[i + 6] = (short)((z1 - (tmp12 * Fix1847759065) + (1 << 10)) >> 11);

            z1 = tmp4 + tmp7;
            int z2 = tmp5 + tmp6;
            int z3 = tmp4 + tmp6;
            int z4 = tmp5 + tmp7;
            int z5 = (z3 + z4) * Fix1175875602;
            tmp4 *= Fix0298631336;
            tmp5 *= Fix2053119869;
            tmp6 *= Fix3072711026;
            tmp7 *= Fix1501321110;
            z1 *= -Fix0899976223;
            z2 *= -Fix2562915447;
            z3 *= -Fix1961570560;
            z4 *= -Fix0390180644;
            z3 += z5;
            z4 += z5;
            data[i + 7] = (short)((tmp4 + z1 + z3 + (1 << 10)) >> 11);
            data[i + 5] = (short)((tmp5 + z2 + z4 + (1 << 10)) >> 11);
            data[i + 3] = (short)((tmp6 + z2 + z3 + (1 << 10)) >> 11);
            data[i + 1] = (short)((tmp7 + z1 + z4 + (1 << 10)) >> 11);
        }

        for (int i = 0; i < 8; i++)
        {
            int tmp0 = data[i] + data[i + 56];
            int tmp7 = data[i] - data[i + 56];
            int tmp1 = data[i + 8] + data[i + 48];
            int tmp6 = data[i + 8] - data[i + 48];
            int tmp2 = data[i + 16] + data[i + 40];
            int tmp5 = data[i + 16] - data[i + 40];
            int tmp3 = data[i + 24] + data[i + 32];
            int tmp4 = data[i + 24] - data[i + 32];

            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;

            data[i] = (short)((tmp10 + tmp11 + 2) >> 2);
            data[i + 32] = (short)((tmp10 - tmp11 + 2) >> 2);
            int z1 = (tmp12 + tmp13) * Fix0541196100;
            data[i + 16] = (short)((z1 + (tmp13 * Fix0765366865) + (1 << 14)) >> 15);
            data[i + 48] = (short)((z1 - (tmp12 * Fix1847759065) + (1 << 14)) >> 15);

            z1 = tmp4 + tmp7;
            int z2 = tmp5 + tmp6;
            int z3 = tmp4 + tmp6;
            int z4 = tmp5 + tmp7;
            int z5 = (z3 + z4) * Fix1175875602;
            tmp4 *= Fix0298631336;
            tmp5 *= Fix2053119869;
            tmp6 *= Fix3072711026;
            tmp7 *= Fix1501321110;
            z1 *= -Fix0899976223;
            z2 *= -Fix2562915447;
            z3 *= -Fix1961570560;
            z4 *= -Fix0390180644;
            z3 += z5;
            z4 += z5;
            data[i + 56] = (short)((tmp4 + z1 + z3 + (1 << 14)) >> 15);
            data[i + 40] = (short)((tmp5 + z2 + z4 + (1 << 14)) >> 15);
            data[i + 24] = (short)((tmp6 + z2 + z3 + (1 << 14)) >> 15);
            data[i + 8] = (short)((tmp7 + z1 + z4 + (1 << 14)) >> 15);
        }
    }

    // jpeg_fdct_ifast: the AA&N DCT, its arithmetic in 16 bits (DCTELEM), its multiplies truncated
    private static void FastDct(
        Span<short> data)
    {
        for (int i = 0; i < 64; i += 8)
        {
            FastDct1D(data, i, 1);
        }

        for (int i = 0; i < 8; i++)
        {
            FastDct1D(data, i, 8);
        }
    }

    private static void FastDct1D(
        Span<short> data,
        int i,
        int step)
    {
        short tmp0 = (short)(data[i] + data[i + (7 * step)]);
        short tmp7 = (short)(data[i] - data[i + (7 * step)]);
        short tmp1 = (short)(data[i + step] + data[i + (6 * step)]);
        short tmp6 = (short)(data[i + step] - data[i + (6 * step)]);
        short tmp2 = (short)(data[i + (2 * step)] + data[i + (5 * step)]);
        short tmp5 = (short)(data[i + (2 * step)] - data[i + (5 * step)]);
        short tmp3 = (short)(data[i + (3 * step)] + data[i + (4 * step)]);
        short tmp4 = (short)(data[i + (3 * step)] - data[i + (4 * step)]);

        short tmp10 = (short)(tmp0 + tmp3);
        short tmp13 = (short)(tmp0 - tmp3);
        short tmp11 = (short)(tmp1 + tmp2);
        short tmp12 = (short)(tmp1 - tmp2);

        data[i] = (short)(tmp10 + tmp11);
        data[i + (4 * step)] = (short)(tmp10 - tmp11);
        short z1 = FastMultiply((short)(tmp12 + tmp13), FastFix0707106781);
        data[i + (2 * step)] = (short)(tmp13 + z1);
        data[i + (6 * step)] = (short)(tmp13 - z1);

        tmp10 = (short)(tmp4 + tmp5);
        tmp11 = (short)(tmp5 + tmp6);
        tmp12 = (short)(tmp6 + tmp7);
        short z5 = FastMultiply((short)(tmp10 - tmp12), FastFix0382683433);
        short z2 = (short)(FastMultiply(tmp10, FastFix0541196100) + z5);
        short z4 = (short)(FastMultiply(tmp12, FastFix1306562965) + z5);
        short z3 = FastMultiply(tmp11, FastFix0707106781);
        short z11 = (short)(tmp7 + z3);
        short z13 = (short)(tmp7 - z3);
        data[i + (5 * step)] = (short)(z13 + z2);
        data[i + (3 * step)] = (short)(z13 - z2);
        data[i + step] = (short)(z11 + z4);
        data[i + (7 * step)] = (short)(z11 - z4);
    }

    // jfdctfst.c's MULTIPLY: the product shifted down 8 bits, truncated, as a DCTELEM
    private static short FastMultiply(
        short value,
        int constant) =>
        (short)((value * constant) >> 8);
}
