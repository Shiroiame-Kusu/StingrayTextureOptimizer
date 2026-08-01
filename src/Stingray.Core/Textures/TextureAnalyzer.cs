// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

/// <summary>
/// Examines a decoded 32-bit surface and recommends a block format for it.
/// </summary>
/// <remarks>
/// The interesting cases in practice are textures an author exported straight
/// from an image editor: uncompressed RGBA8 where whole channels turn out to be
/// constant. Detecting those is what turns a 4x saving into a 100x one.
/// </remarks>
public static class TextureAnalyzer
{
    /// <summary>Dimension used when collapsing a solid-colour texture.</summary>
    public const int SolidColourExtent = 16;

    /// <summary>
    /// Analyses a surface already normalised to straight RGBA. Callers hand
    /// compressed sources through <see cref="TextureDecoder.ToRgba"/> first, so
    /// compressed and uncompressed textures take the same path from here.
    /// </summary>
    public static TextureAnalysis Analyze(ReadOnlySpan<byte> surface, int width, int height,
                                          bool measureDetail = true)
    {
        if (surface.Length % 4 != 0)
            throw new ArgumentException("Surface length is not a whole number of pixels.", nameof(surface));
        if ((long)width * height * 4 != surface.Length)
            throw new ArgumentException("Surface length does not match the given dimensions.", nameof(surface));

        // Histogram every channel in one pass; exact distinct counts fall out of it.
        var counts = new int[4 * 256];
        var totals = new long[4];

        for (var i = 0; i < surface.Length; i += 4)
        {
            counts[surface[i]]++;
            counts[256 + surface[i + 1]]++;
            counts[512 + surface[i + 2]]++;
            counts[768 + surface[i + 3]]++;
        }

        var pixels = surface.Length / 4;
        var stats = new ChannelStatistics[4];
        for (var c = 0; c < 4; c++)
        {
            byte min = 0, max = 0;
            var distinct = 0;
            var seen = false;
            for (var v = 0; v < 256; v++)
            {
                var n = counts[c * 256 + v];
                if (n == 0) continue;
                distinct++;
                totals[c] += (long)n * v;
                if (!seen) { min = (byte)v; seen = true; }
                max = (byte)v;
            }

            stats[c] = new ChannelStatistics
            {
                Minimum = min,
                Maximum = max,
                DistinctValues = distinct,
                Mean = pixels == 0 ? 0 : (double)totals[c] / pixels,
            };
        }

        var solid = stats.All(c => c.IsConstant);

        return new TextureAnalysis
        {
            Red = stats[0], Green = stats[1], Blue = stats[2], Alpha = stats[3],
            // A solid colour trivially survives any resize, and measuring it would
            // just burn time on a 16-megapixel no-op.
            DetailHeadroomDb = solid ? double.PositiveInfinity
                             : measureDetail ? MeasureHalfResolutionPsnr(surface, width, height)
                             : double.NaN,
        };
    }

