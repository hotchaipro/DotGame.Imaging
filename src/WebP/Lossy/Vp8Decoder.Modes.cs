// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/tree_dec.c's ParseIntraMode and VP8ParseIntraModeRow), modified by
// Hot Chai Games LLC: written in C#. libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.WebP;

// A row's modes from the first partition, with the boolean decoder's state in locals as in GetCoeffs
internal static partial class Vp8Decoder
{
    private sealed partial class Decoding
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void ParseIntraModeRow()
        {
            var buf = this.data;
            ref var br = ref this.br;
            ref byte start = ref MemoryMarshal.GetArrayDataReference(buf);
            ref byte tree = ref Unsafe.As<sbyte, byte>(ref MemoryMarshal.GetReference(YModesIntra4));
            ref byte bmodes = ref MemoryMarshal.GetReference(BModesProba);
            ref byte left = ref MemoryMarshal.GetArrayDataReference(this.intraL);
            ref byte topBase = ref MemoryMarshal.GetArrayDataReference(this.intraT);
            ref byte modesBase = ref MemoryMarshal.GetArrayDataReference(this.imodes);
            ulong value = br.Value;
            uint range = br.Range;
            int bits = br.Bits;
            int position = br.Position;
            bool updateMap = this.updateMap;
            bool useSkipProba = this.useSkipProba;
            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                ref byte top = ref Unsafe.Add(ref topBase, 4 * mbX);
                if (updateMap)
                {
                    this.segment[mbX] = (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, this.segmentProbas[0]) == 0)
                        ? (byte)GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, this.segmentProbas[1])
                        : (byte)(GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, this.segmentProbas[2]) + 2);
                }
                else
                {
                    this.segment[mbX] = 0;
                }

                if (useSkipProba)
                {
                    this.skip[mbX] = GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, this.skipProba) != 0;
                }

                bool i4x4 = GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 145) == 0;
                this.isI4x4[mbX] = i4x4;
                ref byte modes = ref Unsafe.Add(ref modesBase, mbX * 16);
                if (!i4x4)
                {
                    int ymode = (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 156) != 0)
                        ? ((GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 128) != 0) ? TmPred : HPred)
                        : ((GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 163) != 0) ? VPred : DcPred);
                    modes = (byte)ymode;
                    uint four = 0x01010101u * (uint)ymode;
                    Unsafe.WriteUnaligned(ref top, four);
                    Unsafe.WriteUnaligned(ref left, four);
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                    {
                        int ymode = Unsafe.Add(ref left, y);
                        for (int x = 0; x < 4; x++)
                        {
                            ref byte prob = ref Unsafe.Add(ref bmodes, ((Unsafe.Add(ref top, x) * 10) + ymode) * 9);
                            int i = (sbyte)Unsafe.Add(ref tree, GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, prob));
                            while (i > 0)
                            {
                                i = (sbyte)Unsafe.Add(ref tree, (2 * i) + GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, Unsafe.Add(ref prob, i)));
                            }

                            ymode = -i;
                            Unsafe.Add(ref top, x) = (byte)ymode;
                        }

                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref modes, y * 4), Unsafe.ReadUnaligned<uint>(ref top));
                        Unsafe.Add(ref left, y) = (byte)ymode;
                    }
                }

                this.uvMode[mbX] = (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 142) == 0) ? (byte)DcPred
                    : (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 114) == 0) ? (byte)VPred
                    : (GetBit(ref br, buf, ref start, ref value, ref range, ref bits, ref position, 183) != 0) ? (byte)TmPred : (byte)HPred;
            }

            br.Value = value;
            br.Range = range;
            br.Bits = bits;
            br.Position = position;
        }
    }
}
