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

        using var stream = bundle.HasStreamFile && new FileInfo(bundle.StreamPath).Length > 0
            ? GpuResourceFile.Open(bundle.StreamPath)
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

            // A streamed texture describes itself in the Stingray prefix rather
            // than through gpuSize alone, so it gets its own check.
            if (entry.StreamSize > 0)
            {
                VerifyStreamed(bundle, entry, texture, stream, issues);
                continue;
            }

            // A resident mip chain costs the sum of every level, not just level 0.
            if (texture.Header.MipMapCount > 1)
            {
                var chain = texture.SourceFormat
                    .MipChain(texture.Width, texture.Height, texture.Header.MipMapCount).Sum();
                if (chain != entry.GpuSize)
                    issues.Add(new VerificationIssue("texture-size",
                        $"{entry.Name}: header describes a {texture.Header.MipMapCount}-level chain "
                      + $"of {chain} bytes but the table says {entry.GpuSize}"));
                continue;
            }

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
    /// Checks that a streamed texture's Stingray prefix agrees with its DDS header
    /// and with the two payloads it points at.
    /// </summary>
    /// <remarks>
    /// The level table is what the engine uses to find each mip inside
    /// <c>.stream</c>. If it describes different dimensions from the DDS header the
    /// bundle still looks structurally sound — sizes line up, nothing overlaps —
    /// but every level is read from the wrong offset and the texture renders as
    /// garbage. That is worth catching here rather than in game, so the expected
    /// values are recomputed from the DDS header rather than taken from the writer.
    /// </remarks>
    private static void VerifyStreamed(
        Bundle bundle, BundleFileEntry entry, TextureResource texture,
        GpuResourceFile? stream, List<VerificationIssue> issues)
    {
        var payload = bundle.GetCpuPayload(entry);
        var prefix = texture.Header.Offset;
        var mips = Math.Max(1, texture.Header.MipMapCount);

        var first = texture.FirstResidentMip;
        if (first == TextureResource.NotStreamed || first >= (uint)mips)
        {
            issues.Add(new VerificationIssue("stream-resident",
                $"{entry.Name}: streamed but the first resident level is {(int)first}, "
              + $"which is not within a {mips}-level chain"));
            return;
        }

        var chain = texture.SourceFormat.MipChain(texture.Width, texture.Height, mips);
        var total = chain.Sum();

        if (entry.StreamSize != total)
            issues.Add(new VerificationIssue("stream-size",
                $"{entry.Name}: .stream holds {entry.StreamSize} bytes but the header "
              + $"describes a {mips}-level chain of {total}"));

        var resident = chain.Skip((int)first).Sum();
        if (entry.GpuSize != resident)
            issues.Add(new VerificationIssue("stream-resident-size",
                $"{entry.Name}: {entry.GpuSize} bytes resident but levels {first}.. "
              + $"come to {resident}"));

        if (entry.StreamOffset % BundleFormat.GpuAlignment != 0)
            issues.Add(new VerificationIssue("stream-alignment",
                $"{entry.Name}: stream offset {entry.StreamOffset} is not a multiple "
              + $"of {BundleFormat.GpuAlignment}"));

        if (stream is null)
            issues.Add(new VerificationIssue("missing-file",
                $"{entry.Name} is streamed but {Path.GetFileName(bundle.StreamPath)} does not exist"));
        else if ((long)entry.StreamOffset + entry.StreamSize > stream.Length)
            issues.Add(new VerificationIssue("stream-bounds",
                $"{entry.Name}: stream region runs to {entry.StreamOffset + entry.StreamSize}, "
              + $"past the {stream.Length}-byte file"));

        if (prefix < StingrayTexturePrefix.LevelTableOffset + mips * StingrayTexturePrefix.LevelEntrySize)
        {
            issues.Add(new VerificationIssue("stream-table",
                $"{entry.Name}: a {mips}-level table does not fit in a {prefix}-byte prefix"));
            return;
        }

        var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            payload[StingrayTexturePrefix.ChainSizeOffset..]);
        if (declared != total)
            issues.Add(new VerificationIssue("stream-table",
                $"{entry.Name}: the prefix declares a {declared}-byte chain but the header "
              + $"describes {total}"));

        long running = 0;
        for (var level = 0; level < mips; level++)
        {
            var at = StingrayTexturePrefix.LevelTableOffset + level * StingrayTexturePrefix.LevelEntrySize;
            var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[at..]);
            var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[(at + 2)..]);
            var next = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 4)..]);
            var rest = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 8)..]);

            var expectedW = Math.Max(1, texture.Width >> level);
            var expectedH = Math.Max(1, texture.Height >> level);
            if (w != expectedW || h != expectedH)
            {
                issues.Add(new VerificationIssue("stream-table",
                    $"{entry.Name}: level {level} of the prefix table says {w}x{h}, but the "
                  + $"DDS header implies {expectedW}x{expectedH} — the engine would read every "
                  + "level from the wrong offset"));
                return;
            }

            running += chain[level];
            var expectedNext = level == mips - 1 ? 0 : running;
            var expectedRest = level == mips - 1 ? 0 : total - running;
            if (next != expectedNext || rest != expectedRest)
            {
                issues.Add(new VerificationIssue("stream-table",
                    $"{entry.Name}: level {level} offsets are ({next}, {rest}), expected "
                  + $"({expectedNext}, {expectedRest})"));
                return;
            }
        }
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
