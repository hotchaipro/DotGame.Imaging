// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/enc/quant_enc.c and cost_enc.c, and src/dsp/cost.c), modified by Hot
// Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2011 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Encoder
{
    private sealed partial class Encoder
    {
        // TrellisQuantizeBlock: the levels that minimize rate x lambda + weighted distortion, each one
        // its plain quantization or one more (two nodes per position)
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool TrellisQuantizeBlock(
            short[] input,
            int inAt,
            short[] output,
            int outAt,
            int ctx0,
            int coeffType,
            Matrix mtx,
            int lambda)
        {
            ref uint positions = ref MemoryMarshal.GetReference(TrellisPositions);
            ref ushort levelCosts = ref MemoryMarshal.GetArrayDataReference(this.levelCost);
            ref byte probas = ref MemoryMarshal.GetArrayDataReference(this.coeffs);
            ref short inRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(input), inAt);
            ref ulong matrix = ref MemoryMarshal.GetArrayDataReference(mtx.Packed);
            ref int nodes = ref MemoryMarshal.GetArrayDataReference(this.trellisNodes);
            ref ushort fixedCosts = ref MemoryMarshal.GetReference(LevelFixedCosts);
            int first = (coeffType == TypeI16Ac) ? 1 : 0;
            int typeCosts = CostTable(coeffType, 0, 0);
            int typeProbas = ProbaAt(coeffType, 0, 0);
            int bestPath = -1;
            long bestScore;
            long score0, score1;
            int costs0, costs1;
            int last;
            {
                int thresh = mtx.Q[1] * mtx.Q[1] / 4;
                byte lastProba = Unsafe.Add(ref probas, typeProbas + ProbaAt(0, EncBands[first], ctx0));
                last = first - 1;
                if (AdvSimd.Arm64.IsSupported)
                {
                    // The coefficients in zigzag order (TBL), v * v > q * q / 4 as |v| > q / 2 (the
                    // same for integers), the last position from the mask, n >= first
                    var table = (Vector128.LoadUnsafe(ref inRef).AsByte(), Vector128.LoadUnsafe(ref inRef, 8).AsByte());
                    var z0 = AdvSimd.Arm64.VectorTableLookup(table, Vector128.Create((byte)0, 1, 2, 3, 8, 9, 16, 17, 10, 11, 4, 5, 6, 7, 12, 13)).AsInt16();
                    var z1 = AdvSimd.Arm64.VectorTableLookup(table, Vector128.Create((byte)18, 19, 24, 25, 26, 27, 20, 21, 14, 15, 22, 23, 28, 29, 30, 31)).AsInt16();
                    var half = Vector128.Create((ushort)(mtx.Q[1] >> 1));
                    var over = Vector128.Narrow(Vector128.GreaterThan(Vector128.Abs(z0).AsUInt16(), half), Vector128.GreaterThan(Vector128.Abs(z1).AsUInt16(), half)).AsByte();
                    ulong mask = AdvSimd.ShiftRightLogicalNarrowingLower(over.AsUInt16(), 4).AsUInt64().ToScalar() & (ulong.MaxValue << (4 * first));
                    if (mask != 0)
                    {
                        last = 15 - (BitOperations.LeadingZeroCount(mask) >> 2);
                    }
                }
                else
                {
                    for (int n = 15; n >= first; n--)
                    {
                        int v = Unsafe.Add(ref inRef, (int)(Unsafe.Add(ref positions, n) & 0xff));
                        if (v * v > thresh)
                        {
                            last = n;
                            break;
                        }
                    }
                }

                if (last < 15)
                {
                    ++last;
                }

                bestScore = (long)BitCost(false, lastProba) * lambda;
                score0 = score1 = (long)((ctx0 == 0) ? BitCost(true, lastProba) : 0) * lambda;
                costs0 = costs1 = typeCosts + CostTable(0, EncBands[first], ctx0);
            }

            for (int n = first; n <= last; n++)
            {
                uint position = Unsafe.Add(ref positions, n);
                int j = (int)(position & 0xff);
                ulong m = Unsafe.Add(ref matrix, j);
                uint q = (uint)m & 0xffff;
                uint iq = (uint)(m >> 16) & 0xffff;
                int x = Unsafe.Add(ref inRef, j);
                int signBit = (x >> 31) & (1 << 16);
                uint coeff0 = (uint)((x ^ (x >> 31)) - (x >> 31)) + (uint)(m >> 32);
                int level0 = QuantDiv(coeff0, iq, 0);
                int threshLevel = QuantDiv(coeff0, iq, 0x80 << (QFix - 8));
                level0 = (level0 > MaxLevel) ? MaxLevel : level0;
                threshLevel = (threshLevel > MaxLevel) ? MaxLevel : threshLevel;
                uint weight = position >> 16;
                int band = (int)(position >> 8) & 0xff;
                int bandCosts = typeCosts + CostTable(0, band, 0);
                long prev0 = score0, prev1 = score1;
                int prevCosts0 = costs0, prevCosts1 = costs1;
                long distortion0 = (long)(coeff0 * coeff0);

                if (level0 == 0)
                {
                    // The common case, a plain level of 0: no distortion change (the error stays the
                    // coefficient), no fixed cost, no end of the best path; and the node one above, 1
                    long a0 = (long)Unsafe.Add(ref levelCosts, prevCosts0) * lambda;
                    long a1 = (long)Unsafe.Add(ref levelCosts, prevCosts1) * lambda;
                    long s0 = prev0 + a0;
                    long diff = (prev1 + a1) - s0;
                    int bestPrev = (int)((ulong)diff >> 63);
                    score0 = s0 + (diff & (diff >> 63));
                    costs0 = bandCosts;
                    Unsafe.Add(ref nodes, n * 2) = signBit | (bestPrev << 17);
                    costs1 = bandCosts + (MaxVariableLevel + 1);
                    if (threshLevel < 1)
                    {
                        score1 = MaxCost;
                        continue;
                    }

                    {
                        int newError = (int)(coeff0 - q);
                        long distortion = RdDistoMult * (long)(int)(weight * ((uint)(newError * newError) - (uint)distortion0));
                        int fixedCost = Unsafe.Add(ref fixedCosts, 1);
                        long b0 = ((long)(fixedCost + Unsafe.Add(ref levelCosts, prevCosts0 + 1)) * lambda) + distortion;
                        long b1 = ((long)(fixedCost + Unsafe.Add(ref levelCosts, prevCosts1 + 1)) * lambda) + distortion;
                        long t0 = prev0 + b0;
                        long d = (prev1 + b1) - t0;
                        int prev = (int)((ulong)d >> 63);
                        long cur = t0 + (d & (d >> 63));
                        Unsafe.Add(ref nodes, (n * 2) + 1) = 1 | signBit | (prev << 17);
                        score1 = cur;
                        if (cur < bestScore)
                        {
                            long lastPosCost = (n < 15) ? BitCost(false, Unsafe.Add(ref probas, typeProbas + ProbaAt(0, band, 1))) : 0;
                            long gain = cur + (lastPosCost * lambda) - bestScore;
                            long better = gain >> 63;
                            bestScore += gain & better;
                            bestPath = (bestPath & ~(int)better) | (((n << 2) | (1 << 1) | prev) & (int)better);
                        }
                    }

                    continue;
                }

                // The node at level0 (always valid: thresh_level >= level0)
                {
                    int level = level0;
                    costs0 = bandCosts + (Min2(level) * (MaxVariableLevel + 1));
                    int newError = (int)(coeff0 - ((uint)level * q));
                    long distortion = RdDistoMult * (long)(int)(weight * ((uint)(newError * newError) - (uint)distortion0));
                    int fixedCost = Unsafe.Add(ref fixedCosts, level);
                    int capped = (level > MaxVariableLevel) ? MaxVariableLevel : level;
                    long a0 = ((long)(fixedCost + Unsafe.Add(ref levelCosts, prevCosts0 + capped)) * lambda) + distortion;
                    long a1 = ((long)(fixedCost + Unsafe.Add(ref levelCosts, prevCosts1 + capped)) * lambda) + distortion;
                    long s0 = prev0 + a0;
                    long diff = (prev1 + a1) - s0;
                    int bestPrev = (int)((ulong)diff >> 63);
                    long cur = s0 + (diff & (diff >> 63));
                    Unsafe.Add(ref nodes, n * 2) = level | signBit | (bestPrev << 17);
                    score0 = cur;
                    if ((level != 0) && (cur < bestScore))
                    {
                        long lastPosCost = (n < 15) ? BitCost(false, Unsafe.Add(ref probas, typeProbas + ProbaAt(0, band, Min2(level)))) : 0;

                        // (the new best, branchless: whether it is is data-dependent)
                        long gain = cur + (lastPosCost * lambda) - bestScore;
                        long better = gain >> 63;
                        bestScore += gain & better;
                        bestPath = (bestPath & ~(int)better) | (((n << 2) | (0 << 1) | bestPrev) & (int)better);
                    }
                }

                // The node one above
                {
                    int level = level0 + 1;
                    costs1 = bandCosts + (Min2(level) * (MaxVariableLevel + 1));
                    if (level > threshLevel)
                    {
                        score1 = MaxCost;
                    }
                    else
                    {
                        int newError = (int)(coeff0 - ((uint)level * q));
                        long distortion = RdDistoMult * (long)(int)(weight * ((uint)(newError * newError) - (uint)distortion0));
                        int fixedCost = Unsafe.Add(ref fixedCosts, level);
                        int capped = (level > MaxVariableLevel) ? MaxVariableLevel : level;
                        long a0 = ((long)(fixedCost + Unsafe.Add(ref levelCosts, prevCosts0 + capped)) * lambda) + distortion;
                        long a1 = ((long)(fixedCost + Unsafe.Add(ref levelCosts, prevCosts1 + capped)) * lambda) + distortion;
                        long s0 = prev0 + a0;
                        long diff = (prev1 + a1) - s0;
                        int bestPrev = (int)((ulong)diff >> 63);
                        long cur = s0 + (diff & (diff >> 63));
                        Unsafe.Add(ref nodes, (n * 2) + 1) = level | signBit | (bestPrev << 17);
                        score1 = cur;
                        if (cur < bestScore)
                        {
                            long lastPosCost = (n < 15) ? BitCost(false, Unsafe.Add(ref probas, typeProbas + ProbaAt(0, band, Min2(level)))) : 0;

                            // (the new best, branchless: whether it is is data-dependent)
                            long gain = cur + (lastPosCost * lambda) - bestScore;
                            long better = gain >> 63;
                            bestScore += gain & better;
                            bestPath = (bestPath & ~(int)better) | (((n << 2) | (1 << 1) | bestPrev) & (int)better);
                        }
                    }
                }
            }

            // in[first..15] and out[first..15] cleared (in[0] and out[0] kept for the I16 AC)
            ref short outRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(output), outAt);
            {
                short in0 = inRef;
                short out0 = outRef;
                Vector128<short>.Zero.StoreUnsafe(ref inRef);
                Vector128<short>.Zero.StoreUnsafe(ref inRef, 8);
                Vector128<short>.Zero.StoreUnsafe(ref outRef);
                Vector128<short>.Zero.StoreUnsafe(ref outRef, 8);
                if (first != 0)
                {
                    inRef = in0;
                    outRef = out0;
                }
            }

            if (bestPath < 0)
            {
                return false;
            }

            {
                int nz = 0;
                int n = bestPath >> 2;
                int bestNode = (bestPath >> 1) & 1;
                ref int top = ref Unsafe.Add(ref nodes, (n * 2) + bestNode);
                top = (top & ~(1 << 17)) | ((bestPath & 1) << 17);
                ref uint positionsRef = ref MemoryMarshal.GetReference(TrellisPositions);
                for (; n >= first; n--)
                {
                    int node = Unsafe.Add(ref nodes, (n * 2) + bestNode);
                    int j = (int)(Unsafe.Add(ref positionsRef, n) & 0xff);
                    int level = node & 0xffff;
                    int negative = -((node >> 16) & 1);
                    int signed = (level ^ negative) - negative;
                    Unsafe.Add(ref outRef, n) = (short)signed;
                    nz |= level;
                    Unsafe.Add(ref inRef, j) = (short)(signed * (int)(uint)(Unsafe.Add(ref matrix, j) & 0xffff));
                    bestNode = node >> 17;
                }

                return nz != 0;
            }
        }

        // min(level, 2) for a level >= 0, branchless (levels 0, 1 and 2 are all common)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Min2(
            int level)
        {
            int over = level - 2;
            return level - (over & ~(over >> 31));
        }
    }
}
