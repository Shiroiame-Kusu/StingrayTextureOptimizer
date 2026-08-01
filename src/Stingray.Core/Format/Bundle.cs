// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Stingray.Core.Format;

/// <summary>
/// A loaded bundle: the whole bundle file held in memory (they are small — the
/// bulk lives in the sibling <c>.gpu_resources</c>) plus its parsed tables.
/// </summary>
public sealed class Bundle
{
    private Bundle(string path, byte[] image, BundleHeader header,
                   IReadOnlyList<BundleTypeEntry> types, IReadOnlyList<BundleFileEntry> files)
    {
        Path = path;
        Image = image;
        Header = header;
        Types = types;
        Files = files;
    }

    /// <summary>Path of the bundle file itself (not the gpu_resources sibling).</summary>
    public string Path { get; }

    /// <summary>Raw bundle bytes. Repacking mutates entry fields in place.</summary>
    public byte[] Image { get; }

    public BundleHeader Header { get; }
    public IReadOnlyList<BundleTypeEntry> Types { get; }
    public IReadOnlyList<BundleFileEntry> Files { get; }

    public string GpuResourcePath => Path + ".gpu_resources";
    public string StreamPath => Path + ".stream";
    public bool HasGpuResources => File.Exists(GpuResourcePath);

    public IEnumerable<BundleFileEntry> Textures => Files.Where(f => f.IsTexture);
    public IEnumerable<BundleFileEntry> GpuBackedFiles => Files.Where(f => f.HasGpuData);
    public IEnumerable<BundleFileEntry> StreamBackedFiles => Files.Where(f => f.HasStreamData);
    public bool HasStreamFile => File.Exists(StreamPath);

    public static Bundle Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var image = File.ReadAllBytes(path);
        return Parse(path, image);
    }

    public static Bundle Parse(string path, byte[] image)
    {
        var header = BundleHeader.Read(image);

        var types = new List<BundleTypeEntry>((int)header.TypeCount);
        var cursor = BundleFormat.HeaderSize;
        for (var i = 0; i < header.TypeCount; i++)
        {
            Require(cursor + BundleFormat.TypeEntrySize <= image.Length, "type table runs past end of file");
            types.Add(BundleTypeEntry.Read(image.AsSpan(cursor)));
            cursor += BundleFormat.TypeEntrySize;
        }

        var files = new List<BundleFileEntry>((int)header.FileCount);
        for (var i = 0UL; i < header.FileCount; i++)
        {
            Require(cursor + BundleFormat.FileEntrySize <= image.Length, "file table runs past end of file");
            files.Add(BundleFileEntry.Read(image.AsSpan(cursor), cursor));
            cursor += BundleFormat.FileEntrySize;
        }

        // The declared type counts must account for every file entry; if they do
        // not, the table stride is wrong and everything downstream is garbage.
        var declared = types.Aggregate(0UL, (acc, t) => acc + t.Count);
        Require(declared == header.FileCount,
            $"type counts sum to {declared} but header declares {header.FileCount} files");

        return new Bundle(path, image, header, types, files);
    }

    /// <summary>CPU-side payload of an entry, as a view over <see cref="Image"/>.</summary>
    public ReadOnlySpan<byte> GetCpuPayload(BundleFileEntry entry) =>
        Image.AsSpan((int)entry.Offset, (int)entry.Size);

    public Span<byte> GetMutableCpuPayload(BundleFileEntry entry) =>
        Image.AsSpan((int)entry.Offset, (int)entry.Size);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException($"Malformed bundle: {message}.");
    }
}
