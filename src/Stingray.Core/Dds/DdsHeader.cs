// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace Stingray.Core.Dds;

/// <summary>
/// Reader/patcher for the DDS header embedded in a texture's CPU payload.
/// </summary>
/// <remarks>
/// Stingray prefixes the DDS image with its own texture header, so the DDS magic
/// is located by search rather than assumed to be at offset zero. Patching is
/// done in place and never changes the header's length, which keeps every
/// bundle <c>Offset</c>/<c>Size</c> field valid.
/// </remarks>
public sealed class DdsHeader
{
    public const uint DdsMagic = 0x2053_4444;      // "DDS "
    public const uint FourCcDx10 = 0x3031_5844;    // "DX10"

    public const uint FlagPitch = 0x8;
    public const uint FlagLinearSize = 0x8_0000;
    public const uint PixelFormatFourCc = 0x4;

    public const int CoreHeaderLength = 128;       // magic + DDS_HEADER
    public const int Dx10HeaderLength = 20;

    private DdsHeader(int offset) => Offset = offset;

    /// <summary>Offset of the DDS magic within the CPU payload.</summary>
    public int Offset { get; }

    public uint Flags { get; private set; }
    public int Height { get; private set; }
    public int Width { get; private set; }
    public uint PitchOrLinearSize { get; private set; }
    public int MipMapCount { get; private set; }
    public uint FourCc { get; private set; }
    public bool HasDx10Header => FourCc == FourCcDx10;
    public DxgiFormat DxgiFormat { get; private set; }

    /// <summary>Total header length including the DX10 extension when present.</summary>
    public int Length => CoreHeaderLength + (HasDx10Header ? Dx10HeaderLength : 0);

    /// <summary>Locates and parses the DDS header inside a texture payload.</summary>
    public static bool TryRead(ReadOnlySpan<byte> payload, out DdsHeader? header)
    {
        header = null;
        var magic = BitConverter.GetBytes(DdsMagic);
        var offset = payload.IndexOf(magic);
        if (offset < 0 || payload.Length - offset < CoreHeaderLength) return false;

        var dds = payload[offset..];
        var result = new DdsHeader(offset)
        {
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(dds[0x08..]),
            Height = (int)BinaryPrimitives.ReadUInt32LittleEndian(dds[0x0C..]),
            Width = (int)BinaryPrimitives.ReadUInt32LittleEndian(dds[0x10..]),
            PitchOrLinearSize = BinaryPrimitives.ReadUInt32LittleEndian(dds[0x14..]),
            MipMapCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(dds[0x1C..]),
            FourCc = BinaryPrimitives.ReadUInt32LittleEndian(dds[0x54..]),
        };

        if (result.HasDx10Header)
        {
            if (dds.Length < CoreHeaderLength + Dx10HeaderLength) return false;
            result.DxgiFormat = (DxgiFormat)BinaryPrimitives.ReadUInt32LittleEndian(dds[CoreHeaderLength..]);
        }
        else
        {
            // Legacy FourCC textures are recognised but not optimised; the tool
            // only rewrites DX10-style headers so it never has to invent one.
            result.DxgiFormat = DxgiFormat.Unknown;
        }

        header = result;
        return true;
    }

    /// <summary>
    /// Rewrites dimensions, format and size fields in place. The payload span
    /// must start at the same position as the one passed to <see cref="TryRead"/>.
    /// </summary>
    public void Patch(Span<byte> payload, int width, int height, DxgiFormat format, long surfaceSize)
    {
        if (!HasDx10Header)
            throw new NotSupportedException("Only DX10-style DDS headers can be rewritten.");
        if (surfaceSize is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(surfaceSize));

        var dds = payload[Offset..];

        // Compressed surfaces are described by linear size, not pitch.
        var flags = Flags;
        flags = format.IsBlockCompressed()
            ? (flags & ~FlagPitch) | FlagLinearSize
            : (flags & ~FlagLinearSize) | FlagPitch;

        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x08..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x0C..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x10..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x14..], (uint)surfaceSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[CoreHeaderLength..], (uint)format);

        Flags = flags;
        Width = width;
        Height = height;
        PitchOrLinearSize = (uint)surfaceSize;
        DxgiFormat = format;
    }

    /// <summary>
    /// Builds a standalone .dds file from this header plus pixel data, for export
    /// or for round-tripping through an external tool.
    /// </summary>
    public byte[] ToStandaloneFile(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> surface)
    {
        var header = payload.Slice(Offset, Length);
        var file = new byte[header.Length + surface.Length];
        header.CopyTo(file);
        surface.CopyTo(file.AsSpan(header.Length));
        return file;
    }
}
