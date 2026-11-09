// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/frame_enc.c, and the recording in cost_enc.c and token_enc.c),
// modified by Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    private sealed partial class Encoder
    {
        // VP8RecordCoeffs
        private int RecordCoeffs(
            int ctx,
            in Residual res)
        {
            int n = res.First;
            int type = res.CoeffType;
            ref uint stats = ref MemoryMarshal.GetArrayDataReference(this.stats);
            ref byte bands = ref MemoryMarshal.GetReference(EncBands);
            ref short coeffs = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(res.Coeffs), res.CoeffsAt);
            int s = ProbaAt(type, n, ctx);
            if (res.Last < 0)
            {
                RecordStats(false, ref stats, s);
                return 0;
            }

            while (n <= res.Last)
            {
                int v;
                RecordStats(true, ref stats, s);
                while ((v = Unsafe.Add(ref coeffs, n++)) == 0)
                {
                    RecordStats(false, ref stats, s + 1);
                    s = ProbaAt(type, Unsafe.Add(ref bands, n), 0);
                }

                RecordStats(true, ref stats, s + 1);
                if (!RecordStats(2u < (uint)(v + 1), ref stats, s + 2))
                {
                    s = ProbaAt(type, Unsafe.Add(ref bands, n), 1);
                }
                else
                {
                    v = Math.Abs(v);
                    if (!RecordStats(v > 4, ref stats, s + 3))
                    {
                        if (RecordStats(v != 2, ref stats, s + 4))
                        {
                            RecordStats(v == 4, ref stats, s + 5);
                        }
                    }
                    else if (!RecordStats(v > 10, ref stats, s + 6))
                    {
                        RecordStats(v > 6, ref stats, s + 7);
                    }
                    else if (!RecordStats(v >= 3 + (8 << 2), ref stats, s + 8))
                    {
                        RecordStats(v >= 3 + (8 << 1), ref stats, s + 9);
                    }
                    else
                    {
                        RecordStats(v >= 3 + (8 << 3), ref stats, s + 10);
                    }

                    s = ProbaAt(type, Unsafe.Add(ref bands, n), 2);
                }
            }

            if (n < 16)
            {
                RecordStats(false, ref stats, s);
            }

            return 1;
        }

        // RecordResiduals
        private void RecordResiduals(
            Iterator it,
            ModeScore rd)
        {
            it.NzToBytes();
            Residual res;
            if (this.MbType[it.Mb] == 1)
            {
                res = Residual.Init(0, TypeI16Dc);
                res.SetCoeffs(rd.YDcLevels, 0);
                it.TopNz[8] = it.LeftNz[8] = this.RecordCoeffs(it.TopNz[8] + it.LeftNz[8], res);
                res = Residual.Init(1, TypeI16Ac);
            }
            else
            {
                res = Residual.Init(0, TypeI4Ac);
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int ctx = it.TopNz[x] + it.LeftNz[y];
                    res.SetCoeffs(rd.YAcLevels, (x + (y * 4)) * 16);
                    it.TopNz[x] = it.LeftNz[y] = this.RecordCoeffs(ctx, res);
                }
            }

            res = Residual.Init(0, TypeChromaA);
            for (int ch = 0; ch <= 2; ch += 2)
            {
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                        res.SetCoeffs(rd.UvLevels, ((ch * 2) + x + (y * 2)) * 16);
                        it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = this.RecordCoeffs(ctx, res);
                    }
                }
            }

            it.BytesToNz();
        }

        // AddToken: a token, and its bit in the statistics (room reserved by RecordTokens)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AddToken(
            ref ushort tokens,
            ref int count,
            ref uint stats,
            bool bit,
            int probaIndex,
            int statsAt)
        {
            Unsafe.Add(ref tokens, count++) = (ushort)((bit ? 1 << 15 : 0) | probaIndex);
            ref uint p = ref Unsafe.Add(ref stats, statsAt);
            uint s = p;
            if (s >= 0xfffe0000u)
            {
                s = ((s + 1u) >> 1) & 0x7fff7fffu;
            }

            p = s + 0x00010000u + (bit ? 1u : 0u);
            return bit;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddConstantToken(
            ref ushort tokens,
            ref int count,
            bool bit,
            int proba)
        {
            Unsafe.Add(ref tokens, count++) = (ushort)((bit ? 1 << 15 : 0) | TokenBuffer.FixedProbaBit | proba);
        }

        // VP8RecordCoeffTokens
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int RecordCoeffTokens(
            int ctx,
            in Residual res)
        {
            ref short coeffs = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(res.Coeffs), res.CoeffsAt);
            ref ushort tokens = ref MemoryMarshal.GetArrayDataReference(this.tokens.Tokens);
            ref uint stats = ref MemoryMarshal.GetArrayDataReference(this.stats);
            ref byte bands = ref MemoryMarshal.GetReference(EncBands);
            int count = this.tokens.Count;
            int type = res.CoeffType;
            int last = res.Last;
            int n = res.First;
            int baseId = ProbaAt(type, n, ctx);
            if (!AddToken(ref tokens, ref count, ref stats, last >= 0, baseId + 0, baseId + 0))
            {
                this.tokens.Count = count;
                return 0;
            }

            while (n < 16)
            {
                int c = Unsafe.Add(ref coeffs, n++);
                bool sign = c < 0;
                uint v = (uint)(sign ? -c : c);
                if (!AddToken(ref tokens, ref count, ref stats, v != 0, baseId + 1, baseId + 1))
                {
                    baseId = ProbaAt(type, Unsafe.Add(ref bands, n), 0);
                    continue;
                }

                if (!AddToken(ref tokens, ref count, ref stats, v > 1, baseId + 2, baseId + 2))
                {
                    baseId = ProbaAt(type, Unsafe.Add(ref bands, n), 1);
                }
                else
                {
                    AddLargeLevelTokens(ref tokens, ref count, ref stats, v, baseId);
                    baseId = ProbaAt(type, Unsafe.Add(ref bands, n), 2);
                }

                AddConstantToken(ref tokens, ref count, sign, 128);
                if ((n == 16) || !AddToken(ref tokens, ref count, ref stats, n <= last, baseId + 0, baseId + 0))
                {
                    break;
                }
            }

            this.tokens.Count = count;
            return 1;
        }

        // The tokens of a level above 1 (Cat5 and Cat6 record their second bit in stats + 9, as
        // libwebp does)
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddLargeLevelTokens(
            ref ushort tokens,
            ref int count,
            ref uint stats,
            uint v,
            int baseId)
        {
            int s = baseId;
            if (!AddToken(ref tokens, ref count, ref stats, v > 4, baseId + 3, s + 3))
            {
                if (AddToken(ref tokens, ref count, ref stats, v != 2, baseId + 4, s + 4))
                {
                    AddToken(ref tokens, ref count, ref stats, v == 4, baseId + 5, s + 5);
                }
            }
            else if (!AddToken(ref tokens, ref count, ref stats, v > 10, baseId + 6, s + 6))
            {
                if (!AddToken(ref tokens, ref count, ref stats, v > 6, baseId + 7, s + 7))
                {
                    AddConstantToken(ref tokens, ref count, v == 6, 159);
                }
                else
                {
                    AddConstantToken(ref tokens, ref count, v >= 9, 165);
                    AddConstantToken(ref tokens, ref count, (v & 1) == 0, 145);
                }
            }
            else
            {
                int mask;
                ReadOnlySpan<byte> tab;
                uint residue = v - 3;
                if (residue < (8 << 1))
                {
                    AddToken(ref tokens, ref count, ref stats, false, baseId + 8, s + 8);
                    AddToken(ref tokens, ref count, ref stats, false, baseId + 9, s + 9);
                    residue -= 8 << 0;
                    mask = 1 << 2;
                    tab = Cat3;
                }
                else if (residue < (8 << 2))
                {
                    AddToken(ref tokens, ref count, ref stats, false, baseId + 8, s + 8);
                    AddToken(ref tokens, ref count, ref stats, true, baseId + 9, s + 9);
                    residue -= 8 << 1;
                    mask = 1 << 3;
                    tab = Cat4;
                }
                else if (residue < (8 << 3))
                {
                    AddToken(ref tokens, ref count, ref stats, true, baseId + 8, s + 8);
                    AddToken(ref tokens, ref count, ref stats, false, baseId + 10, s + 9);
                    residue -= 8 << 2;
                    mask = 1 << 4;
                    tab = Cat5;
                }
                else
                {
                    AddToken(ref tokens, ref count, ref stats, true, baseId + 8, s + 8);
                    AddToken(ref tokens, ref count, ref stats, true, baseId + 10, s + 9);
                    residue -= 8 << 3;
                    mask = 1 << 10;
                    tab = Cat6;
                }

                int t = 0;
                while (mask != 0)
                {
                    AddConstantToken(ref tokens, ref count, (residue & (uint)mask) != 0, tab[t++]);
                    mask >>= 1;
                }
            }
        }

        // RecordTokens
        private void RecordTokens(
            Iterator it,
            ModeScore rd)
        {
            // (at most 19 tokens per coefficient and one per block)
            this.tokens.Reserve(25 * ((16 * 19) + 1));
            it.NzToBytes();
            Residual res;
            if (this.MbType[it.Mb] == 1)
            {
                int ctx = it.TopNz[8] + it.LeftNz[8];
                res = Residual.Init(0, TypeI16Dc);
                res.SetCoeffs(rd.YDcLevels, 0);
                it.TopNz[8] = it.LeftNz[8] = this.RecordCoeffTokens(ctx, res);
                res = Residual.Init(1, TypeI16Ac);
            }
            else
            {
                res = Residual.Init(0, TypeI4Ac);
            }

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int ctx = it.TopNz[x] + it.LeftNz[y];
                    res.SetCoeffs(rd.YAcLevels, (x + (y * 4)) * 16);
                    it.TopNz[x] = it.LeftNz[y] = this.RecordCoeffTokens(ctx, res);
                }
            }

            res = Residual.Init(0, TypeChromaA);
            for (int ch = 0; ch <= 2; ch += 2)
            {
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        int ctx = it.TopNz[4 + ch + x] + it.LeftNz[4 + ch + y];
                        res.SetCoeffs(rd.UvLevels, ((ch * 2) + x + (y * 2)) * 16);
                        it.TopNz[4 + ch + x] = it.LeftNz[4 + ch + y] = this.RecordCoeffTokens(ctx, res);
                    }
                }
            }

            it.BytesToNz();
        }
    }
}
