// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

/// <summary>
/// Decodes block-compressed surfaces back to RGBA8 so already-compressed
/// textures can be analysed and resized.
/// </summary>
public static class TextureDecoder
{
    public static bool CanDecode(DxgiFormat format) =>
        format.IsBlockCompressed() && format is not (DxgiFormat.Bc6HUf16 or DxgiFormat.Bc6HSf16);

    /// <summary>Decodes a compressed surface to straight RGBA byte order.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> surface, int width, int height, DxgiFormat format)
    {
        if (!CanDecode(format))
            throw new NotSupportedException($"Cannot decode {format.DisplayName()}.");

        var decoder = new BcDecoder();
        var pixels = decoder.DecodeRaw(
            surface.ToArray(), width, height, ToCompressionFormat(format));

        var rgba = new byte[(long)width * height * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var o = i * 4;
            rgba[o] = p.r; rgba[o + 1] = p.g; rgba[o + 2] = p.b; rgba[o + 3] = p.a;
        }

        return rgba;
    }

    /// <summary>
    /// Returns a surface as RGBA8 regardless of whether it was stored compressed.
    /// </summary>
    public static byte[] ToRgba(ReadOnlySpan<byte> surface, int width, int height, DxgiFormat format)
    {
        if (format.IsBlockCompressed()) return Decode(surface, width, height, format);
        if (!format.IsUncompressedRgba())
            throw new NotSupportedException($"Cannot read {format.DisplayName()} as RGBA.");

        var rgba = surface.ToArray();
        if (!format.IsBgra()) return rgba;
        for (var i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        return rgba;
    }

    internal static CompressionFormat ToCompressionFormat(DxgiFormat format) => format switch
    {
        // BC1 signals punchthrough alpha per block through endpoint ordering, so
        // decoding as Bc1WithAlpha is the correct reading of any BC1 payload:
        // blocks without it decode identically, and blocks with it keep their
        // transparency instead of turning black.
        DxgiFormat.Bc1Unorm or DxgiFormat.Bc1UnormSrgb => CompressionFormat.Bc1WithAlpha,
        DxgiFormat.Bc2Unorm or DxgiFormat.Bc2UnormSrgb => CompressionFormat.Bc2,
        DxgiFormat.Bc3Unorm or DxgiFormat.Bc3UnormSrgb => CompressionFormat.Bc3,
        DxgiFormat.Bc4Unorm => CompressionFormat.Bc4,
        DxgiFormat.Bc5Unorm => CompressionFormat.Bc5,
        DxgiFormat.Bc7Unorm or DxgiFormat.Bc7UnormSrgb => CompressionFormat.Bc7,
        _ => throw new NotSupportedException($"{format.DisplayName()} has no BCn mapping."),
    };
}
