// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
//
// Ported from libjpeg-turbo 3.2.1, modified by Hot Chai Games LLC: written in C#, with the output
// buffered in chunks for an IBufferWriter.
//
// The markers (SOI, JFIF APP0, DQT, SOF0/1/2, DHT, DRI, SOS, EOI) follow jcmarker.c, which carries
// this notice:
//   This file was part of the Independent JPEG Group's software:
//   Copyright (C) 1991-1998, Thomas G. Lane.
//   Modified 2003-2010 by Guido Vollbeding.
//   libjpeg-turbo Modifications:
//   Copyright (C) 2010, 2022-2023, D. R. Commander.
//   For conditions of distribution and use, see the accompanying README.ijg file (in this folder).
//
// This software is based in part on the work of the Independent JPEG Group.

using System;
using System.Buffers;

namespace DotGame.Imaging.Jpeg;

internal sealed partial class JpegEncoder
{
    // Output

    private void Reserve(
        int count)
    {
        if (this.outputPosition + count > this.output.Length)
        {
            this.FlushOutput();
        }
    }

    private void FlushOutput()
    {
        if (this.outputPosition > 0)
        {
            this.writer.Write(this.output.AsSpan(0, this.outputPosition));
            this.outputPosition = 0;
        }
    }

    private void WriteByte(
        int value)
    {
        this.Reserve(1);
        this.output[this.outputPosition++] = (byte)value;
    }

    private void Write2Bytes(
        int value)
    {
        this.Reserve(2);
        this.output[this.outputPosition++] = (byte)(value >> 8);
        this.output[this.outputPosition++] = (byte)value;
    }

    private void WriteMarker(
        int code)
    {
        this.WriteByte(0xFF);
        this.WriteByte(code);
    }

    // Markers (jcmarker.c)

    private void WriteFileHeader()
    {
        this.WriteMarker(0xD8);

        // emit_jfif_app0: JFIF 1.01, no units, 1:1 density, no thumbnail
        this.WriteMarker(0xE0);
        this.Write2Bytes(16);
        this.WriteByte(0x4A);
        this.WriteByte(0x46);
        this.WriteByte(0x49);
        this.WriteByte(0x46);
        this.WriteByte(0);
        this.WriteByte(1);
        this.WriteByte(1);
        this.WriteByte(0);
        this.Write2Bytes(1);
        this.Write2Bytes(1);
        this.WriteByte(0);
        this.WriteByte(0);
    }

    // write_frame_header: the quantization tables, then SOF0 (baseline), SOF1 (16-bit tables) or SOF2
    private void WriteFrameHeader()
    {
        bool precise = false;
        foreach (var c in this.components)
        {
            precise |= this.WriteQuantizationTable(c.QuantTable);
        }

        int code = this.options.Progressive ? 0xC2 : (precise ? 0xC1 : 0xC0);
        this.WriteMarker(code);
        this.Write2Bytes((3 * this.components.Length) + 8);
        this.WriteByte(8);
        this.Write2Bytes(this.height);
        this.Write2Bytes(this.width);
        this.WriteByte(this.components.Length);
        foreach (var c in this.components)
        {
            this.WriteByte(c.Id);
            this.WriteByte((c.H << 4) + c.V);
            this.WriteByte(c.QuantTable);
        }
    }

    // emit_dqt: whether the table has 16-bit values
    private bool WriteQuantizationTable(
        int index)
    {
        var table = this.quantization[index];
        bool precise = false;
        for (int i = 0; i < 64; i++)
        {
            precise |= table[i] > 255;
        }

        if (!this.quantizationSent[index])
        {
            this.WriteMarker(0xDB);
            this.Write2Bytes(precise ? ((64 * 2) + 3) : (64 + 3));
            this.WriteByte(index + (precise ? 0x10 : 0));
            for (int i = 0; i < 64; i++)
            {
                int value = table[NaturalOrder[i]];
                if (precise)
                {
                    this.WriteByte(value >> 8);
                }

                this.WriteByte(value & 0xFF);
            }

            this.quantizationSent[index] = true;
        }

        return precise;
    }

    // write_scan_header: the Huffman tables the scan needs and haven't been sent, DRI if the interval
    // changed, and SOS
    private void WriteScanHeader(
        Scan scan)
    {
        foreach (var c in scan.Components)
        {
            if ((scan.Ss == 0) && (scan.Ah == 0))
            {
                this.WriteHuffmanTable(this.dcTables[c.DcTable], c.DcTable);
            }

            if (scan.Se != 0)
            {
                this.WriteHuffmanTable(this.acTables[c.AcTable], c.AcTable + 0x10);
            }
        }

        if (scan.RestartInterval != this.lastRestartInterval)
        {
            this.WriteMarker(0xDD);
            this.Write2Bytes(4);
            this.Write2Bytes(scan.RestartInterval);
            this.lastRestartInterval = scan.RestartInterval;
        }

        this.WriteMarker(0xDA);
        this.Write2Bytes((2 * scan.Components.Length) + 6);
        this.WriteByte(scan.Components.Length);
        foreach (var c in scan.Components)
        {
            this.WriteByte(c.Id);
            int td = ((scan.Ss == 0) && (scan.Ah == 0)) ? c.DcTable : 0;
            int ta = (scan.Se != 0) ? c.AcTable : 0;
            this.WriteByte((td << 4) + ta);
        }

        this.WriteByte(scan.Ss);
        this.WriteByte(scan.Se);
        this.WriteByte((scan.Ah << 4) + scan.Al);
    }

    // emit_dht
    private void WriteHuffmanTable(
        HuffmanTable table,
        int index)
    {
        if (table.Sent)
        {
            return;
        }

        int length = 0;
        for (int i = 1; i <= 16; i++)
        {
            length += table.Bits[i];
        }

        this.WriteMarker(0xC4);
        this.Write2Bytes(length + 19);
        this.WriteByte(index);
        for (int i = 1; i <= 16; i++)
        {
            this.WriteByte(table.Bits[i]);
        }

        for (int i = 0; i < length; i++)
        {
            this.WriteByte(table.Values[i]);
        }

        table.Sent = true;
    }
}
