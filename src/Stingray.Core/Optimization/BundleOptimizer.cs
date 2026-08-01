// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Textures;

namespace Stingray.Core.Optimization;

public readonly record struct ApplyProgress(int Completed, int Total, string Stage, string Detail);

public sealed class OptimizationResult
{
    public required long OriginalGpuSize { get; init; }
    public required long NewGpuSize { get; init; }
    public required int TexturesEncoded { get; init; }

    /// <summary>Entries repointed at a payload written for an earlier entry.</summary>
    public int PayloadsDeduplicated { get; init; }

    /// <summary>Bytes saved by not writing those duplicates.</summary>
    public long DeduplicatedBytes { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public long Saved => OriginalGpuSize - NewGpuSize;
    public double Ratio => OriginalGpuSize == 0 ? 1 : (double)NewGpuSize / OriginalGpuSize;
}

/// <summary>Applies an <see cref="OptimizationPlan"/>, producing a rewritten bundle pair.</summary>
/// <remarks>
/// Only DDS header fields and the per-entry GPU offset/size are rewritten. CPU
/// payload lengths are untouched, so every <c>Offset</c> in the file table stays
/// correct and non-texture assets are copied through byte for byte.
/// </remarks>
public static class BundleOptimizer
{
    public static OptimizationResult Apply(
        OptimizationPlan plan,
        GpuResourceFile sourceGpu,
        string outputBundlePath,
        string outputGpuPath,
        EncodeOptions? encodeOptions = null,
        IProgress<ApplyProgress>? progress = null,
        bool deduplicate = true,
        CancellationToken cancellationToken = default,
        string? outputStreamPath = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceGpu);

        var stopwatch = Stopwatch.StartNew();
        var image = (byte[])plan.Bundle.Image.Clone();
        var included = plan.Textures.Where(t => t.Include).ToList();

        // 1. Encode every selected texture up front so a failure aborts before
        //    anything is written to disk. Identical sources with identical targets
        //    produce identical output, so each combination is encoded once however
        //    many entries share it.
        var encoded = new Dictionary<ulong, byte[]>();
        var streamPayloads = new Dictionary<ulong, byte[]>();
        var encodeCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        for (var i = 0; i < included.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = included[i];
            progress?.Report(new ApplyProgress(i, included.Count, "Encoding", item.Texture.Name));

            var entry = item.Texture.Entry;
            var surface = sourceGpu.Read(entry.GpuOffset, entry.GpuSize);

            // Building a chain the texture never had is the one path here that
            // produces levels rather than moving them, so it is an encode all the
            // way down. When it is also streamed, the whole new chain goes to
            // .stream and the tail below the resident level stays behind.
            if (item.GeneratesMips)
            {
                var built = TextureEncoder.EncodeChain(
                    surface,
                    item.Texture.Width, item.Texture.Height, item.Texture.SourceFormat,
                    item.TargetWidth, item.TargetHeight, item.TargetFormat,
                    item.TargetMipCount, encodeOptions);

                if (item.IsStreamConversion)
                {
                    var levels = item.TargetFormat.MipChain(
                        item.TargetWidth, item.TargetHeight, item.TargetMipCount);
                    var keepResident = (int)levels.Take(item.StreamResidentMip!.Value).Sum();
                    streamPayloads[entry.FileId] = built;
                    encoded[entry.FileId] = built.AsSpan(keepResident).ToArray();
                }
                else
                {
                    encoded[entry.FileId] = built;
                }

                continue;
            }

            // Converting an existing chain to a streamed one discards nothing: the
            // levels above the size cap are dropped outright, what is left goes to
            // .stream, and the tail below the floor also stays resident. Both are
            // slices of what is already there.
            if (item.IsStreamConversion)
            {
                var chain = item.Texture.MipChain;
                var dropped = (int)chain.Take(item.MipLevelsToDrop).Sum();
                var untilResident = (int)chain
                    .Take(item.MipLevelsToDrop + item.StreamResidentMip!.Value).Sum();

                streamPayloads[entry.FileId] = surface.AsSpan(dropped).ToArray();
                encoded[entry.FileId] = surface.AsSpan(untilResident).ToArray();
                continue;
            }

            // Dropping mip levels is a slice, not an encode: skip past the levels
            // being discarded and keep the rest byte for byte.
            if (item.IsMipDrop)
            {
                var chain = item.Texture.MipChain;
                var skip = chain.Take(item.MipLevelsToDrop).Sum();
                var keep = chain.Skip(item.MipLevelsToDrop).Take(item.TargetMipCount).Sum();
                encoded[entry.FileId] = surface.AsSpan((int)skip, (int)keep).ToArray();
                continue;
            }

            var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(surface))
                         + $":{item.TargetFormat}:{item.TargetWidth}x{item.TargetHeight}";

            if (!encodeCache.TryGetValue(cacheKey, out var payload))
            {
                payload = TextureEncoder.Encode(
                    surface,
                    item.Texture.Width, item.Texture.Height, item.Texture.SourceFormat,
                    item.TargetWidth, item.TargetHeight, item.TargetFormat,
                    encodeOptions);
                encodeCache[cacheKey] = payload;
            }

