// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libwebp 1.6.0 (src/dec/vp8_dec.c's frame, segment, filter and partition headers,
// quant_dec.c and tree_dec.c's probabilities), modified by Hot Chai Games LLC: written in C#.
// libwebp carries this notice:
//   Copyright 2010 Google Inc. All Rights Reserved.
//
//   Use of this source code is governed by a BSD-style license that can be found in the COPYING file
//   in the root of the source tree (THIRD-PARTY-NOTICES.md here). An additional intellectual property
//   rights grant can be found in the file PATENTS (also in THIRD-PARTY-NOTICES.md).

using System;
using System.IO;

namespace DotGame.Imaging.WebP;

internal static partial class Vp8Decoder
{
    private sealed partial class Decoding
    {
        // VP8GetHeaders: the frame header, then the first partition's headers
        public void ReadHeaders()
        {
            var buf = this.data;
            if (buf.Length < 10)
            {
                throw new InvalidDataException("The WebP image's VP8 frame header is cut short.");
            }

            int bits = buf[0] | (buf[1] << 8) | (buf[2] << 16);
            int partitionLength = bits >> 5;
            int position = 10;
            if (partitionLength > buf.Length - position)
            {
                throw new InvalidDataException("The WebP image's first VP8 partition is cut short.");
            }

            this.br = BitReader.Create(buf, position, partitionLength);
            position += partitionLength;

            // The color space and clamping, which don't change decoding
            this.br.Get(buf);
            this.br.Get(buf);
            this.ParseSegmentHeader();
            this.ParseFilterHeader();
            this.ParsePartitions(position, buf.Length - position);
            this.ParseQuant();

            // Whether to update the probabilities, which doesn't matter for a key frame
            this.br.Get(buf);
            this.ParseProba();
        }

        private void ParseSegmentHeader()
        {
            var buf = this.data;
            this.useSegment = this.br.Get(buf);
            if (this.useSegment)
            {
                this.updateMap = this.br.Get(buf);
                if (this.br.Get(buf))
                {
                    this.absoluteDelta = this.br.Get(buf);
                    for (int s = 0; s < NumMbSegments; s++)
                    {
                        this.quantizer[s] = (sbyte)(this.br.Get(buf) ? this.br.GetSignedValue(buf, 7) : 0);
                    }

                    for (int s = 0; s < NumMbSegments; s++)
                    {
                        this.filterStrength[s] = (sbyte)(this.br.Get(buf) ? this.br.GetSignedValue(buf, 6) : 0);
                    }
                }

                if (this.updateMap)
                {
                    for (int s = 0; s < 3; s++)
                    {
                        this.segmentProbas[s] = (byte)(this.br.Get(buf) ? this.br.GetValue(buf, 8) : 255u);
                    }
                }
            }
            else
            {
                this.updateMap = false;
            }

            if (this.br.Eof)
            {
                throw new InvalidDataException("The WebP image's VP8 segment header is damaged.");
            }
        }

        private void ParseFilterHeader()
        {
            var buf = this.data;
            this.filterSimple = this.br.Get(buf);
            this.filterLevel = (int)this.br.GetValue(buf, 6);
            this.sharpness = (int)this.br.GetValue(buf, 3);
            this.useLfDelta = this.br.Get(buf);
            if (this.useLfDelta && this.br.Get(buf))
            {
                for (int i = 0; i < NumRefLfDeltas; i++)
                {
                    if (this.br.Get(buf))
                    {
                        this.refLfDelta[i] = this.br.GetSignedValue(buf, 6);
                    }
                }

                for (int i = 0; i < NumModeLfDeltas; i++)
                {
                    if (this.br.Get(buf))
                    {
                        this.modeLfDelta[i] = this.br.GetSignedValue(buf, 6);
                    }
                }
            }

            this.filterType = (this.filterLevel == 0) ? 0 : this.filterSimple ? 1 : 2;
            if (this.br.Eof)
            {
                throw new InvalidDataException("The WebP image's VP8 filter header is damaged.");
            }
        }

        // The token partitions: their sizes (3 bytes each, but for the last) and then their data
        private void ParsePartitions(
            int start,
            int size)
        {
            var buf = this.data;
            this.numPartsMinusOne = (1 << (int)this.br.GetValue(buf, 2)) - 1;
            int lastPart = this.numPartsMinusOne;
            if (size < 3 * lastPart)
            {
                throw new InvalidDataException("The WebP image's VP8 partitions are cut short.");
            }

            int sizes = start;
            int partStart = start + (lastPart * 3);
            int sizeLeft = size - (lastPart * 3);
            for (int p = 0; p < lastPart; p++)
            {
                int partSize = buf[sizes] | (buf[sizes + 1] << 8) | (buf[sizes + 2] << 16);
                if (partSize > sizeLeft)
                {
                    partSize = sizeLeft;
                }

                this.parts[p] = BitReader.Create(buf, partStart, partSize);
                partStart += partSize;
                sizeLeft -= partSize;
                sizes += 3;
            }

            this.parts[lastPart] = BitReader.Create(buf, partStart, sizeLeft);
            if (partStart >= start + size)
            {
                throw new InvalidDataException("The WebP image's VP8 partitions are cut short.");
            }
        }

        // VP8ParseQuant
        private void ParseQuant()
        {
            var buf = this.data;
            int baseQ0 = (int)this.br.GetValue(buf, 7);
            int dqy1Dc = this.br.Get(buf) ? this.br.GetSignedValue(buf, 4) : 0;
            int dqy2Dc = this.br.Get(buf) ? this.br.GetSignedValue(buf, 4) : 0;
            int dqy2Ac = this.br.Get(buf) ? this.br.GetSignedValue(buf, 4) : 0;
            int dquvDc = this.br.Get(buf) ? this.br.GetSignedValue(buf, 4) : 0;
            int dquvAc = this.br.Get(buf) ? this.br.GetSignedValue(buf, 4) : 0;
            for (int i = 0; i < NumMbSegments; i++)
            {
                int q;
                if (this.useSegment)
                {
                    q = this.quantizer[i];
                    if (!this.absoluteDelta)
                    {
                        q += baseQ0;
                    }
                }
                else if (i > 0)
                {
                    this.dqm.AsSpan(0, 6).CopyTo(this.dqm.AsSpan(i * 6));
                    continue;
                }
                else
                {
                    q = baseQ0;
                }

                var m = this.dqm.AsSpan(i * 6, 6);
                m[0] = DcTable[Clip(q + dqy1Dc, 127)];
                m[1] = AcTable[Clip(q, 127)];
                m[2] = DcTable[Clip(q + dqy2Dc, 127)] * 2;
                m[3] = Math.Max((AcTable[Clip(q + dqy2Ac, 127)] * 101581) >> 16, 8);
                m[4] = DcTable[Clip(q + dquvDc, 117)];
                m[5] = AcTable[Clip(q + dquvAc, 127)];
            }
        }

        private static int Clip(
            int v,
            int max)
        {
            return (v < 0) ? 0 : (v > max) ? max : v;
        }

        // VP8ParseProba
        private void ParseProba()
        {
            var buf = this.data;
            var update = CoeffsUpdateProba;
            var defaults = CoeffsProba0;
            for (int i = 0; i < this.probas.Length; i++)
            {
                this.probas[i] = (this.br.GetBit(buf, update[i]) != 0) ? (byte)this.br.GetValue(buf, 8) : defaults[i];
            }

            this.useSkipProba = this.br.Get(buf);
            if (this.useSkipProba)
            {
                this.skipProba = (int)this.br.GetValue(buf, 8);
            }
        }
    }
}
