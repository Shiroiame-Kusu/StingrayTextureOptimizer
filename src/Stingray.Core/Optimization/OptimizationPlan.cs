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

    private DxgiFormat _targetFormat;
    private int _targetWidth;
    private int _targetHeight;

    /// <summary>
    /// Format the payload is written in. Fixed for a sliced chain: those bytes
    /// are the author's own, still in the format they were encoded in, so the
    /// header must say so however the caller sets this.
    /// </summary>
    public DxgiFormat TargetFormat
    {
        get => IsMipDrop ? Texture.SourceFormat : _targetFormat;
        set => _targetFormat = value;
    }

    /// <summary>Width of the surviving level 0. Derived for a sliced chain.</summary>
    public int TargetWidth
    {
        get => IsMipDrop ? Math.Max(1, Texture.Width >> MipLevelsToDrop) : _targetWidth;
        set => _targetWidth = value;
    }

    /// <summary>Height of the surviving level 0. Derived for a sliced chain.</summary>
    public int TargetHeight
    {
        get => IsMipDrop ? Math.Max(1, Texture.Height >> MipLevelsToDrop) : _targetHeight;
        set => _targetHeight = value;
    }

    /// <summary>
    /// Top mip levels to discard. Non-zero means the payload is sliced rather
    /// than re-encoded: the levels that remain are the author's own data, so
    /// nothing is recompressed and no generation loss is introduced.
    /// </summary>
    public int MipLevelsToDrop { get; set; }

    /// <summary>Levels remaining after the slice. 1 means the chain is gone.</summary>
    public int TargetMipCount { get; set; } = 1;

    /// <summary>
    /// First level to keep resident once the texture is converted to a streamed
    /// one, or null to leave its residency alone.
    /// </summary>
    public int? StreamResidentMip { get; set; }

    /// <summary>
    /// True when this texture is being moved into <c>.stream</c>. Nothing is
    /// dropped or re-encoded: the full chain is written to the stream file and
    /// only the tail from <see cref="StreamResidentMip"/> stays in video memory.
    /// </summary>
    public bool IsStreamConversion => StreamResidentMip is not null;

    /// <summary>
    /// True when a mip chain is being built for a texture that shipped without
    /// one. These levels are generated rather than sliced, so the whole texture is
    /// re-encoded and the target describes it, not the source.
    /// </summary>
    public bool GeneratesMips { get; set; }

    /// <summary>The chain this texture ends up with, generated or sliced.</summary>
    private long[] TargetChain => TargetFormat.MipChain(TargetWidth, TargetHeight, TargetMipCount);

    /// <summary>
    /// Bytes this texture will occupy in <c>.stream</c>: everything that survives
    /// the size cap, which is what the engine can load back on demand.
    /// </summary>
    public long PredictedStreamSize =>
        !IsStreamConversion ? 0
        : GeneratesMips ? TargetChain.Sum()
        : Texture.MipChain.Skip(MipLevelsToDrop).Sum();

    /// <summary>
    /// True when the payload is sliced out of the existing chain rather than
    /// re-encoded. Any mipmapped texture takes this path: re-encoding one would
    /// mean rebuilding the whole chain, and treating its payload as a single
    /// surface would silently discard every level below the first.
    /// </summary>
    public bool IsMipDrop => Texture.MipMapCount > 1;

    /// <summary>
    /// Whether the target format is a free choice. It is not for a sliced chain:
    /// changing it there would rewrite the header to describe a format the
    /// payload is not in, so the choice is refused rather than half-applied.
    /// </summary>
    public bool SupportsFormatChange => !IsMipDrop;

    public long CurrentSize => Texture.GpuSize;

    public long PredictedSize
    {
        get
        {
            if (!Include) return CurrentSize;

            // A generated chain does not exist in the source, so its cost has to
            // come from the target rather than from what is already there.
            if (GeneratesMips)
                return IsStreamConversion
                    ? TargetChain.Skip(StreamResidentMip!.Value).Sum()
                    : TargetChain.Sum();

            // A streamed texture keeps only its tail resident. Levels above the
            // size cap are gone for good; the rest are still there, just in
            // .stream rather than in video memory.
            if (IsStreamConversion)
                return Texture.MipChain.Skip(MipLevelsToDrop + StreamResidentMip!.Value).Sum();

            if (!IsMipDrop) return TargetFormat.SurfaceSize(TargetWidth, TargetHeight);

            // A mipmapped texture costs the sum of the levels that survive, not
            // the size of its largest one.
            return Texture.MipChain.Skip(MipLevelsToDrop).Take(TargetMipCount).Sum();
        }
    }
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
        MipLevelsToDrop = Recommendation.MipLevelsToDrop;
        TargetMipCount = Recommendation.TargetMipCount;
        StreamResidentMip = Recommendation.StreamResidentMip;
        GeneratesMips = Recommendation.GeneratesMips;
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
    /// Groups of entries whose payloads are byte-identical, with how many distinct
    /// regions each group currently occupies.
    /// </summary>
    /// <remarks>
    /// Entries that repeat content but already point at one shared region cost
    /// nothing, which is exactly what an already-optimised bundle looks like.
    /// Counting those as waste would report a saving that repacking cannot deliver.
    /// </remarks>
    private IEnumerable<(int DistinctRegions, uint Size)> DuplicateGroups =>
        Bundle.GpuBackedFiles
            .Where(e => _contentHashes.ContainsKey(e.FileId))
            .GroupBy(e => _contentHashes[e.FileId], StringComparer.Ordinal)
            .Select(g => (g.Select(e => e.GpuOffset).Distinct().Count(), g.First().GpuSize))
            .Where(g => g.Item1 > 1);

    /// <summary>Bytes repacking would actually reclaim by sharing payloads.</summary>
    public long RedundantBytes =>
        DuplicateGroups.Sum(g => (long)(g.DistinctRegions - 1) * g.Size);

    /// <summary>Entries that would stop occupying a region of their own.</summary>
    public int DuplicateEntryCount => DuplicateGroups.Sum(g => g.DistinctRegions - 1);

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

    /// <summary>
    /// Bytes the GPU must allocate today. Each entry becomes its own resource, so
    /// a payload shared by several entries is counted once per entry — sharing
    /// shrinks the file, not video memory.
    /// </summary>
    public long CurrentGpuFootprint => Bundle.GpuBackedFiles.Sum(f => (long)f.GpuSize);

    /// <summary>
    /// Projected GPU allocation. Unlike <see cref="PredictedGpuSize"/> this ignores
    /// deduplication, because only shrinking a surface — compressing, collapsing or
    /// resizing it — reduces what the GPU has to hold.
    /// </summary>
    public long PredictedGpuFootprint
    {
        get
        {
            var byId = Textures.ToDictionary(t => t.Texture.Entry.FileId);
            return Bundle.GpuBackedFiles.Sum(e =>
                byId.TryGetValue(e.FileId, out var item) && item.Include
                    ? item.PredictedSize
                    : (long)e.GpuSize);
        }
    }

    public long PredictedFootprintSaving => CurrentGpuFootprint - PredictedGpuFootprint;

    /// <summary>Textures being given a mip chain they did not have.</summary>
    public int GeneratedChainCount => Textures.Count(t => t.Include && t.GeneratesMips);

    /// <summary>Textures being moved into <c>.stream</c>.</summary>
    public int StreamConversionCount => Textures.Count(t => t.Include && t.IsStreamConversion);

    /// <summary>
    /// Bytes the conversions add to <c>.stream</c>. This is what streaming costs:
    /// the saving is in video memory, and it is paid for on disk.
    /// </summary>
    public long AddedStreamBytes =>
        Textures.Where(t => t.Include && t.IsStreamConversion).Sum(t => t.PredictedStreamSize);

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
        MipMode mipMode = MipMode.KeepChain,
        int streamFloor = 0,
        bool generateMips = false,
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
                // Only level 0 is analysed; the rest of the chain is derived from it.
                var levelZero = (uint)texture.SourceFormat.SurfaceSize(texture.Width, texture.Height);
                var surface = gpu.Read(entry.GpuOffset, Math.Min(levelZero, entry.GpuSize));
                var rgba = TextureDecoder.ToRgba(surface, texture.Width, texture.Height, texture.SourceFormat);
                analysis = TextureAnalyzer.Analyze(rgba, texture.Width, texture.Height);
                analysisCache[key] = analysis;
            }

            var recommendation = TextureAnalyzer.Recommend(
                analysis, texture.Width, texture.Height, strategy, collapseSolidColours,
                maxDimension, texture.SourceFormat, texture.MipMapCount, mipMode,
                streamFloor, texture.Header.Offset, generateMips);

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
