// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#.
//
// The simple progression, the scans' MCU geometry and restart intervals follow jcparam.c and
// jcmaster.c, which carry these notices:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1998, Thomas G. Lane.
//   Modified 2003-2008 by Guido Vollbeding.
//   Lossless JPEG Modifications: Copyright (C) 1999, Ken Murchison.
//   libjpeg-turbo Modifications:
//   Copyright (C) 2010, 2016, 2018, 2022-2024, D. R. Commander.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.

using System;
using System.Runtime.CompilerServices;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // jpeg_simple_progression
    private Scan[] SimpleProgression()
    {
        var c = this.components;
        if (c.Length == 3)
        {
            return
            [
                new Scan(c, 0, 0, 0, 1),
                new Scan([c[0]], 1, 5, 0, 2),
                new Scan([c[2]], 1, 63, 0, 1),
                new Scan([c[1]], 1, 63, 0, 1),
                new Scan([c[0]], 6, 63, 0, 2),
                new Scan([c[0]], 1, 63, 2, 1),
                new Scan(c, 0, 0, 1, 0),
                new Scan([c[2]], 1, 63, 1, 0),
                new Scan([c[1]], 1, 63, 1, 0),
                new Scan([c[0]], 1, 63, 1, 0),
            ];
        }

        return
        [
            new Scan(c, 0, 0, 0, 1),
            new Scan(c, 1, 5, 0, 2),
            new Scan(c, 6, 63, 0, 2),
            new Scan(c, 1, 63, 2, 1),
            new Scan(c, 0, 0, 1, 0),
            new Scan(c, 1, 63, 1, 0),
        ];
    }

    // per_scan_setup: the scan's MCU geometry and restart interval
    private void StartScan(
        Scan scan)
    {
        if (scan.Components.Length == 1)
        {
            var c = scan.Components[0];
            scan.McusPerRow = c.WidthInBlocks;
            scan.McuRows = c.HeightInBlocks;
            scan.BlocksInMcu = 1;
            scan.BlockComponent = [0];
            scan.BlockOffset = [0];
        }
        else
        {
            scan.McusPerRow = this.mcusPerRow;
            scan.McuRows = this.imcuRows;
            int blocks = 0;
            foreach (var c in scan.Components)
            {
                blocks += c.H * c.V;
            }

            scan.BlocksInMcu = blocks;
            scan.BlockComponent = new int[blocks];
            scan.BlockOffset = new int[blocks];
            int n = 0;
            for (int ci = 0; ci < scan.Components.Length; ci++)
            {
                var c = scan.Components[ci];
                for (int y = 0; y < c.V; y++)
                {
                    for (int x = 0; x < c.H; x++)
                    {
                        scan.BlockComponent[n] = ci;
                        scan.BlockOffset[n] = ((y * c.BlocksPerRow) + x) * 64;
                        n++;
                    }
                }
            }
        }

        if (this.options.RestartRows > 0)
        {
            scan.RestartInterval = (int)Math.Min((long)this.options.RestartRows * scan.McusPerRow, 65535L);
        }
        else
        {
            scan.RestartInterval = this.options.RestartBlocks;
        }
    }

    // The offset of an MCU's first block in a component's coefficients, the MCU at (column, row) of the
    // scan, its rows counted from the first in the array (rowBase MCU rows earlier)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int McuOrigin(
        Scan scan,
        Component c,
        int column,
        int row)
    {
        if (scan.Components.Length == 1)
        {
            return ((row * c.BlocksPerRow) + column) * 64;
        }

        return ((row * c.V * c.BlocksPerRow) + (column * c.H)) * 64;
    }

    /// <summary>A component of the image: its sampling, tables, samples and coefficients.</summary>
    private sealed class Component
    {
        public Component(
            int id,
            int h,
            int v,
            int table)
        {
            this.Id = id;
            this.H = h;
            this.V = v;
            this.QuantTable = table;
            this.DcTable = table;
            this.AcTable = table;
        }

        public int Id { get; }

        public int H { get; }

        public int V { get; }

        public int QuantTable { get; }

        public int DcTable { get; }

        public int AcTable { get; }

        public int WidthInBlocks { get; set; }

        public int HeightInBlocks { get; set; }

        // Blocks in a row of the coefficients: the MCUs' width, dummy blocks included
        public int BlocksPerRow { get; set; }

        // An iMCU row of the component's samples, 8 x V rows of PlaneStride bytes
        public byte[] Plane { get; set; } = [];

        public int PlaneStride { get; set; }

        // The quantized coefficients, natural order, 64 a block: an iMCU row of them, or the whole
        // image's when the scans need them all
        public short[] Coefficients { get; set; } = [];
    }

    /// <summary>A scan: its components, spectral band, approximation and MCU geometry.</summary>
    private sealed class Scan
    {
        public Scan(
            Component[] components,
            int ss,
            int se,
            int ah,
            int al)
        {
            this.Components = components;
            this.Ss = ss;
            this.Se = se;
            this.Ah = ah;
            this.Al = al;
        }

        public Component[] Components { get; }

        public int Ss { get; }

        public int Se { get; }

        public int Ah { get; }

        public int Al { get; }

        public int McusPerRow { get; set; }

        public int McuRows { get; set; }

        public int BlocksInMcu { get; set; }

        // Each block of an MCU: the index of its component in the scan, and its offset from the MCU's
        // first block in that component's coefficients
        public int[] BlockComponent { get; set; } = [];

        public int[] BlockOffset { get; set; } = [];

        public int RestartInterval { get; set; }
    }
}
