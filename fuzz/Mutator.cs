// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DotGame.Imaging.Fuzz;

/// <summary>
/// Makes new inputs from old ones: AFL's havoc (bit flips, interesting values, arithmetic, blocks
/// deleted, copied and inserted, truncation, splicing two inputs), and mutations that know each
/// format's structure, so a change gets past the checks that would otherwise reject it at once: a PNG's
/// chunk CRCs and zlib stream, a WebP file's RIFF and chunk sizes, a JPEG file's markers and segments.
/// </summary>
internal sealed class Mutator
{
    private static readonly int[] Interesting8 = [0, 1, 2, 3, 4, 7, 8, 15, 16, 31, 32, 63, 64, 100, 127, 128, 129, 192, 254, 255];
    private static readonly int[] Interesting16 = [0, 1, 2, 8, 16, 32, 64, 128, 255, 256, 512, 1000, 1024, 4096, 8191, 8192, 8193, 16383, 16384, 32767, 32768, 65535];
    private static readonly long[] Interesting32 = [0, 1, 2, 16, 255, 256, 65535, 65536, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF, 0x0100_0000, 8192, 8193, 16384, 1 << 20];

    private readonly Random random;
    private readonly int maxLength;

    public Mutator(
        int seed,
        int maxLength)
    {
        this.random = new Random(seed);
        this.maxLength = maxLength;
    }

    public byte[] Mutate(
        byte[] parent,
        IReadOnlyList<byte[]> corpus)
    {
        List<byte> data = [.. parent];
        Format kind = Detect(parent);
        int rounds = 1 << this.random.Next(1, 5);
        for (int i = 0; i < rounds; i++)
        {
            if ((this.random.Next(4) == 0) && (kind != Format.Any))
            {
                this.Structured(data, kind, corpus);
            }
            else
            {
                this.Havoc(data, corpus);
            }

            if (data.Count > this.maxLength)
            {
                data.RemoveRange(this.maxLength, data.Count - this.maxLength);
            }
        }

        byte[] result = [.. data];

        // Most of the time, make the sizes and checksums right again, so the change gets as far as
        // the code that reads what was changed
        if (this.random.Next(10) != 0)
        {
            if (kind == Format.Png)
            {
                result = FixPng(result);
            }
            else if (kind == Format.WebP)
            {
                FixRiff(result);
            }
        }

        return result;
    }

    private static Format Detect(
        byte[] data)
    {
        if ((data.Length >= 8) && (data[0] == 0x89) && (data[1] == (byte)'P'))
        {
            return Format.Png;
        }

        if ((data.Length >= 2) && (data[0] == 0xFF) && (data[1] == 0xD8))
        {
            return Format.Jpeg;
        }

        if ((data.Length >= 12) && (data[0] == (byte)'R') && (data[8] == (byte)'W'))
        {
            return Format.WebP;
        }

        return Format.Any;
    }