    public static FormatRecommendation Recommend(
        TextureAnalysis analysis,
        int width,
        int height,
        OptimizationStrategy strategy = OptimizationStrategy.Balanced,
        bool collapseSolidColours = true,
        int maxDimension = 0,
        DxgiFormat sourceFormat = DxgiFormat.Unknown,
        int mipCount = 1,
        MipMode mipMode = MipMode.KeepChain,
        int streamFloor = 0,
        int prefixLength = 0)
    {
        // Streaming composes with the size cap rather than overriding it. The cap
        // discards levels above it for good, which is the only part that costs
        // quality; what remains goes to .stream and only the tail below the floor
        // stays resident. So "cap at 1024, keep 256 resident" means .stream holds
        // 1024 and down while video memory holds 256 and down.
        //
        // A floor at or above what survives the cap is excluded: that would leave
        // every level resident and still write a second copy into .stream, which is
        // all of the disk cost for none of the benefit.
        if (streamFloor > 0 && mipCount > 1 && sourceFormat.IsSizable())
        {
            var capped = DxgiFormatInfo.LevelsToDrop(width, height, mipCount, maxDimension);
            var cappedWidth = Math.Max(1, width >> capped);
            var cappedHeight = Math.Max(1, height >> capped);
            var cappedMips = mipCount - capped;

            var resident = DxgiFormatInfo.LevelsToDrop(
                cappedWidth, cappedHeight, cappedMips, streamFloor);

            if (resident > 0 && StingrayTexturePrefix.CanDescribe(prefixLength, cappedMips))
            {
                var chain = sourceFormat.MipChain(cappedWidth, cappedHeight, cappedMips);
                var residentBytes = chain.Skip(resident).Sum();
                var capNote = capped == 0
                    ? $"the whole {mipCount}-level chain"
                    : $"{cappedWidth}x{cappedHeight} and below ({capped} level(s) discarded)";

                return new FormatRecommendation
                {
                    Format = sourceFormat,
                    Width = cappedWidth,
                    Height = cappedHeight,
                    MipLevelsToDrop = capped,
                    TargetMipCount = cappedMips,
                    StreamResidentMip = resident,
                    IsLossless = capped == 0,
                    Rationale = $"streamed: {capNote} moves to .stream, leaving "
                              + $"{Math.Max(1, cappedWidth >> resident)}x{Math.Max(1, cappedHeight >> resident)} "
                              + $"and below resident ({residentBytes:N0} bytes); "
                              + "the rest still loads on demand",
                };
            }
        }

        // A texture that carries a mip chain can be shrunk by discarding its top
        // levels: the ones that remain are the author's own pixels, already
        // encoded, so nothing is recompressed and nothing is resampled. That is
        // strictly better than re-encoding a downscale, and far quicker.
        // Any sizable format, not just block-compressed: an uncompressed chain is
        // sliced too. Letting one fall through to the branches below would pick a
        // format the slice cannot deliver, and the header would then describe
        // bytes that were never re-encoded.
        if (mipCount > 1 && sourceFormat.IsSizable())
        {
            var drop = DxgiFormatInfo.LevelsToDrop(width, height, mipCount, maxDimension);
            var collapsing = mipMode == MipMode.SingleLevel && mipCount > 1;

            if (drop == 0 && !collapsing)
                return new FormatRecommendation
                {
                    Format = sourceFormat,
                    Width = width,
                    Height = height,
                    TargetMipCount = mipCount,
                    IsLossless = true,
                    Rationale = $"{mipCount} mip levels, none above the cap; left as is",
                };

            var remaining = collapsing ? 1 : mipCount - drop;
            var how = collapsing
                ? drop == 0
                    ? $"keeping only the full-size level and discarding the other {mipCount - 1}"
                    : $"keeping only the largest level that fits, discarding the other {mipCount - 1}"
                : $"dropping the top {drop} mip level(s), keeping the remaining {remaining}";

            return new FormatRecommendation
            {
                Format = sourceFormat,
                Width = Math.Max(1, width >> drop),
                Height = Math.Max(1, height >> drop),
                MipLevelsToDrop = drop,
                TargetMipCount = remaining,
                IsLossless = false,
                Rationale = $"{how}; kept exactly as authored, with no re-encoding",
            };
        }

        // Collapse solid colours first, whatever they are stored as. Real bundles
        // contain 4096x4096 BC7 surfaces holding a single colour; skipping them
        // just because they are "already compressed" leaves 16 MiB on the table.
        if (analysis.IsSolidColour && collapseSolidColours && sourceFormat.IsBlockCompressed())
        {
            var solid = analysis.SolidColour;
            return new FormatRecommendation
            {
                Format = sourceFormat,
                Width = SolidColourExtent,
                Height = SolidColourExtent,
                IsLossless = true,
                Rationale = $"every pixel is RGBA({solid[0]},{solid[1]},{solid[2]},{solid[3]}); "
                          + $"collapsed to {SolidColourExtent}x{SolidColourExtent} in the existing format",
            };
        }

        // Otherwise an already-compressed texture is only ever resized: re-encoding
        // it into a different family would compound generation loss for no size
        // gain, and switching sRGB to linear would shift every colour.
        if (sourceFormat.IsBlockCompressed())
        {
            var (rw, rh, rnote) = ApplyDownscale(width, height, maxDimension, analysis);
            return new FormatRecommendation
            {
                Format = sourceFormat,
                Width = rw,
                Height = rh,
                IsLossless = false,
                Rationale = rnote ?? "already compressed; left as is",
            };
        }

        if (analysis.IsSolidColour && collapseSolidColours)
        {
            var c = analysis.SolidColour;

            // BC1 endpoints are 5:6:5, so only some colours survive exactly.
            // BC7 can reproduce any 8-bit RGBA, so fall back to it when needed.
            if (IsExactInBc1(c[0], c[1], c[2], c[3]))
            {
                return new FormatRecommendation
                {
                    Format = MatchColourSpace(DxgiFormat.Bc1Unorm, sourceFormat),
                    Width = SolidColourExtent,
                    Height = SolidColourExtent,
                    IsLossless = true,
                    Rationale = $"every pixel is RGBA({c[0]},{c[1]},{c[2]},{c[3]}); "
                              + $"collapsed to {SolidColourExtent}x{SolidColourExtent}, exact in BC1",
                };
            }

            return new FormatRecommendation
            {
                Format = MatchColourSpace(DxgiFormat.Bc7Unorm, sourceFormat),
                Width = SolidColourExtent,
                Height = SolidColourExtent,
                IsLossless = true,
                Rationale = $"every pixel is RGBA({c[0]},{c[1]},{c[2]},{c[3]}); "
                          + $"collapsed to {SolidColourExtent}x{SolidColourExtent}, exact in BC7",
            };
        }

        // BC1 fits when alpha carries nothing, and under the smallest-size strategy
        // also when alpha is a pure cutout, since BC1 stores one bit of it.
        var bc1Fits = !analysis.HasAlphaDetail
                   || (strategy == OptimizationStrategy.SmallestSize && analysis.HasBinaryAlpha);

        if (bc1Fits && strategy != OptimizationStrategy.MaximumQuality)
        {
            var (bw, bh, bnote) = ApplyDownscale(width, height, maxDimension, analysis);
            var opaque = analysis.HasAlphaDetail
                ? "alpha is a pure cutout, which fits BC1's single alpha bit at half the size of BC7"
                : "alpha is uniformly opaque, so BC1 halves the size of an alpha-capable format";
            return new FormatRecommendation
            {
                Format = MatchColourSpace(DxgiFormat.Bc1Unorm, sourceFormat),
                Width = bw,
                Height = bh,
                IsLossless = false,
                Rationale = bnote is null ? opaque : $"{opaque}; {bnote}",
            };
        }

        var reason = analysis.IsFlatColourWithAlphaDetail
            ? "colour channels are flat but alpha carries detail, so alpha must be preserved"
            : analysis.HasAlphaDetail
                ? "alpha carries detail"
                : "maximum-quality strategy keeps BC7 for opaque content";

        var (w, h, note) = ApplyDownscale(width, height, maxDimension, analysis);

        return new FormatRecommendation
        {
            Format = MatchColourSpace(DxgiFormat.Bc7Unorm, sourceFormat),
            Width = w,
            Height = h,
            IsLossless = false,
            Rationale = note is null ? reason : $"{reason}; {note}",
        };
    }

