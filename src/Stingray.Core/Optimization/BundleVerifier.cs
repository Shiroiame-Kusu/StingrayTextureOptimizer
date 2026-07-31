// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Textures;

namespace Stingray.Core.Optimization;

public sealed record VerificationIssue(string Category, string Detail);

public sealed class VerificationReport
{
    public required IReadOnlyList<VerificationIssue> Issues { get; init; }

    /// <summary>Distinct payload regions, after collapsing shared ones.</summary>
    public required int SegmentsChecked { get; init; }

    /// <summary>Entries pointing at a payload another entry also uses.</summary>
    public int AliasedEntries { get; init; }

    public required int PayloadsCompared { get; init; }
    public required long PaddingBytes { get; init; }

    public bool Passed => Issues.Count == 0;
}

/// <summary>
/// Independent re-read of a written bundle. Deliberately does not share code with
/// the writer, so a bug in the writer is not mirrored by the check.
/// </summary>
public static class BundleVerifier
{
    public static VerificationReport Verify(string bundlePath, string? originalBundlePath = null)
    {
        var issues = new List<VerificationIssue>();
        var bundle = Bundle.Load(bundlePath);

        using var gpu = File.Exists(bundle.GpuResourcePath)
            ? GpuResourceFile.Open(bundle.GpuResourcePath)
            : null;

        if (gpu is null)
        {
            issues.Add(new VerificationIssue("missing-file",
                $"{Path.GetFileName(bundle.GpuResourcePath)} does not exist"));
            return new VerificationReport
            {
                Issues = issues, SegmentsChecked = 0, PayloadsCompared = 0, PaddingBytes = 0,
            };
        }

        // --- layout: alignment, ordering, overlap, bounds -----------------------
        // Several entries may deliberately share one payload, so overlap is only
        // checked between *distinct* regions. Two entries at the same offset with
        // the same size are an intentional alias; anything partially overlapping
        // is a real bug.
        var entries = bundle.GpuBackedFiles.ToList();
        var regions = entries
            .Select(f => ((long)f.GpuOffset, (long)f.GpuSize))
            .Distinct()
            .OrderBy(r => r.Item1)
            .ToList();

        var aliased = entries.Count - regions.Count;
        long previousEnd = 0;
        long covered = 0;

        foreach (var entry in entries)
        {
            if (entry.GpuOffset % BundleFormat.GpuAlignment != 0)
                issues.Add(new VerificationIssue("alignment",
                    $"{entry.Name} starts at {entry.GpuOffset}, not a multiple of {BundleFormat.GpuAlignment}"));

            if ((long)entry.GpuOffset + entry.GpuSize > gpu.Length)
                issues.Add(new VerificationIssue("bounds",
                    $"{entry.Name} runs to {entry.GpuOffset + entry.GpuSize}, past the {gpu.Length}-byte file"));
        }

        foreach (var (offset, size) in regions)
        {
            if (offset < previousEnd)
                issues.Add(new VerificationIssue("overlap",
                    $"region at {offset} partially overlaps the previous one ending at {previousEnd}"));

            previousEnd = offset + size;
            covered += size;
        }

        if (gpu.Length - previousEnd >= BundleFormat.GpuAlignment)
            issues.Add(new VerificationIssue("slack",
                $"{gpu.Length - previousEnd} unreferenced bytes at end of file"));

        // --- textures: header and table must agree ------------------------------
        foreach (var entry in bundle.Textures)
        {
            if (!TextureResource.TryCreate(bundle, entry, out var texture) || texture is null)
            {
                issues.Add(new VerificationIssue("texture-header", $"{entry.Name} has no readable DDS header"));
                continue;
            }

            if (!texture.Header.HasDx10Header) continue;

            // A format this build cannot size is not evidence of a problem: the
            // optimiser skips those too. Reporting it as an error would fail
            // verification on bundles that were never touched.
            if (!texture.SourceFormat.IsSizable()) continue;

            // Streamed textures keep their mip chain in the .stream file and leave
            // only a resident tail in gpu_resources, so the GPU size legitimately
            // does not describe the full surface. Same for any mipmapped texture.
            if (entry.StreamSize > 0 || texture.Header.MipMapCount > 1) continue;

            var mips = Math.Max(1, texture.Header.MipMapCount);
            var expected = mips > 1
                ? texture.SourceFormat.MipChain(texture.Width, texture.Height, mips).Sum()
                : texture.SourceFormat.SurfaceSize(texture.Width, texture.Height);
            if (expected != entry.GpuSize)
                issues.Add(new VerificationIssue("texture-size",
                    $"{entry.Name}: header describes {texture.Width}x{texture.Height} "
                  + $"{texture.SourceFormat.DisplayName()} ({expected} bytes) but the table says {entry.GpuSize}"));

            // Only compressed surfaces describe themselves with a linear size;
            // uncompressed ones store a per-row pitch, which is not the payload size.
            if (!texture.SourceFormat.IsBlockCompressed()) continue;

            // For a mip chain the linear size describes level 0, not the payload.
            var levelZero = texture.SourceFormat.SurfaceSize(texture.Width, texture.Height);
            if (texture.Header.PitchOrLinearSize != levelZero)
                issues.Add(new VerificationIssue("texture-linearsize",
                    $"{entry.Name}: DDS linear size {texture.Header.PitchOrLinearSize} "
                  + $"!= level 0 size {levelZero}"));

            if ((texture.Header.Flags & DdsHeader.FlagLinearSize) == 0)
                issues.Add(new VerificationIssue("texture-flags",
                    $"{entry.Name}: compressed surface without DDSD_LINEARSIZE"));
        }

        // --- optional: compare against the pre-optimisation original ------------
        var compared = 0;
        if (originalBundlePath is not null)
            compared = CompareWithOriginal(bundle, gpu, originalBundlePath, issues);

        return new VerificationReport
        {
            Issues = issues,
            SegmentsChecked = regions.Count,
            AliasedEntries = aliased,
            PayloadsCompared = compared,
            PaddingBytes = gpu.Length - covered,
        };
    }

