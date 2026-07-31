// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Textures;

namespace Stingray.Core.Optimization;

/// <summary>One texture's proposed treatment. Editable before the plan is applied.</summary>
public sealed class TexturePlanItem
{
    public required TextureResource Texture { get; init; }
    public required TextureAnalysis Analysis { get; init; }
    public required FormatRecommendation Recommendation { get; init; }

    /// <summary>Whether this texture is rewritten when the plan is applied.</summary>
    public bool Include { get; set; } = true;

    public DxgiFormat TargetFormat { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }

    public long CurrentSize => Texture.GpuSize;
    public long PredictedSize => Include ? TargetFormat.SurfaceSize(TargetWidth, TargetHeight) : CurrentSize;
    public long Saving => CurrentSize - PredictedSize;

    /// <summary>True while the item still matches what the analyser suggested.</summary>
    public bool IsRecommended =>
        TargetFormat == Recommendation.Format
        && TargetWidth == Recommendation.Width
        && TargetHeight == Recommendation.Height;

    public void ResetToRecommendation()
    {
        TargetFormat = Recommendation.Format;
        TargetWidth = Recommendation.Width;
        TargetHeight = Recommendation.Height;
        Include = true;
    }
}

/// <summary>A texture the tool declined to touch, with the reason why.</summary>
public sealed record SkippedTexture(string Name, string Description, string Reason)
{
    public static SkippedTexture From(TextureResource texture, string reason) =>
        new(texture.Name,
            $"{texture.Width}x{texture.Height} {texture.SourceFormat.DisplayName()}",
            reason);

    public static SkippedTexture From(BundleFileEntry entry, string reason) =>
        new(entry.Name, $"{entry.GpuSize:N0} bytes", reason);
}

/// <summary>The full set of decisions for one bundle.</summary>
public sealed class OptimizationPlan
{
    private readonly Dictionary<ulong, string> _contentHashes;

    private OptimizationPlan(Bundle bundle, IReadOnlyList<TexturePlanItem> textures,
                             IReadOnlyList<SkippedTexture> skipped, long currentGpuSize,
                             Dictionary<ulong, string> contentHashes)
    {
        Bundle = bundle;
        Textures = textures;
        Skipped = skipped;
        CurrentGpuSize = currentGpuSize;
        _contentHashes = contentHashes;
    }

    /// <summary>Whether repacking writes each distinct payload only once.</summary>
    public bool Deduplicate { get; set; } = true;