    private void Havoc(
        List<byte> data,
        IReadOnlyList<byte[]> corpus)
    {
        int count = data.Count;
        switch (this.random.Next(16))
        {
            case 0 when count > 0:
                data[this.random.Next(count)] ^= (byte)(1 << this.random.Next(8));
                break;
            case 1 when count > 0:
                data[this.random.Next(count)] = (byte)Interesting8[this.random.Next(Interesting8.Length)];
                break;
            case 2 when count > 1:
                WriteInt(data, this.random.Next(count - 1), 2, Interesting16[this.random.Next(Interesting16.Length)]);
                break;
            case 3 when count > 3:
                WriteInt(data, this.random.Next(count - 3), 4, Interesting32[this.random.Next(Interesting32.Length)]);
                break;
            case 4 when count > 0:
                {
                    int at = this.random.Next(count);
                    data[at] = (byte)(data[at] + this.random.Next(-35, 36));
                    break;
                }

            case 5 when count > 1:
                {
                    int at = this.random.Next(count - 1);
                    int value = ((data[at] << 8) | data[at + 1]) + this.random.Next(-35, 36);
                    WriteInt(data, at, 2, value);
                    break;
                }

            case 6 when count > 0:
                data[this.random.Next(count)] = (byte)this.random.Next(256);
                break;
            case 7 when count > 1:
                {
                    // Delete a block
                    int length = this.BlockLength(count);
                    data.RemoveRange(this.random.Next(count - length + 1), length);
                    break;
                }

            case 8 when count > 0:
                {
                    // Copy a block over another
                    int length = this.BlockLength(count);
                    int from = this.random.Next(count - length + 1);
                    int to = this.random.Next(count - length + 1);
                    byte[] block = data.GetRange(from, length).ToArray();
                    for (int i = 0; i < length; i++)
                    {
                        data[to + i] = block[i];
                    }

                    break;
                }

            case 9 when count > 0:
                {
                    // Insert a copy of a block
                    int length = this.BlockLength(count);
                    int from = this.random.Next(count - length + 1);
                    data.InsertRange(this.random.Next(count + 1), data.GetRange(from, length));
                    break;
                }

            case 10:
                {
                    // Insert random or repeated bytes
                    int length = this.random.Next(1, 33);
                    var block = new byte[length];
                    if (this.random.Next(2) == 0)
                    {
                        this.random.NextBytes(block);
                    }
                    else
                    {
                        Array.Fill(block, (byte)Interesting8[this.random.Next(Interesting8.Length)]);
                    }

                    data.InsertRange(this.random.Next(count + 1), block);
                    break;
                }

            case 11 when count > 1:
                {
                    // Truncate
                    int keep = this.random.Next(1, count);
                    data.RemoveRange(keep, count - keep);
                    break;
                }
            case 12 when (count > 1) && (corpus.Count > 0):
                {
                    // Splice: this input's start, another's end
                    byte[] other = corpus[this.random.Next(corpus.Count)];
                    int at = this.random.Next(count);
                    int from = this.random.Next(other.Length + 1);
                    data.RemoveRange(at, count - at);
                    data.AddRange(other.AsSpan(from).ToArray());
                    break;
                }

            case 13 when count > 8:
                {
                    // Flip several bits near each other
                    int at = this.random.Next(count - 8);
                    for (int i = 0; i < 8; i++)
                    {
                        if (this.random.Next(3) == 0)
                        {
                            data[at + i] ^= (byte)(1 << this.random.Next(8));
                        }
                    }

                    break;
                }

            case 14 when count > 0:
                {
                    // Zero or saturate a block
                    int length = this.BlockLength(count);
                    int at = this.random.Next(count - length + 1);
                    byte value = (this.random.Next(2) == 0) ? (byte)0 : (byte)0xFF;
                    for (int i = 0; i < length; i++)
                    {
                        data[at + i] = value;
                    }

                    break;
                }

            default:
                if (count > 0)
                {
                    data[this.random.Next(count)] = (byte)this.random.Next(256);
                }

                break;
        }
    }

    private int BlockLength(
        int count)
    {
        int max = Math.Min(count, this.random.Next(4) == 0 ? 1024 : 32);
        return this.random.Next(1, max + 1);
    }

    private static void WriteInt(
        List<byte> data,
        int at,
        int size,
        long value)
    {
        bool bigEndian = (value & 1) == 0;
        for (int i = 0; i < size; i++)
        {
            int shift = bigEndian ? (8 * (size - 1 - i)) : (8 * i);
            data[at + i] = (byte)(value >> shift);
        }
    }

    private void Structured(
        List<byte> data,
        Format kind,
        IReadOnlyList<byte[]> corpus)
    {
        switch (kind)
        {
            case Format.Png:
                this.MutatePng(data);
                break;
            case Format.Jpeg:
                this.MutateJpeg(data, corpus);
                break;
            default:
                this.MutateWebP(data);
                break;
        }
    }

    // PNG ------------------------------------------------------------------------------------------

    private sealed class Chunk
    {
        public string Type = "";
        public byte[] Data = [];
    }

