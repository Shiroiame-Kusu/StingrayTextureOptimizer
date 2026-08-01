// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

/// <summary>Per-channel distribution over a decoded surface.</summary>
public sealed class ChannelStatistics
{
    public required byte Minimum { get; init; }
    public required byte Maximum { get; init; }
    public required int DistinctValues { get; init; }
    public required double Mean { get; init; }

    public bool IsConstant => Minimum == Maximum;

    public override string ToString() =>
        IsConstant ? $"const {Minimum}" : $"{Minimum}-{Maximum} ({DistinctValues} values)";
}

/// <summary>What the analyser concluded about a texture's contents.</summary>
public sealed class TextureAnalysis
{
    public required ChannelStatistics Red { get; init; }
    public required ChannelStatistics Green { get; init; }
    public required ChannelStatistics Blue { get; init; }
    public required ChannelStatistics Alpha { get; init; }

    public IReadOnlyList<ChannelStatistics> Channels => [Red, Green, Blue, Alpha];

    /// <summary>Every pixel is identical — the surface carries no detail at all.</summary>
    public bool IsSolidColour =>
        Red.IsConstant && Green.IsConstant && Blue.IsConstant && Alpha.IsConstant;

    /// <summary>Colour is flat but alpha varies, typical of an unused normal map slot.</summary>
    public bool IsFlatColourWithAlphaDetail =>
        Red.IsConstant && Green.IsConstant && Blue.IsConstant && !Alpha.IsConstant;

    /// <summary>Alpha carries information, so an alpha-capable format is required.</summary>
    public bool HasAlphaDetail => !Alpha.IsConstant || Alpha.Minimum != 255;

    /// <summary>
    /// Alpha is a pure cutout — only fully transparent or fully opaque. BC1 carries
    /// one bit of alpha, so such a texture fits it at half the size of BC7.
    /// </summary>
    public bool HasBinaryAlpha =>
        Alpha.DistinctValues == 2 && Alpha.Minimum == 0 && Alpha.Maximum == 255;

    public byte[] SolidColour => [Red.Minimum, Green.Minimum, Blue.Minimum, Alpha.Minimum];

    /// <summary>
    /// PSNR in dB of halving the surface and restoring it. High means the extra
    /// resolution carries nothing visible. NaN when not measured.
    /// </summary>
    public double DetailHeadroomDb { get; init; } = double.NaN;

    /// <summary>Plain-language reading of <see cref="DetailHeadroomDb"/>.</summary>
    public string DetailVerdict => DetailHeadroomDb switch
    {
        double.PositiveInfinity => "nothing lost",
        var d when double.IsNaN(d) => "not measured",
        >= 45 => $"nothing visible lost ({DetailHeadroomDb:F0} dB)",
        >= 36 => $"slight softening ({DetailHeadroomDb:F0} dB)",
        >= 30 => $"visible softening ({DetailHeadroomDb:F0} dB)",
        _ => $"real detail lost ({DetailHeadroomDb:F0} dB)",
    };

    /// <summary>True when halving costs nothing a viewer could notice.</summary>
    public bool HalvingIsSafe => DetailHeadroomDb >= 45;

    /// <summary>Short human-readable characterisation, used by the GUI and CLI.</summary>
    public string Summary
    {
        get
        {
            if (IsSolidColour)
                return $"solid RGBA({Red.Minimum},{Green.Minimum},{Blue.Minimum},{Alpha.Minimum})";
            if (IsFlatColourWithAlphaDetail)
                return $"flat RGB({Red.Minimum},{Green.Minimum},{Blue.Minimum}), "
                     + $"alpha has {Alpha.DistinctValues} values";
            return HasAlphaDetail ? "full detail with alpha" : "full detail, opaque";
        }
    }
}

/// <summary>What to do with a mip chain when a texture is shrunk.</summary>
public enum MipMode
{
    /// <summary>
    /// Discard the top levels only, keeping the rest of the chain. The texture
    /// stays mipmapped, so it will not shimmer at distance and still samples
    /// efficiently. The tail costs about a third of the level above it.
    /// </summary>
    KeepChain,

    /// <summary>
    /// Keep one level and nothing else — the largest that fits the cap. Smaller
    /// than <see cref="KeepChain"/> by roughly a quarter, but the texture is no
    /// longer mipmapped, which brings back shimmering when it is minified and
    /// costs texture-cache efficiency.
    /// </summary>
    SingleLevel,
}

/// <summary>How aggressively to trade quality for size when recommending formats.</summary>
public enum OptimizationStrategy
{
    /// <summary>BC7 wherever detail exists, BC1 for opaque-and-flat cases.</summary>
    Balanced,

    /// <summary>BC7 everywhere detail exists, never BC1 for real image content.</summary>
    MaximumQuality,

    /// <summary>Prefer BC1 whenever alpha carries no information.</summary>
    SmallestSize,
}

/// <summary>The analyser's suggested target for one texture.</summary>
public sealed class FormatRecommendation
{
    public required DxgiFormat Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Rationale { get; init; }

    /// <summary>True when the result is provably identical to the source.</summary>
    public required bool IsLossless { get; init; }

    /// <summary>
    /// Top mip levels to discard instead of re-encoding. Slicing a chain keeps
    /// the author's own pixels for every level that survives.
    /// </summary>
    public int MipLevelsToDrop { get; init; }

    /// <summary>How many levels remain. 1 means the chain is gone.</summary>
    public int TargetMipCount { get; init; } = 1;

    /// <summary>
    /// First mip level to keep resident when converting the texture to a streamed
    /// one, or null to leave its residency alone. Nothing is discarded: the whole
    /// chain moves to <c>.stream</c> and only this tail stays in video memory.
    /// </summary>
    public int? StreamResidentMip { get; init; }

    /// <summary>
    /// Whether a mip chain is being built for a texture that shipped without one.
    /// The levels do not exist yet, so unlike every other path here they are
    /// generated rather than sliced out of what is already there.
    /// </summary>
    public bool GeneratesMips { get; init; }

    public long PredictedSize => Format.SurfaceSize(Width, Height);
}
