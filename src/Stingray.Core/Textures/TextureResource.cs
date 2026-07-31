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

    /// <summary>Null when the texture can be optimised, otherwise why it cannot.</summary>
    public string? Unsupported { get; private init; }

    public bool CanOptimize => Unsupported is null;

    public static bool TryCreate(Bundle bundle, BundleFileEntry entry, out TextureResource? resource)
    {
        resource = null;
        if (!entry.IsTexture || !entry.HasGpuData) return false;
        if (!DdsHeader.TryRead(bundle.GetCpuPayload(entry), out var header) || header is null) return false;

        resource = new TextureResource(entry, header)
        {
            Unsupported = Classify(header, entry),
        };
        return true;
    }

    private static string? Classify(DdsHeader header, BundleFileEntry entry)
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
        if (header.MipMapCount > 1)
            return $"has {header.MipMapCount} mip levels (only single-mip surfaces are handled)";
        if (header.Width % 4 != 0 || header.Height % 4 != 0)
            return $"dimensions {header.Width}x{header.Height} are not a multiple of 4";

        var expected = header.DxgiFormat.SurfaceSize(header.Width, header.Height);
        if (expected != entry.GpuSize)
            return $"payload is {entry.GpuSize} bytes but the header implies {expected}";

        return null;
    }

    public override string ToString() =>
        $"{Name} {Width}x{Height} {SourceFormat.DisplayName()} ({GpuSize:N0} bytes)";
}