    /// <summary>
    /// Bytes currently occupied by payloads that are byte-identical to an earlier
    /// one. Mods often ship the same texture under many ids.
    /// </summary>
    public long RedundantBytes
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long redundant = 0;
            foreach (var entry in Bundle.GpuBackedFiles.OrderBy(f => f.GpuOffset))
                if (_contentHashes.TryGetValue(entry.FileId, out var hash) && !seen.Add(hash))
                    redundant += entry.GpuSize;
            return redundant;
        }
    }

    public int DuplicateEntryCount
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return Bundle.GpuBackedFiles
                .OrderBy(f => f.GpuOffset)
                .Count(e => _contentHashes.TryGetValue(e.FileId, out var h) && !seen.Add(h));
        }
    }

    public Bundle Bundle { get; }
    public IReadOnlyList<TexturePlanItem> Textures { get; }
    public IReadOnlyList<SkippedTexture> Skipped { get; }

    /// <summary>Size of the existing gpu_resources file.</summary>
    public long CurrentGpuSize { get; }

    /// <summary>
    /// Projected gpu_resources size, including inter-payload alignment padding so
    /// the number shown to the user matches what actually lands on disk.
    /// </summary>
    public long PredictedGpuSize
    {
        get
        {
            var byId = Textures.ToDictionary(t => t.Texture.Entry.FileId);
            var written = new HashSet<string>(StringComparer.Ordinal);
            long cursor = 0;

            foreach (var entry in Bundle.GpuBackedFiles.OrderBy(f => f.GpuOffset))
            {
                var included = byId.TryGetValue(entry.FileId, out var item) && item.Include;
                var size = included ? item!.PredictedSize : entry.GpuSize;

                if (Deduplicate && _contentHashes.TryGetValue(entry.FileId, out var hash))
                {
                    // Identical sources encoded to the same target produce identical
                    // output, so the encode settings form part of the identity.
                    var key = included
                        ? $"{hash}:{item!.TargetFormat}:{item.TargetWidth}x{item.TargetHeight}"
                        : hash;

                    if (!written.Add(key)) continue;
                }

                cursor = Align(cursor) + size;
            }

            return cursor;
        }
    }

    public long PredictedSaving => CurrentGpuSize - PredictedGpuSize;

    internal static long Align(long value)
    {
        var rem = value % BundleFormat.GpuAlignment;
        return rem == 0 ? value : value + (BundleFormat.GpuAlignment - rem);
    }

    /// <summary>
    /// Analyses every texture in the bundle and proposes a target format for each.
    /// </summary>
    public static OptimizationPlan Build(
        Bundle bundle,
        GpuResourceFile gpu,
        OptimizationStrategy strategy = OptimizationStrategy.Balanced,
        bool collapseSolidColours = true,
        IProgress<PlanProgress>? progress = null,
        int maxDimension = 0,
        CancellationToken cancellationToken = default)
    {
        var items = new List<TexturePlanItem>();
        var skipped = new List<SkippedTexture>();

        // Hash every GPU payload first. Both the size prediction and the repack use
        // this to decide what is shared, and it lets analysis run once per *distinct*
        // payload rather than once per entry — in real bundles that is 19 decodes
        // instead of 183.
        var hashes = new Dictionary<ulong, string>();
        foreach (var entry in bundle.GpuBackedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes[entry.FileId] = Convert.ToHexString(gpu.Hash(entry.GpuOffset, entry.GpuSize));
        }

        var analysisCache = new Dictionary<string, TextureAnalysis>(StringComparer.Ordinal);
        var candidates = bundle.Textures.ToList();

        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = candidates[i];

            if (!TextureResource.TryCreate(bundle, entry, out var texture) || texture is null)
            {
                skipped.Add(SkippedTexture.From(entry, "no readable DDS header"));
                continue;
            }

            progress?.Report(new PlanProgress(i + 1, candidates.Count, texture.Name));

            if (!texture.CanOptimize)
            {
                skipped.Add(SkippedTexture.From(texture, texture.Unsupported!));
                continue;
            }

            var key = hashes.TryGetValue(entry.FileId, out var hash) ? hash : entry.Name;
            if (!analysisCache.TryGetValue(key, out var analysis))
            {
                var surface = gpu.Read(entry.GpuOffset, entry.GpuSize);
                var rgba = TextureDecoder.ToRgba(surface, texture.Width, texture.Height, texture.SourceFormat);
                analysis = TextureAnalyzer.Analyze(rgba, texture.Width, texture.Height);
                analysisCache[key] = analysis;
            }

            var recommendation = TextureAnalyzer.Recommend(
                analysis, texture.Width, texture.Height, strategy, collapseSolidColours,
                maxDimension, texture.SourceFormat);

            var item = new TexturePlanItem
            {
                Texture = texture,
                Analysis = analysis,
                Recommendation = recommendation,
            };
            item.ResetToRecommendation();

            // A "saving" that is not a saving means leaving the texture alone.
            if (item.PredictedSize >= item.CurrentSize)
            {
                skipped.Add(SkippedTexture.From(texture,
                    $"already efficient ({recommendation.Format.DisplayName()} would not be smaller)"));
                continue;
            }

            items.Add(item);
        }

        return new OptimizationPlan(bundle, items, skipped, gpu.Length, hashes);
    }
}

public readonly record struct PlanProgress(int Completed, int Total, string CurrentTexture);
