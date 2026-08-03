// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Stingray.Core.Format;

/// <summary>
/// Random-access reader for a <c>.gpu_resources</c> companion file.
/// </summary>
/// <remarks>
/// These files routinely run to hundreds of megabytes, so payloads are read on
/// demand rather than held in memory.
/// </remarks>
public sealed class GpuResourceFile : IDisposable
{
    private readonly FileStream _stream;

    private GpuResourceFile(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }
    public long Length => _stream.Length;

    public static GpuResourceFile Open(string path)
    {
        // FileShare.Delete lets the file be unlinked while this handle is open,
        // which is what a mod manager or the game moving it out from under a
        // scan needs. It is NOT enough to rename over the file: Windows refuses
        // that with "access denied" while any handle is open, whatever sharing
        // was asked for. Replacing one is <see cref="Close"/>'s job.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.Read | FileShare.Delete,
                                    bufferSize: 1 << 20, FileOptions.RandomAccess);
        return new GpuResourceFile(path, stream);
    }

    /// <summary>
    /// Lets go of the file, which anything replacing it in place has to do
    /// first.
    /// </summary>
    /// <remarks>
    /// Unix renames over an open file happily, so this was invisible until the
    /// tests ran on Windows, where every in-place rewrite failed at the commit.
    /// Idempotent, so a caller's <c>using</c> stays correct after this has
    /// already been called on its behalf.
    /// </remarks>
    public void Close() => _stream.Dispose();

    public byte[] Read(ulong offset, uint size)
    {
        var buffer = new byte[size];
        ReadInto(offset, buffer);
        return buffer;
    }

    public void ReadInto(ulong offset, Span<byte> destination)
    {
        if (offset + (ulong)destination.Length > (ulong)Length)
            throw new InvalidDataException(
                $"Payload at {offset} (+{destination.Length}) runs past the end of {System.IO.Path.GetFileName(Path)}.");

        _stream.Seek((long)offset, SeekOrigin.Begin);
        _stream.ReadExactly(destination);
    }

    /// <summary>
    /// Content hash of a payload, computed by streaming so large meshes never
    /// have to be held in memory.
    /// </summary>
    public byte[] Hash(ulong offset, uint size)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        _stream.Seek((long)offset, SeekOrigin.Begin);

        var remaining = (int)size;
        var buffer = new byte[Math.Min(Math.Max(remaining, 1), 1 << 20)];
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, buffer.Length);
            _stream.ReadExactly(buffer, 0, chunk);
            sha.TransformBlock(buffer, 0, chunk, null, 0);
            remaining -= chunk;
        }

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }

    /// <summary>Streams a payload straight into <paramref name="destination"/>.</summary>
    public void CopyTo(ulong offset, uint size, Stream destination)
    {
        _stream.Seek((long)offset, SeekOrigin.Begin);
        var remaining = (int)size;
        var buffer = new byte[Math.Min(remaining, 1 << 20)];
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, buffer.Length);
            _stream.ReadExactly(buffer, 0, chunk);
            destination.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }

    public void Dispose() => Close();
}
