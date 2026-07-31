// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

/// <summary>Which block compressor to encode with.</summary>
public enum EncoderBackend
{
    /// <summary>Compressonator's HPC path when it is usable, otherwise managed.</summary>
    Auto,

    /// <summary>BCnEncoder.Net. Pure managed, always available.</summary>
    Managed,

    /// <summary>Compressonator CLI. Much faster, needs the bundled native binary.</summary>
    Compressonator,
}

/// <summary>One block-compression implementation.</summary>
public interface IBlockEncoderBackend
{
    string Name { get; }

    /// <summary>Whether this backend can run here at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Encodes a straight-RGBA surface to <paramref name="target"/>.</summary>
    byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height,
                  DxgiFormat target, EncodeOptions options);
}
