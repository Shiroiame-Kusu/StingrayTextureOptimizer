// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace Stingray.Core.Format;

/// <summary>
/// On-disk layout constants for Stingray/Bitsquid asset bundles, as shipped by
/// Helldivers 2, Darktide and Vermintide 2 (<c>&lt;hash&gt;</c>,
/// <c>&lt;hash&gt;.patch_N</c> and their <c>.gpu_resources</c> / <c>.stream</c>
/// siblings).
/// </summary>
/// <remarks>
/// Layout was recovered by inspection; see <c>docs/bundle-format.md</c>. Fields
/// named <c>UnknownNN</c> are round-tripped verbatim rather than recomputed,
/// because nothing observed so far derives them from bundle contents.
/// </remarks>
public static class BundleFormat
{
    public const uint Magic = 0xF000_0011;

    public const int HeaderSize = 0x48;
    public const int TypeEntrySize = 32;
    public const int FileEntrySize = 80;

    /// <summary>
    /// Byte alignment observed for every payload inside a <c>.gpu_resources</c>
    /// file. Writing at a coarser alignment is harmless; a finer one is not.
    /// </summary>
    public const int GpuAlignment = 64;
}

/// <summary>Fixed 0x48-byte bundle header.</summary>
public sealed class BundleHeader
{
    public uint Magic { get; init; }
    public uint TypeCount { get; init; }
    public ulong FileCount { get; init; }

    public uint Unknown10 { get; init; }
    public uint Unknown14 { get; init; }
    public ulong Unknown18 { get; init; }

    /// <summary>
    /// Opaque size-like field at 0x20. Matches no sum of bundle contents in any
    /// sample examined, so it is preserved rather than recalculated.
    /// </summary>
    public ulong UnknownSize20 { get; init; }

    /// <summary>Opaque size-like field at 0x28. See <see cref="UnknownSize20"/>.</summary>
    public ulong UnknownSize28 { get; init; }

    public static BundleHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < BundleFormat.HeaderSize)
            throw new InvalidDataException("File is smaller than a bundle header.");

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != BundleFormat.Magic)
            throw new InvalidDataException(
                $"Not a Stingray bundle: magic 0x{magic:X8}, expected 0x{BundleFormat.Magic:X8}.");

        return new BundleHeader
        {
            Magic = magic,
            TypeCount = BinaryPrimitives.ReadUInt32LittleEndian(data[0x04..]),
            FileCount = BinaryPrimitives.ReadUInt64LittleEndian(data[0x08..]),
            Unknown10 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]),
            Unknown14 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]),
            Unknown18 = BinaryPrimitives.ReadUInt64LittleEndian(data[0x18..]),
            UnknownSize20 = BinaryPrimitives.ReadUInt64LittleEndian(data[0x20..]),
            UnknownSize28 = BinaryPrimitives.ReadUInt64LittleEndian(data[0x28..]),
        };
    }
}

/// <summary>One 32-byte entry of the type table that follows the header.</summary>
public sealed class BundleTypeEntry
{
    public ulong Unknown00 { get; init; }
    public ulong TypeId { get; init; }
    public ulong Count { get; init; }
    public uint Alignment { get; init; }
    public uint Unknown1C { get; init; }

    public string TypeName => StingrayTypeIds.NameOf(TypeId);

    public static BundleTypeEntry Read(ReadOnlySpan<byte> data) => new()
    {
        Unknown00 = BinaryPrimitives.ReadUInt64LittleEndian(data),
        TypeId = BinaryPrimitives.ReadUInt64LittleEndian(data[0x08..]),
        Count = BinaryPrimitives.ReadUInt64LittleEndian(data[0x10..]),
        Alignment = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]),
        Unknown1C = BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]),
    };
}

/// <summary>
/// One 80-byte file entry. Mutable: repacking rewrites
/// <see cref="GpuOffset"/> and <see cref="GpuSize"/> in place.
/// </summary>
public sealed class BundleFileEntry
{
    /// <summary>Byte offset of this entry within the bundle file.</summary>
    public int EntryOffset { get; init; }

    public ulong FileId { get; init; }
    public ulong TypeId { get; init; }

    /// <summary>Offset of the CPU-side payload within the bundle file.</summary>
    public ulong Offset { get; init; }
    public ulong StreamOffset { get; init; }
    public ulong GpuOffset { get; set; }

    public ulong Unknown28 { get; init; }
    public ulong Unknown30 { get; init; }

    public uint Size { get; init; }
    public uint StreamSize { get; init; }
    public uint GpuSize { get; set; }

    public uint Unknown44 { get; init; }
    public uint Unknown48 { get; init; }
    public uint Index { get; init; }

    public string TypeName => StingrayTypeIds.NameOf(TypeId);
    public string Name => $"{FileId:X16}";
    public bool HasGpuData => GpuSize > 0;
    public bool IsTexture => TypeId == StingrayTypeIds.Texture;

    public static BundleFileEntry Read(ReadOnlySpan<byte> data, int entryOffset) => new()
    {
        EntryOffset = entryOffset,
        FileId = BinaryPrimitives.ReadUInt64LittleEndian(data),
        TypeId = BinaryPrimitives.ReadUInt64LittleEndian(data[0x08..]),
        Offset = BinaryPrimitives.ReadUInt64LittleEndian(data[0x10..]),
        StreamOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[0x18..]),
        GpuOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[0x20..]),
        Unknown28 = BinaryPrimitives.ReadUInt64LittleEndian(data[0x28..]),
        Unknown30 = BinaryPrimitives.ReadUInt64LittleEndian(data[0x30..]),
        Size = BinaryPrimitives.ReadUInt32LittleEndian(data[0x38..]),
        StreamSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]),
        GpuSize = BinaryPrimitives.ReadUInt32LittleEndian(data[0x40..]),
        Unknown44 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x44..]),
        Unknown48 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x48..]),
        Index = BinaryPrimitives.ReadUInt32LittleEndian(data[0x4C..]),
    };

    /// <summary>
    /// Writes GPU offset/size into a bundle image without touching this instance,
    /// so a plan can be applied more than once from unchanged state.
    /// </summary>
    public void WriteGpuFields(Span<byte> bundleImage, ulong gpuOffset, uint gpuSize)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bundleImage[(EntryOffset + 0x20)..], gpuOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bundleImage[(EntryOffset + 0x40)..], gpuSize);
    }
}