    /// <summary>
    /// Keeps an sRGB source in an sRGB target. Writing a linear format where the
    /// engine expects sRGB shifts every colour on screen.
    /// </summary>
    internal static DxgiFormat MatchColourSpace(DxgiFormat target, DxgiFormat source)
    {
        var sourceIsSrgb = source is DxgiFormat.R8G8B8A8UnormSrgb or DxgiFormat.B8G8R8A8UnormSrgb
                                  or DxgiFormat.Bc1UnormSrgb or DxgiFormat.Bc2UnormSrgb
                                  or DxgiFormat.Bc3UnormSrgb or DxgiFormat.Bc7UnormSrgb;
        if (!sourceIsSrgb) return target;

        return target switch
        {
            DxgiFormat.Bc1Unorm => DxgiFormat.Bc1UnormSrgb,
            DxgiFormat.Bc2Unorm => DxgiFormat.Bc2UnormSrgb,
            DxgiFormat.Bc3Unorm => DxgiFormat.Bc3UnormSrgb,
            DxgiFormat.Bc7Unorm => DxgiFormat.Bc7UnormSrgb,
            _ => target,
        };
    }

    /// <summary>
    /// Halves dimensions until they fit <paramref name="maxDimension"/>, reporting
    /// what the measured detail says it will cost.
    /// </summary>
    internal static (int Width, int Height, string? Note) ApplyDownscale(
        int width, int height, int maxDimension, TextureAnalysis analysis)
    {
        if (maxDimension <= 0 || Math.Max(width, height) <= maxDimension)
            return (width, height, null);

        int w = width, h = height;
        while (Math.Max(w, h) > maxDimension && w % 2 == 0 && h % 2 == 0 && w > 4 && h > 4)
        {
            w /= 2;
            h /= 2;
        }

        return (w, h, $"resized to {w}x{h} ({analysis.DetailVerdict})");
    }

