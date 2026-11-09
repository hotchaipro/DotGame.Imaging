// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace DotGame.Imaging.Fuzz;

/// <summary>
/// Native memory with an inaccessible page after it. A span of it ends exactly where the page begins,
/// so a decoder that reads or writes past the end of its input or its pixels crashes at once, instead
/// of reading whatever follows the array on the heap.
/// </summary>
internal sealed unsafe class GuardedBuffer
{
    private const int PageSize = 65536;

    private readonly byte* start;
    private readonly int capacity;

    public GuardedBuffer(
        int capacity)
    {
        this.capacity = ((capacity + PageSize - 1) / PageSize) * PageSize;
        nuint total = (nuint)(this.capacity + PageSize);
        if (OperatingSystem.IsWindows())
        {
            this.start = (byte*)VirtualAlloc(null, total, 0x3000, 0x04);
            if ((this.start == null) || !VirtualProtect(this.start + this.capacity, PageSize, 0x01, out _))
            {
                throw new InvalidOperationException("Couldn't allocate guarded memory.");
            }
        }
        else
        {
            // PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON (Linux 0x20, macOS 0x1000)
            int anon = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
            this.start = (byte*)mmap(null, total, 3, 0x02 | anon, -1, 0);
            if ((this.start == (byte*)-1) || (mprotect(this.start + this.capacity, PageSize, 0) != 0))
            {
                throw new InvalidOperationException("Couldn't allocate guarded memory.");
            }
        }
    }

    public int Capacity => this.capacity;

    /// <summary>
    /// The last <paramref name="length"/> bytes before the guard page.
    /// </summary>
    public Span<byte> End(
        int length)
    {
        if ((uint)length > (uint)this.capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return new Span<byte>(this.start + this.capacity - length, length);
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("libc", SetLastError = true)]
    private static extern void* mmap(void* address, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(void* address, nuint length, int prot);
}
