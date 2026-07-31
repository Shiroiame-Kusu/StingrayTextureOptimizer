// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

/// <summary>
/// Encodes with BCnEncoder.Net: pure managed, no native payload, works anywhere
/// the tool runs. Slower than Compressonator, so it is the fallback rather than
/// the default — but it is what makes the tool dependency-free.
/// </summary>
public sealed class ManagedEncoderBackend : IBlockEncoderBackend
{
    public string Name => "BCnEncoder.Net";

    public bool IsAvailable => true;

    public byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height,
                         DxgiFormat target, EncodeOptions options)
    {
        // BC1 has two encodings: opaque, and one with a single alpha bit. Pick the
        // latter only when the surface actually has transparent texels, since the
        // alpha-capable mode gives up a colour endpoint.
        var format = ToCompressionFormat(target);
        if (format == CompressionFormat.Bc1 && HasTransparency(rgba))
            format = CompressionFormat.Bc1WithAlpha;

        var encoder = new BcEncoder(format);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = ToQuality(options.Quality);
        encoder.Options.IsParallel = options.ThreadCount > 1;
        encoder.Options.TaskCount = options.ThreadCount;

        return encoder.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32, 0, out _, out _);
    }

    private static bool HasTransparency(ReadOnlySpan<byte> rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return true;
        return false;
    }

    private static CompressionFormat ToCompressionFormat(DxgiFormat format) => format switch
    {
        DxgiFormat.Bc1Unorm or DxgiFormat.Bc1UnormSrgb => CompressionFormat.Bc1,
        DxgiFormat.Bc2Unorm or DxgiFormat.Bc2UnormSrgb => CompressionFormat.Bc2,
        DxgiFormat.Bc3Unorm or DxgiFormat.Bc3UnormSrgb => CompressionFormat.Bc3,
        DxgiFormat.Bc4Unorm => CompressionFormat.Bc4,
        DxgiFormat.Bc5Unorm => CompressionFormat.Bc5,
        DxgiFormat.Bc7Unorm or DxgiFormat.Bc7UnormSrgb => CompressionFormat.Bc7,
        _ => throw new NotSupportedException($"{format.DisplayName()} is not a supported encode target."),
    };

    private static CompressionQuality ToQuality(EncodeQuality quality) => quality switch
    {
        EncodeQuality.Fast => CompressionQuality.Fast,
        EncodeQuality.Best => CompressionQuality.BestQuality,
        _ => CompressionQuality.Balanced,
    };
}
