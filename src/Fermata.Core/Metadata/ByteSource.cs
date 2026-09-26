using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Fermata.Metadata;

/// <summary>Random-access bytes for tag parsing.</summary>
/// <remarks>
/// A source is a file, or an in-memory copy of a tag that had to be decoded first, such as an
/// unsynchronized ID3v2 tag.
/// </remarks>
internal abstract class ByteSource
{
    public abstract long Length { get; }

    /// <summary>Reads up to <c>buffer.Length</c> bytes at <paramref name="offset"/>; returns the count read.</summary>
    public abstract int Read(long offset, Span<byte> buffer);

    public void ReadExactly(long offset, Span<byte> buffer)
    {
        if (Read(offset, buffer) != buffer.Length)
            throw new InvalidDataException("Unexpected end of file.");
    }

    /// <summary>Reads <paramref name="length"/> bytes into a new array, refusing absurd sizes from corrupt headers.</summary>
    public byte[] ReadArray(long offset, int length, int limit = TagLimits.MaxFieldBytes)
    {
        if (length < 0 || length > limit || offset < 0 || offset + length > Length)
            throw new InvalidDataException("Field length is out of range.");
        var data = new byte[length];
        ReadExactly(offset, data);
        return data;
    }

    public uint ReadUInt32BigEndian(long offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExactly(offset, bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>True if the bytes at <paramref name="offset"/> equal <paramref name="expected"/>.</summary>
    public bool Matches(long offset, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[expected.Length];
        return Read(offset, actual) == expected.Length && actual.SequenceEqual(expected);
    }
}

/// <summary>A file read with positioned reads.</summary>
/// <remarks>Small reads are served from a window, and large reads bypass it.</remarks>
internal sealed class FileByteSource : ByteSource, IDisposable
{
    private const int WindowSize = 16 * 1024;
    private readonly SafeFileHandle handle;
    private byte[]? window;
    private long windowOffset = -1;
    private int windowLength;

    public FileByteSource(string path)
    {
        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Length = RandomAccess.GetLength(handle);
    }

    public override long Length { get; }

    public override int Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length || buffer.IsEmpty)
            return 0;
        if (buffer.Length > WindowSize / 4)
            return ReadFully(offset, buffer);
        if (window is null || offset < windowOffset || offset + buffer.Length > windowOffset + windowLength)
        {
            window ??= ArrayPool<byte>.Shared.Rent(WindowSize);
            windowOffset = offset;
            windowLength = ReadFully(offset, window.AsSpan(0, WindowSize));
        }
        int available = (int)Math.Min(buffer.Length, windowOffset + windowLength - offset);
        window.AsSpan((int)(offset - windowOffset), available).CopyTo(buffer);
        return available;
    }

    private int ReadFully(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    public void Dispose()
    {
        handle.Dispose();
        if (window is not null)
        {
            ArrayPool<byte>.Shared.Return(window);
            window = null;
        }
    }
}

internal sealed class MemoryByteSource(ReadOnlyMemory<byte> data) : ByteSource
{
    public override long Length => data.Length;

    public override int Read(long offset, Span<byte> buffer)
    {
        if (offset >= data.Length)
            return 0;
        int count = (int)Math.Min(buffer.Length, data.Length - offset);
        data.Span.Slice((int)offset, count).CopyTo(buffer);
        return count;
    }
}

internal static class TagLimits
{
    /// <summary>The largest single text or picture field accepted from a file.</summary>
    public const int MaxFieldBytes = 64 * 1024 * 1024;

    /// <summary>Largest text field read eagerly; longer ones are skipped unless they are lyrics or pictures.</summary>
    public const int MaxTextBytes = 256 * 1024;
}