    private static List<Chunk>? ParsePng(
        List<byte> data)
    {
        if (data.Count < 8)
        {
            return null;
        }

        byte[] bytes = [.. data];
        var chunks = new List<Chunk>();
        int at = 8;
        while (at + 12 <= bytes.Length)
        {
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at)), (uint)(bytes.Length - at - 12));
            chunks.Add(new Chunk
            {
                Type = Encoding.Latin1.GetString(bytes, at + 4, 4),
                Data = bytes.AsSpan(at + 8, length).ToArray(),
            });
            at += 12 + length;
        }

        return chunks;
    }

    private static byte[] WritePng(
        byte[] signature,
        List<Chunk> chunks,
        bool fixCrcs)
    {
        var output = new MemoryStream();
        output.Write(signature);
        Span<byte> four = stackalloc byte[4];
        foreach (Chunk chunk in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(four, (uint)chunk.Data.Length);
            output.Write(four);
            byte[] type = Encoding.Latin1.GetBytes(chunk.Type.PadRight(4)[..4]);
            output.Write(type);
            output.Write(chunk.Data);
            BinaryPrimitives.WriteUInt32BigEndian(four, fixCrcs ? Crc32(type, chunk.Data) : 0);
            output.Write(four);
        }

        return output.ToArray();
    }

    private static byte[] FixPng(
        byte[] data)
    {
        // Rewrites each chunk's CRC (and a length that runs past the end)
        List<Chunk>? chunks = ParsePng([.. data]);
        if ((chunks == null) || (chunks.Count == 0))
        {
            return data;
        }

        return WritePng(data[..8], chunks, true);
    }

    private void MutatePng(
        List<byte> data)
    {
        List<Chunk>? chunks = ParsePng(data);
        if ((chunks == null) || (chunks.Count == 0))
        {
            return;
        }

        byte[] signature = data.GetRange(0, 8).ToArray();
        switch (this.random.Next(8))
        {
            case 0:
            case 1:
            case 2:
                this.MutatePixelData(chunks);
                break;
            case 3:
                {
                    // An IHDR field: width, height, bit depth, color type, compression, filter, interlace
                    Chunk? header = chunks.Find(c => c.Type == "IHDR");
                    if ((header != null) && (header.Data.Length >= 13))
                    {
                        int field = this.random.Next(7);
                        if (field < 2)
                        {
                            BinaryPrimitives.WriteUInt32BigEndian(header.Data.AsSpan(field * 4), (uint)Interesting32[this.random.Next(Interesting32.Length)] % ((this.random.Next(2) == 0) ? 64u : uint.MaxValue));
                        }
                        else
                        {
                            byte[] values = field switch
                            {
                                2 => [1, 2, 4, 8, 16, 0, 3, 32],
                                3 => [0, 2, 3, 4, 6, 1, 5, 7],
                                _ => [0, 1, 2],
                            };
                            header.Data[6 + field] = values[this.random.Next(values.Length)];
                        }
                    }

                    break;
                }

            case 4:
                {
                    // Delete, duplicate or move a chunk
                    int at = this.random.Next(chunks.Count);
                    Chunk chunk = chunks[at];
                    int op = this.random.Next(3);
                    if (op == 0)
                    {
                        chunks.RemoveAt(at);
                    }
                    else if (op == 1)
                    {
                        chunks.Insert(this.random.Next(chunks.Count + 1), new Chunk { Type = chunk.Type, Data = (byte[])chunk.Data.Clone() });
                    }
                    else
                    {
                        chunks.RemoveAt(at);
                        chunks.Insert(this.random.Next(chunks.Count + 1), chunk);
                    }

                    break;
                }

            case 5:
                {
                    // Add a palette or transparency chunk, sized oddly
                    string[] types = ["PLTE", "tRNS", "IDAT", "IEND", "bKGD", "gAMA", "sRGB", "acTL", "fdAT", "zzZZ", "ZZZZ"];
                    int length = this.random.Next(4) switch
                    {
                        0 => 0,
                        1 => this.random.Next(1, 8),
                        2 => 3 * this.random.Next(1, 300),
                        _ => this.random.Next(1, 800),
                    };
                    var bytes = new byte[length];
                    this.random.NextBytes(bytes);
                    chunks.Insert(this.random.Next(1, chunks.Count + 1), new Chunk { Type = types[this.random.Next(types.Length)], Data = bytes });
                    break;
                }

            case 6:
                {
                    // Split the pixel data into more IDAT chunks
                    int at = chunks.FindIndex(c => c.Type == "IDAT");
                    if ((at >= 0) && (chunks[at].Data.Length > 1))
                    {
                        byte[] whole = chunks[at].Data;
                        int split = this.random.Next(1, whole.Length);
                        chunks[at].Data = whole[..split];
                        chunks.Insert(at + 1, new Chunk { Type = "IDAT", Data = whole[split..] });
                    }

                    break;
                }

            default:
                {
                    // Havoc inside one chunk's data
                    Chunk chunk = chunks[this.random.Next(chunks.Count)];
                    List<byte> bytes = [.. chunk.Data];
                    this.Havoc(bytes, []);
                    chunk.Data = [.. bytes];
                    break;
                }
        }

        data.Clear();
        data.AddRange(WritePng(signature, chunks, true));
    }

    private void MutatePixelData(
        List<Chunk> chunks)
    {
        // Inflates the pixel data, changes it (filter types, rows cut short or too long, random
        // bytes), and deflates it again, so the change reaches the filters and the row readers
        var compressed = new MemoryStream();
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Type == "IDAT")
            {
                compressed.Write(chunk.Data);
            }
        }

        List<byte> raw;
        try
        {
            compressed.Position = 0;
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var inflated = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while (((read = zlib.Read(buffer)) > 0) && (inflated.Length < (32 << 20)))
            {
                inflated.Write(buffer, 0, read);
            }

            raw = [.. inflated.ToArray()];
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // Data zlib can't inflate (ZLibException is an IOException)
            raw = [];
        }

        int rounds = this.random.Next(1, 5);
        for (int i = 0; i < rounds; i++)
        {
            int op = this.random.Next(4);
            if ((op == 0) && (raw.Count > 0))
            {
                // A filter type byte, or what's there, set to a filter type or not one
                raw[this.random.Next(raw.Count)] = (byte)this.random.Next(7);
            }
            else if (op == 1)
            {
                // More bytes than the image needs, or fewer
                if ((this.random.Next(2) == 0) && (raw.Count > 1))
                {
                    // Cut off the end, by a little or a lot
                    int keep = (this.random.Next(2) == 0) ? Math.Max(0, raw.Count - this.random.Next(1, 64)) : this.random.Next(raw.Count);
                    raw.RemoveRange(keep, raw.Count - keep);
                }
                else
                {
                    var extra = new byte[this.random.Next(1, 4096)];
                    this.random.NextBytes(extra);
                    raw.AddRange(extra);
                }
            }
            else
            {
                this.Havoc(raw, []);
            }
        }

        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, this.random.Next(3) switch
        { 0 => CompressionLevel.NoCompression, 1 => CompressionLevel.Fastest, _ => CompressionLevel.Optimal }, true))
        {
            zlib.Write([.. raw]);
        }

        byte[] deflated = output.ToArray();
        int first = chunks.FindIndex(c => c.Type == "IDAT");
        chunks.RemoveAll(c => c.Type == "IDAT");
        if (first < 0)
        {
            first = Math.Min(1, chunks.Count);
        }

        chunks.Insert(Math.Min(first, chunks.Count), new Chunk { Type = "IDAT", Data = deflated });
    }

    private static uint Crc32(
        byte[] type,
        byte[] data)
    {
        uint crc = 0xFFFF_FFFF;
        crc = Crc32Update(crc, type);
        crc = Crc32Update(crc, data);
        return ~crc;
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = ((c & 1) != 0) ? (0xEDB8_8320 ^ (c >> 1)) : (c >> 1);
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32Update(
        uint crc,
        byte[] data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    // WebP -----------------------------------------------------------------------------------------

    private static void FixRiff(
        byte[] data)
    {
        // The RIFF size, and each chunk's size where it runs past the end
        if (data.Length < 12)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        int at = 12;
        while (at + 8 <= data.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            if (size > (uint)(data.Length - at - 8))
            {
                size = (uint)(data.Length - at - 8);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 4), size);
            }

            at += 8 + (int)size + (int)(size & 1);
        }
    }

    private void MutateWebP(
        List<byte> data)
    {
        // The chunks: [type, offset of data, size]
        var chunks = new List<(string Type, int At, int Size)>();
        int at = 12;
        while (at + 8 <= data.Count)
        {
            string type = Encoding.Latin1.GetString([data[at], data[at + 1], data[at + 2], data[at + 3]]);
            int size = (int)Math.Min((uint)(data[at + 4] | (data[at + 5] << 8) | (data[at + 6] << 16) | (data[at + 7] << 24)), (uint)(data.Count - at - 8));
            chunks.Add((type, at + 8, size));
            at += 8 + size + (size & 1);
        }

        if (chunks.Count == 0)
        {
            return;
        }

        var (chunkType, start, length) = chunks[this.random.Next(chunks.Count)];
        switch (this.random.Next(5))
        {
            case 0:
                {
                    // A header field: VP8X's flags and canvas, VP8L's size and version, VP8's frame
                    // header and size, ALPH's method, filter and preprocessing
                    int field = chunkType switch
                    {
                        "VP8X" => this.random.Next(Math.Min(10, length)),
                        "VP8L" => this.random.Next(Math.Min(5, length)),
                        "VP8 " => this.random.Next(Math.Min(10, length)),
                        "ALPH" => 0,
                        _ => this.random.Next(Math.Max(1, Math.Min(8, length))),
                    };
                    if (start + field < data.Count)
                    {
                        data[start + field] = (this.random.Next(2) == 0) ? (byte)Interesting8[this.random.Next(Interesting8.Length)] : (byte)(data[start + field] ^ (1 << this.random.Next(8)));
                    }

                    break;
                }

            case 1:
                {
                    // Delete or duplicate a chunk
                    int end = Math.Min(data.Count, start + length + (length & 1));
                    List<byte> chunk = data.GetRange(start - 8, end - start + 8);
                    if (this.random.Next(2) == 0)
                    {
                        data.RemoveRange(start - 8, chunk.Count);
                    }
                    else
                    {
                        data.InsertRange(end, chunk);
                    }

                    break;
                }

            case 2:
                {
                    // Add a chunk
                    string[] types = ["VP8X", "ALPH", "VP8 ", "VP8L", "ANIM", "ANMF", "ICCP", "EXIF"];
                    var payload = new byte[this.random.Next(0, 64)];
                    this.random.NextBytes(payload);
                    var chunk = new List<byte>(Encoding.Latin1.GetBytes(types[this.random.Next(types.Length)]))
                    {
                        (byte)payload.Length, 0, 0, 0,
                    };
                    chunk.AddRange(payload);
                    if ((payload.Length & 1) != 0)
                    {
                        chunk.Add(0);
                    }

                    data.InsertRange(12, chunk);
                    break;
                }

            default:
                {
                    // Havoc inside one chunk's data
                    if (length > 0)
                    {
                        List<byte> bytes = data.GetRange(start, length);
                        this.Havoc(bytes, []);
                        data.RemoveRange(start, length);
                        data.InsertRange(start, bytes);
                        int size = bytes.Count;
                        data[start - 4] = (byte)size;
                        data[start - 3] = (byte)(size >> 8);
                        data[start - 2] = (byte)(size >> 16);
                        data[start - 1] = (byte)(size >> 24);
                        if (((size & 1) != 0) != ((length & 1) != 0))
                        {
                            // Keep the padding byte right
                            if ((size & 1) != 0)
                            {
                                data.Insert(start + size, 0);
                            }
                            else if (start + size < data.Count)
                            {
                                data.RemoveAt(start + size);
                            }
                        }
                    }

                    break;
                }
        }
    }

    // JPEG -----------------------------------------------------------------------------------------

    private void MutateJpeg(
        List<byte> data,
        IReadOnlyList<byte[]> corpus)
    {
        // The markers' offsets, and where each segment's data starts and ends
        var segments = new List<(byte Marker, int At, int End)>();
        for (int i = 2; i + 3 < data.Count; i++)
        {
            if ((data[i] == 0xFF) && (data[i + 1] != 0x00) && (data[i + 1] != 0xFF))
            {
                byte marker = data[i + 1];
                bool hasLength = (marker < 0xD0) || (marker > 0xD9);
                int end = hasLength ? Math.Min(data.Count, i + 2 + ((data[i + 2] << 8) | data[i + 3])) : i + 2;
                segments.Add((marker, i, end));
                if (hasLength && (marker != 0xDA))
                {
                    i = Math.Max(i, end - 1);
                }
            }
        }

        if (segments.Count == 0)
        {
            return;
        }

        var (kind, at, segmentEnd) = segments[this.random.Next(segments.Count)];
        switch (this.random.Next(7))
        {
            case 0:
            case 1:
                {
                    // A field of a segment's header: SOF's precision, size, components and their
                    // sampling and tables; SOS's components, tables and spectral selection; DHT's
                    // counts; DQT's precision; DRI's interval
                    int length = segmentEnd - at - 4;
                    if (length > 0)
                    {
                        int field = at + 4 + this.random.Next(Math.Min(length, 24));
                        int op = this.random.Next(3);
                        data[field] = op switch
                        {
                            0 => (byte)Interesting8[this.random.Next(Interesting8.Length)],
                            1 => (byte)((this.random.Next(5) << 4) | this.random.Next(5)),
                            _ => (byte)(data[field] + this.random.Next(-3, 4)),
                        };
                    }

                    break;
                }

            case 2:
                {
                    // Delete a segment
                    data.RemoveRange(at, Math.Max(2, segmentEnd - at));
                    break;
                }

            case 3:
                {
                    // Duplicate a segment, here or somewhere else
                    List<byte> segment = data.GetRange(at, Math.Max(2, segmentEnd - at));
                    data.InsertRange(this.random.Next(2) == 0 ? segmentEnd : segments[this.random.Next(segments.Count)].At, segment);
                    break;
                }

            case 4:
                {
                    // A marker in the entropy-coded data: a restart (in or out of order), EOI, or another
                    int scan = segments.FindIndex(s => s.Marker == 0xDA);
                    if (scan >= 0)
                    {
                        int from = segments[scan].End;
                        int position = from + this.random.Next(Math.Max(1, data.Count - from));
                        byte marker = this.random.Next(4) switch
                        {
                            0 => 0xD9,
                            1 => (byte)this.random.Next(0xC0, 0xFF),
                            _ => (byte)(0xD0 + this.random.Next(8)),
                        };
                        data.InsertRange(Math.Min(position, data.Count), [0xFF, marker]);
                    }

                    break;
                }

            case 5:
                {
                    // Change a segment's length
                    if ((kind < 0xD0) || (kind > 0xD9))
                    {
                        int length = (data[at + 2] << 8) | data[at + 3];
                        length = this.random.Next(3) switch
                        {
                            0 => this.random.Next(0, 4),
                            1 => length + this.random.Next(-8, 9),
                            _ => Interesting16[this.random.Next(Interesting16.Length)],
                        };
                        data[at + 2] = (byte)(length >> 8);
                        data[at + 3] = (byte)length;
                    }

                    break;
                }

            default:
                {
                    // Another file's segment of the same kind, or any kind
                    byte[] other = corpus.Count > 0 ? corpus[this.random.Next(corpus.Count)] : [];
                    for (int i = 2; i + 3 < other.Length; i++)
                    {
                        if ((other[i] == 0xFF) && (other[i + 1] is >= 0xC0 and <= 0xFE) && ((other[i + 1] < 0xD0) || (other[i + 1] > 0xD9)) && (this.random.Next(4) == 0))
                        {
                            int end = Math.Min(other.Length, i + 2 + ((other[i + 2] << 8) | other[i + 3]));
                            data.InsertRange(at, other.AsSpan(i, end - i).ToArray());
                            break;
                        }
                    }

                    break;
                }
        }
    }
}