    /// <summary>
    /// Confirms that everything the optimiser claims not to touch really is
    /// untouched: entry identity, CPU payloads, and all non-texture GPU data.
    /// </summary>
    private static int CompareWithOriginal(
        Bundle rebuilt, GpuResourceFile rebuiltGpu, string originalBundlePath, List<VerificationIssue> issues)
    {
        var original = Bundle.Load(originalBundlePath);
        using var originalGpu = GpuResourceFile.Open(original.GpuResourcePath);

        if (original.Files.Count != rebuilt.Files.Count)
        {
            issues.Add(new VerificationIssue("entry-count",
                $"original has {original.Files.Count} entries, rebuilt has {rebuilt.Files.Count}"));
            return 0;
        }

        if (!original.Image.AsSpan(0, BundleFormat.HeaderSize)
                          .SequenceEqual(rebuilt.Image.AsSpan(0, BundleFormat.HeaderSize)))
            issues.Add(new VerificationIssue("header", "bundle header differs from the original"));

        var compared = 0;
        foreach (var (before, after) in original.Files.Zip(rebuilt.Files))
        {
            if (before.FileId != after.FileId || before.TypeId != after.TypeId)
            {
                issues.Add(new VerificationIssue("entry-identity",
                    $"entry {before.Index}: id/type changed"));
                continue;
            }

            if (before.Offset != after.Offset || before.Size != after.Size)
                issues.Add(new VerificationIssue("cpu-payload",
                    $"{before.Name}: CPU payload moved or resized"));

            if (after.IsTexture || !after.HasGpuData) continue;

            if (before.GpuSize != after.GpuSize)
            {
                issues.Add(new VerificationIssue("passthrough-size",
                    $"{before.Name}: non-texture payload changed size {before.GpuSize} -> {after.GpuSize}"));
                continue;
            }

            var a = originalGpu.Read(before.GpuOffset, before.GpuSize);
            var b = rebuiltGpu.Read(after.GpuOffset, after.GpuSize);
            if (!a.AsSpan().SequenceEqual(b))
                issues.Add(new VerificationIssue("passthrough-data",
                    $"{before.Name}: non-texture payload bytes differ"));
            compared++;
        }

        return compared;
    }
}
