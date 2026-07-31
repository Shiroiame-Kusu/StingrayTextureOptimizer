// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

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

    /// <summary>
    /// Which compressor to use. <see cref="EncoderBackend.Auto"/> prefers
    /// Compressonator's HPC path and falls back to the managed encoder.
    /// </summary>
    public EncoderBackend Backend { get; init; } = EncoderBackend.Auto;
}

/// <summary>Block-compresses surfaces, optionally resizing them on the way.</summary>
public static class TextureEncoder
{
    private static readonly ManagedEncoderBackend Managed = new();
    private static readonly CompressonatorBackend Compressonator = new();

    /// <summary>The backend a given preference resolves to right now.</summary>
    public static IBlockEncoderBackend Resolve(EncoderBackend preference) => preference switch
    {
        EncoderBackend.Managed => Managed,
        EncoderBackend.Compressonator => Compressonator.IsAvailable
            ? Compressonator
            : throw new InvalidOperationException(
                $"Compressonator was requested but is unusable: {CompressonatorBackend.UnavailableReason}."),
        _ => Compressonator.IsAvailable ? Compressonator : Managed,
    };

    /// <summary>Human-readable summary of what Auto would pick, and why.</summary>
    public static string BackendStatus =>
        Compressonator.IsAvailable
            ? Compressonator.Name
            : $"{Managed.Name} ({CompressonatorBackend.UnavailableReason})";

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

        var backend = Resolve(options.Backend);
        byte[] encoded;
        try
        {
            encoded = backend.Encode(rgba, targetWidth, targetHeight, targetFormat, options);
        }
        catch (Exception ex) when (backend is CompressonatorBackend
                                   && options.Backend == EncoderBackend.Auto
                                   && ex is InvalidOperationException or IOException
                                         or NotSupportedException
                                         or System.ComponentModel.Win32Exception)
        {
            // Auto must never fail outright: if the native path breaks mid-run,
            // finish the job on the managed encoder rather than losing the batch.
            encoded = Managed.Encode(rgba, targetWidth, targetHeight, targetFormat, options);
        }

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
