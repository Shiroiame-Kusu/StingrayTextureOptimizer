// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

public enum EncodeQuality
{
    Fast,
    Balanced,
    Best,
}

public sealed class EncodeOptions
{
    public EncodeQuality Quality { get; init; } = EncodeQuality.Balanced;

    /// <summary>
    /// Worker threads for the encoder. Defaults to leaving four cores free:
    /// saturating every core makes desktop sessions unusable during long encodes.
    /// </summary>
    public int ThreadCount { get; init; } = Math.Max(1, Environment.ProcessorCount - 4);
}

/// <summary>Block-compresses surfaces via BCnEncoder.Net.</summary>
public static class TextureEncoder
{
    public static byte[] Encode(
        ReadOnlySpan<byte> surface,
        int sourceWidth,
        int sourceHeight,
        DxgiFormat sourceFormat,
        int targetWidth,
        int targetHeight,
        DxgiFormat targetFormat,
        EncodeOptions? options = null)
    {
        options ??= new EncodeOptions();

        // Decodes first when the source is already compressed, which is how a
        // BC7 texture gets resized without changing format.
        var rgba = TextureDecoder.ToRgba(surface, sourceWidth, sourceHeight, sourceFormat);
        if (targetWidth != sourceWidth || targetHeight != sourceHeight)
            rgba = Resample(rgba, sourceWidth, sourceHeight, targetWidth, targetHeight);

        var encoder = new BcEncoder(ToCompressionFormat(targetFormat));
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = ToQuality(options.Quality);
        encoder.Options.IsParallel = options.ThreadCount > 1;
        encoder.Options.TaskCount = options.ThreadCount;

        var encoded = encoder.EncodeToRawBytes(
            rgba, targetWidth, targetHeight, PixelFormat.Rgba32, 0, out _, out _);

        var expected = targetFormat.SurfaceSize(targetWidth, targetHeight);
        if (encoded.LongLength != expected)
            throw new InvalidOperationException(
                $"Encoder produced {encoded.LongLength} bytes for {targetWidth}x{targetHeight} "
              + $"{targetFormat.DisplayName()}, expected {expected}.");

        return encoded;
    }

    /// <summary>
    /// Area-average resample. Box filtering is the right default here: these are
    /// mostly masks and albedo maps where a sharper kernel would add ringing.
    /// </summary>
    private static byte[] Resample(byte[] source, int sw, int sh, int dw, int dh)
    {
        if (dw <= 0 || dh <= 0)
            throw new ArgumentOutOfRangeException(nameof(dw), "Target dimensions must be positive.");

        var total = (long)dw * dh * 4;
        if (total > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(dw), "Target surface is too large.");
        var result = new byte[total];

        for (var y = 0; y < dh; y++)
        {
            var y0 = (int)((long)y * sh / dh);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));

            for (var x = 0; x < dw; x++)
            {
                var x0 = (int)((long)x * sw / dw);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / dw));

                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = (long)sy * sw * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = (int)(row + (long)sx * 4);
                        r += source[p];
                        g += source[p + 1];
                        b += source[p + 2];
                        a += source[p + 3];
                        n++;
                    }
                }

                var d = (y * dw + x) * 4;
                result[d] = (byte)(r / n);
                result[d + 1] = (byte)(g / n);
                result[d + 2] = (byte)(b / n);
                result[d + 3] = (byte)(a / n);
            }
        }

        return result;
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

    /// <summary>Formats offered as manual overrides in the UI.</summary>
    public static IReadOnlyList<DxgiFormat> SupportedTargets { get; } =
    [
        DxgiFormat.Bc7Unorm,
        DxgiFormat.Bc3Unorm,
        DxgiFormat.Bc1Unorm,
        DxgiFormat.Bc5Unorm,
        DxgiFormat.Bc4Unorm,
    ];
}
