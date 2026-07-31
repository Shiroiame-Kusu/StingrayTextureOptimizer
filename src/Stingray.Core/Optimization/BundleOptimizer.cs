// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
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
        CancellationToken cancellationToken = default)
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
        var encodeCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        for (var i = 0; i < included.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = included[i];
            progress?.Report(new ApplyProgress(i, included.Count, "Encoding", item.Texture.Name));

            var entry = item.Texture.Entry;
            var surface = sourceGpu.Read(entry.GpuOffset, entry.GpuSize);
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

        // 3. Repoint the table and rewrite the DDS headers.
        foreach (var entry in ordered)
        {
            var (offset, size) = newOffsets[entry.FileId];
            entry.WriteGpuFields(image, offset, size);
        }

        foreach (var item in included)
        {
            var entry = item.Texture.Entry;
            var payload = image.AsSpan((int)entry.Offset, (int)entry.Size);
            item.Texture.Header.Patch(
                payload, item.TargetWidth, item.TargetHeight, item.TargetFormat,
                newOffsets[entry.FileId].Size);
        }

        // 4. Commit both files.
        var bundleTemp = outputBundlePath + ".tmp";
        File.WriteAllBytes(bundleTemp, image);
        File.Move(bundleTemp, outputBundlePath, overwrite: true);
        File.Move(gpuTemp, outputGpuPath, overwrite: true);

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