    /// <summary>
    /// Measures how much of a surface's detail genuinely needs full resolution, by
    /// halving it and restoring it and reporting the PSNR in dB.
    /// </summary>
    /// <remarks>
    /// A high value means the extra resolution carries nothing a viewer could see:
    /// the art was effectively authored smaller, or is smooth enough that the top
    /// octave is empty. Real bundles are full of 4096² textures scoring above 45 dB
    /// — and some that are a single colour, scoring infinity.
    /// </remarks>
    public static double MeasureHalfResolutionPsnr(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width < 2 || height < 2) return double.PositiveInfinity;

        int hw = width / 2, hh = height / 2;
        var half = new byte[hw * hh * 4];

        for (var y = 0; y < hh; y++)
        for (var x = 0; x < hw; x++)
        for (var c = 0; c < 4; c++)
        {
            var a = rgba[((2 * y) * width + 2 * x) * 4 + c];
            var b = rgba[((2 * y) * width + 2 * x + 1) * 4 + c];
            var d = rgba[((2 * y + 1) * width + 2 * x) * 4 + c];
            var e = rgba[((2 * y + 1) * width + 2 * x + 1) * 4 + c];
            half[(y * hw + x) * 4 + c] = (byte)((a + b + d + e + 2) / 4);
        }

        // Bilinear restore, accumulating error without materialising the upscale.
        double squaredError = 0;
        for (var y = 0; y < height; y++)
        {
            var sy = (y + 0.5) / 2.0 - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(sy), 0, hh - 1);
            var y1 = Math.Clamp(y0 + 1, 0, hh - 1);
            var fy = Math.Clamp(sy - y0, 0, 1);

            for (var x = 0; x < width; x++)
            {
                var sx = (x + 0.5) / 2.0 - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(sx), 0, hw - 1);
                var x1 = Math.Clamp(x0 + 1, 0, hw - 1);
                var fx = Math.Clamp(sx - x0, 0, 1);

                for (var c = 0; c < 4; c++)
                {
                    var p00 = half[(y0 * hw + x0) * 4 + c];
                    var p01 = half[(y0 * hw + x1) * 4 + c];
                    var p10 = half[(y1 * hw + x0) * 4 + c];
                    var p11 = half[(y1 * hw + x1) * 4 + c];

                    var top = p00 + (p01 - p00) * fx;
                    var bottom = p10 + (p11 - p10) * fx;
                    var value = top + (bottom - top) * fy;

                    var delta = rgba[(y * width + x) * 4 + c] - value;
                    squaredError += delta * delta;
                }
            }
        }

        var mse = squaredError / ((double)width * height * 4);
        return mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>True when a colour round-trips exactly through BC1's 5:6:5 endpoints.</summary>
    internal static bool IsExactInBc1(byte r, byte g, byte b, byte a)
    {
        if (a != 255) return false;
        return Expand5(r >> 3) == r && Expand6(g >> 2) == g && Expand5(b >> 3) == b;

        static int Expand5(int v) => (v << 3) | (v >> 2);
        static int Expand6(int v) => (v << 2) | (v >> 4);
    }
}