            encoded[entry.FileId] = payload;
        }

        // 2. Rebuild the GPU file in the original payload order, writing each
        //    distinct payload once. Mods frequently ship the same texture under
        //    many ids; the format stores an explicit (offset, size) per entry, so
        //    several entries can point at one payload.
        var ordered = plan.Bundle.GpuBackedFiles.OrderBy(f => f.GpuOffset).ToList();
        var newOffsets = new Dictionary<ulong, (ulong Offset, uint Size)>();
        var written = new Dictionary<string, (ulong Offset, uint Size)>(StringComparer.Ordinal);
        var deduplicated = 0;
        long deduplicatedBytes = 0;
        var gpuTemp = outputGpuPath + ".tmp";

        using (var output = new FileStream(gpuTemp, FileMode.Create, FileAccess.Write,
                                           FileShare.None, 1 << 20))
        {
            var padding = new byte[BundleFormat.GpuAlignment];
            long cursor = 0;

            for (var i = 0; i < ordered.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = ordered[i];
                progress?.Report(new ApplyProgress(i, ordered.Count, "Writing", entry.Name));

                encoded.TryGetValue(entry.FileId, out var payload);
                var size = payload is not null ? (uint)payload.Length : entry.GpuSize;

                string? key = null;
                if (deduplicate)
                {
                    var hash = payload is not null
                        ? System.Security.Cryptography.SHA256.HashData(payload)
                        : sourceGpu.Hash(entry.GpuOffset, entry.GpuSize);
                    key = Convert.ToHexString(hash);

                    if (written.TryGetValue(key, out var existing) && existing.Size == size)
                    {
                        newOffsets[entry.FileId] = existing;
                        deduplicated++;
                        deduplicatedBytes += size;
                        continue;
                    }
                }

                var aligned = OptimizationPlan.Align(cursor);
                if (aligned != cursor)
                {
                    output.Write(padding, 0, (int)(aligned - cursor));
                    cursor = aligned;
                }

                if (payload is not null) output.Write(payload, 0, payload.Length);
                else sourceGpu.CopyTo(entry.GpuOffset, entry.GpuSize, output);

                var placement = ((ulong)cursor, size);
                newOffsets[entry.FileId] = placement;
                if (key is not null) written[key] = placement;
                cursor += size;
            }
        }

        // 3. Rebuild .stream, but only when something is actually being converted.
        //    With the feature off this is skipped entirely and the existing stream
        //    file — along with every offset pointing into it — is left untouched.
        var streamTemp = (string?)null;
        var streamPath = outputStreamPath ?? outputBundlePath + ".stream";
        if (streamPayloads.Count > 0)
        {
            streamTemp = streamPath + ".tmp";
            WriteStreamFile(plan, sourceGpu, streamPayloads, streamTemp, image,
                            deduplicate, progress, cancellationToken);
        }
        else if (!PathsMatch(streamPath, plan.Bundle.StreamPath) && plan.Bundle.HasStreamFile)
        {
            // Writing elsewhere and not touching the stream data: it still has to
            // travel with the bundle, because the table keeps pointing into it.
            // Without this the output would be missing every streamed payload.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(streamPath))!);
            File.Copy(plan.Bundle.StreamPath, streamPath, overwrite: true);
        }

        // 4. Repoint the table and rewrite the DDS headers.
        foreach (var entry in ordered)
        {
            var (offset, size) = newOffsets[entry.FileId];
            entry.WriteGpuFields(image, offset, size);
        }

        foreach (var item in included)
        {
            var entry = item.Texture.Entry;
            var payload = image.AsSpan((int)entry.Offset, (int)entry.Size);

            // A converted texture keeps its format. Its dimensions and level count
            // shrink only by whatever the size cap discarded; the Stingray prefix
            // then describes what remains and where residency starts within it.
            if (item.IsStreamConversion)
            {
                item.Texture.Header.Patch(
                    payload, item.TargetWidth, item.TargetHeight, item.TargetFormat,
                    surfaceSize: 0, item.TargetMipCount);
                StingrayTexturePrefix.PatchDdsForStreaming(
                    payload, item.Texture.Header, item.TargetFormat, item.TargetWidth);
                StingrayTexturePrefix.WriteStreaming(
                    payload, item.Texture.Header.Offset, item.TargetFormat,
                    item.TargetWidth, item.TargetHeight,
                    item.TargetMipCount, item.StreamResidentMip!.Value);
                continue;
            }

            // Whenever the payload is a chain — sliced or newly built — the DDS
            // "linear size" describes level 0 rather than the whole payload, and
            // the level count has to say how many there are.
            var describesChain = item.IsMipDrop || item.GeneratesMips;
            var mips = describesChain ? item.TargetMipCount : (int?)null;
            var linearSize = describesChain
                ? item.TargetFormat.SurfaceSize(item.TargetWidth, item.TargetHeight)
                : newOffsets[entry.FileId].Size;

            item.Texture.Header.Patch(
                payload, item.TargetWidth, item.TargetHeight, item.TargetFormat,
                linearSize, mips);
        }

