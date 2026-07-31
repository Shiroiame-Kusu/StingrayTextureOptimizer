// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;

namespace Stingray.Core.Textures;

/// <summary>A texture entry paired with its parsed DDS header.</summary>
public sealed class TextureResource
{
    private TextureResource(BundleFileEntry entry, DdsHeader header)
    {
        Entry = entry;
        Header = header;
    }

    public BundleFileEntry Entry { get; }
    public DdsHeader Header { get; }

    public string Name => Entry.Name;
    public int Width => Header.Width;
    public int Height => Header.Height;
    public DxgiFormat SourceFormat => Header.DxgiFormat;
    public uint GpuSize => Entry.GpuSize;

    /// <summary>
    /// Index of the first mip level held in .gpu_resources. 0xFFFFFFFF means none
    /// are streamed and the whole chain is resident. Lives at offset 8 of the
    /// Stingray prefix, ahead of the DDS header.
    /// </summary>
    public uint FirstResidentMip { get; private init; } = NotStreamed;

    public const uint NotStreamed = 0xFFFF_FFFF;

    /// <summary>
    /// Whether part of this texture lives in the .stream file. The entry's stream
    /// size is the authoritative signal: the header index alone is ambiguous,
    /// since tiny textures carry 0 there while streaming nothing.
    /// </summary>
    public bool IsStreamed => Entry.StreamSize > 0;
    public int MipMapCount => Math.Max(1, Header.MipMapCount);

    /// <summary>Size of each resident mip level, largest first.</summary>
    public long[] MipChain => SourceFormat.MipChain(Width, Height, MipMapCount);

    /// <summary>Null when the texture can be optimised, otherwise why it cannot.</summary>
    public string? Unsupported { get; private init; }

    public bool CanOptimize => Unsupported is null;

    public static bool TryCreate(Bundle bundle, BundleFileEntry entry, out TextureResource? resource)
    {
        resource = null;
        if (!entry.IsTexture || !entry.HasGpuData) return false;
        if (!DdsHeader.TryRead(bundle.GetCpuPayload(entry), out var header) || header is null) return false;

        var payload = bundle.GetCpuPayload(entry);
        var firstResident = header.Offset >= 12
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[8..])
            : NotStreamed;

        resource = new TextureResource(entry, header)
        {
            FirstResidentMip = firstResident,
            Unsupported = Classify(header, entry, firstResident),
        };
        return true;
    }

    private static string? Classify(DdsHeader header, BundleFileEntry entry, uint firstResidentMip)
    {
        if (!header.HasDx10Header)
            return "legacy FourCC header (no DX10 block)";
        if (header.DxgiFormat.IsBlockCompressed())
        {
            // Compressed textures are still worth examining: they can be resized,
            // and mods ship plenty of solid-colour surfaces stored as 4096x4096 BC7.
            if (!TextureDecoder.CanDecode(header.DxgiFormat))
                return $"cannot decode {header.DxgiFormat.DisplayName()}";
        }
        else if (!header.DxgiFormat.IsUncompressedRgba())
        {
            return $"unsupported format ({header.DxgiFormat.DisplayName()})";
        }
        if (header.Width % 4 != 0 || header.Height % 4 != 0)
            return $"dimensions {header.Width}x{header.Height} are not a multiple of 4";

        // Streamed textures keep their top levels in .stream, so their payload is
        // a tail rather than a whole surface or chain. Rewriting that means
        // rewriting the stream file too, which this does not do yet.
        if (entry.StreamSize > 0)
        {
            var top = firstResidentMip == NotStreamed || firstResidentMip == 0
                ? "some levels"
                : $"levels 0..{firstResidentMip - 1}";
            return $"streamed ({top} live in .stream)";
        }

        var mips = Math.Max(1, header.MipMapCount);
        var expected = mips > 1
            ? header.DxgiFormat.MipChain(header.Width, header.Height, mips).Sum()
            : header.DxgiFormat.SurfaceSize(header.Width, header.Height);

        if (expected != entry.GpuSize)
            return $"payload is {entry.GpuSize} bytes but the header implies {expected}";

        return null;
    }

    public override string ToString() =>
        $"{Name} {Width}x{Height} {SourceFormat.DisplayName()} ({GpuSize:N0} bytes)";
}