        // 5. Commit. Each rename is atomic but the set is not, so the stream file
        //    lands before the table that points into it: a bundle referring to
        //    stream data that is already there is recoverable, the reverse is not.
        var bundleTemp = outputBundlePath + ".tmp";
        File.WriteAllBytes(bundleTemp, image);
        if (streamTemp is not null) File.Move(streamTemp, streamPath, overwrite: true);
        File.Move(gpuTemp, outputGpuPath, overwrite: true);
        File.Move(bundleTemp, outputBundlePath, overwrite: true);

        stopwatch.Stop();
        return new OptimizationResult
        {
            OriginalGpuSize = plan.CurrentGpuSize,
            NewGpuSize = new FileInfo(outputGpuPath).Length,
            TexturesEncoded = included.Count,
            PayloadsDeduplicated = deduplicated,
            DeduplicatedBytes = deduplicatedBytes,
            Elapsed = stopwatch.Elapsed,
        };
    }

    private static bool PathsMatch(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                                                  : StringComparison.Ordinal);

    /// <summary>
    /// Writes the <c>.stream</c> file, carrying existing streamed payloads through
    /// and appending the newly converted ones, then repoints every stream field.
    /// </summary>
    /// <remarks>
    /// Packed exactly like <c>.gpu_resources</c>: 64-byte aligned, in file-table
    /// order, ending at the last payload with no slack.
    /// </remarks>
    private static void WriteStreamFile(
        OptimizationPlan plan,
        GpuResourceFile sourceGpu,
        Dictionary<ulong, byte[]> streamPayloads,
        string streamTemp,
        byte[] image,
        bool deduplicate,
        IProgress<ApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bundle = plan.Bundle;

        // Entries that end up with stream data: those that already had some, plus
        // everything being converted now.
        var targets = bundle.Files
            .Where(f => f.HasStreamData || streamPayloads.ContainsKey(f.FileId))
            .ToList();

        // Only opened when something already lives in .stream and has to be
        // carried across; a bundle with no stream file at all is the common case.
        var carriesExisting = targets.Any(f => f.HasStreamData && !streamPayloads.ContainsKey(f.FileId));
        using var existing = carriesExisting && bundle.HasStreamFile
            ? GpuResourceFile.Open(bundle.StreamPath)
            : null;

        if (carriesExisting && existing is null)
            throw new InvalidDataException(
                $"{Path.GetFileName(bundle.StreamPath)} is missing, but the bundle "
              + "declares streamed payloads that have to be preserved.");

        var placements = new Dictionary<ulong, (ulong Offset, uint Size)>();
        var written = new Dictionary<string, (ulong Offset, uint Size)>(StringComparer.Ordinal);

        using (var output = new FileStream(streamTemp, FileMode.Create, FileAccess.Write,
                                           FileShare.None, 1 << 20))
        {
            var padding = new byte[BundleFormat.GpuAlignment];
            long cursor = 0;

            for (var i = 0; i < targets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = targets[i];
                progress?.Report(new ApplyProgress(i, targets.Count, "Streaming", entry.Name));

                streamPayloads.TryGetValue(entry.FileId, out var payload);
                var size = payload is not null ? (uint)payload.Length : entry.StreamSize;

                string? key = null;
                if (deduplicate)
                {
                    var hash = payload is not null
                        ? System.Security.Cryptography.SHA256.HashData(payload)
                        : existing!.Hash(entry.StreamOffset, entry.StreamSize);
                    key = Convert.ToHexString(hash);

                    if (written.TryGetValue(key, out var already) && already.Size == size)
                    {
                        placements[entry.FileId] = already;
                        continue;
                    }
                }

                var aligned = OptimizationPlan.Align(cursor);
                if (aligned != cursor)
                {
                    output.Write(padding, 0, (int)(aligned - cursor));
                    cursor = aligned;
                }

                if (payload is not null) output.Write(payload, 0, payload.Length);
                else existing!.CopyTo(entry.StreamOffset, entry.StreamSize, output);

                var placement = ((ulong)cursor, size);
                placements[entry.FileId] = placement;
                if (key is not null) written[key] = placement;
                cursor += size;
            }
        }

        foreach (var entry in targets)
        {
            var (offset, size) = placements[entry.FileId];
            entry.WriteStreamFields(image, offset, size);
        }
    }

    /// <summary>
    /// Copies a bundle and its companions to <paramref name="backupDirectory"/>,
    /// refusing to overwrite an existing backup.
    /// </summary>
    public static IReadOnlyList<string> CreateBackup(Bundle bundle, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var created = new List<string>();

        foreach (var source in new[] { bundle.Path, bundle.GpuResourcePath, bundle.StreamPath })
        {
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(backupDirectory, Path.GetFileName(source));
            if (File.Exists(destination))
                throw new IOException($"Backup already exists: {destination}. Move or delete it first.");
            File.Copy(source, destination);
            created.Add(destination);
        }

        return created;
    }
}
