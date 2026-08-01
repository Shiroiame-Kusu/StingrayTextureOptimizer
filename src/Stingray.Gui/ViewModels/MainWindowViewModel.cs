// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stingray.Core;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;

namespace Stingray.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private Bundle? _bundle;

    public ObservableCollection<TextureItemViewModel> Textures { get; } = [];
    public ObservableCollection<SkippedTexture> Skipped { get; } = [];

    public IReadOnlyList<StrategyChoice> Strategies { get; } =
    [
        new(OptimizationStrategy.Balanced, "Balanced (BC1 opaque, BC7 with alpha)"),
        new(OptimizationStrategy.MaximumQuality, "Best quality (BC7 always)"),
        new(OptimizationStrategy.SmallestSize, "Smallest size (BC1 wherever it fits)"),
    ];

    public IReadOnlyList<QualityChoice> Qualities { get; } =
    [
        new(EncodeQuality.Fast, "Fast (lowest quality)"),
        new(EncodeQuality.Balanced, "Balanced"),
        new(EncodeQuality.Best, "Best (slowest)"),
    ];

    public IReadOnlyList<MipModeChoice> MipModes { get; } =
    [
        new(MipMode.KeepChain, "Keep smaller levels"),
        new(MipMode.SingleLevel, "Keep one level only"),
    ];

    public IReadOnlyList<BackendChoice> Backends { get; } =
    [
        new(EncoderBackend.Auto, "Automatic"),
        new(EncoderBackend.Compressonator, "Fast (Compressonator)"),
        new(EncoderBackend.Managed, "Portable (BCnEncoder)"),
    ];

    /// <summary>What Auto resolves to right now, shown so the choice is not opaque.</summary>
    public string BackendStatus => $"Encoder: {TextureEncoder.BackendStatus}";

    public IReadOnlyList<SizeCap> SizeCaps { get; } =
    [
        new(0, "Keep original"),
        new(4096, "4096 max"),
        new(2048, "2048 max"),
        new(1024, "1024 max"),
        new(512, "512 max"),
    ];

    /// <summary>
    /// Resident floor for mip streaming. Off by default: the conversion writes
    /// one field whose meaning is not fully established, so it needs testing in
    /// game before anyone relies on it.
    /// </summary>
    public IReadOnlyList<StreamFloor> StreamFloors { get; } =
    [
        new(0, "Off"),
        new(2048, "Keep 2048 resident"),
        new(1024, "Keep 1024 resident"),
        new(512, "Keep 512 resident"),
        new(256, "Keep 256 resident"),
        new(128, "Keep 128 resident"),
        new(64, "Keep 64 resident"),
    ];

    /// <summary>
    /// Mip levels only means anything while streaming is off. A streamed texture
    /// keeps its whole chain — that is the point — so there is nothing to drop,
    /// and leaving the choice live would let it look as though it applied.
    /// </summary>
    public bool CanChooseMipMode => !IsBusy && StreamFloor.Value == 0;

    public string MipModeHint =>
        CanChooseMipMode
            ? "What to do with a texture that carries a mip chain.\n"
            + "Keep smaller levels: discard only the levels above the cap. The texture stays "
            + "mipmapped, so it will not shimmer at distance.\n"
            + "Keep one level only: throw the chain away and keep a single image. Smaller, but "
            + "minified surfaces shimmer and sample less efficiently.\n"
            + "Either way the pixels are the author's own: nothing is re-encoded."
            : "Not used while Stream mips is on: a streamed texture keeps its whole chain, "
            + "so there are no levels to drop. Set Stream mips to Off to choose here.";

    [ObservableProperty] private string? _bundlePath;
    [ObservableProperty] private string _status = "Open a bundle to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _collapseSolidColours = true;
    [ObservableProperty] private bool _deduplicate = true;
    [ObservableProperty] private BackendChoice _encoder = new(EncoderBackend.Auto, "Automatic");

    /// <summary>
    /// Resizing is the only genuinely lossy option here, so it is off by default
    /// and every affected texture reports its measured cost before you commit.
    /// </summary>
    [ObservableProperty] private SizeCap _sizeCap = new(0, "Keep original");
    [ObservableProperty] private StreamFloor _streamFloor = new(0, "Off");
    [ObservableProperty] private MipModeChoice _mipSelection = new(MipMode.KeepChain, "Keep smaller levels");
    [ObservableProperty] private StrategyChoice _strategy =
        new(OptimizationStrategy.Balanced, "Balanced (BC1 opaque, BC7 with alpha)");
    [ObservableProperty] private QualityChoice _quality =
        new(EncodeQuality.Balanced, "Balanced");

    /// <summary>
    /// Defaults to leaving four cores free. Saturating every core during a long
    /// encode makes the desktop unusable.
    /// </summary>
    [ObservableProperty] private int _threadCount = Math.Max(1, Environment.ProcessorCount - 4);

    public int MaxThreads => Environment.ProcessorCount;

    public string Title => BuildInfo.ProductAndVersion;
    public string VersionLabel => $"v{BuildInfo.Version}";

    public long CurrentSize { get; private set; }
    public long PredictedSize { get; private set; }
    public long Saving => CurrentSize - PredictedSize;
    public double SavingFraction => CurrentSize == 0 ? 0 : (double)Saving / CurrentSize;

    public bool HasPlan => Textures.Count > 0;
    public bool HasSkipped => Skipped.Count > 0;

    public int DuplicateEntryCount { get; private set; }
    public long RedundantBytes { get; private set; }
    public bool HasDuplicates => Deduplicate && DuplicateEntryCount > 0;
    public bool ShowDuplicateInfo => DuplicateEntryCount > 0;

    public string DuplicateSummary => DuplicateEntryCount == 0
        ? string.Empty
        : $"{DuplicateEntryCount} entries repeat a payload another entry already has, "
        + $"wasting {Format.Bytes(RedundantBytes)}. These are stored once and shared.";

    /// <summary>
    /// Deduplication alone can be a large win even when every texture is already
    /// compressed, so an empty texture list does not mean there is nothing to do.
    /// </summary>
    /// <summary>
    /// Enabled only when repacking would actually shrink something. A bundle that
    /// has already been optimised has duplicates that share one region, which cost
    /// nothing and cannot be reclaimed again.
    /// </summary>
    /// <summary>Whether repacking would shrink anything, regardless of busy state.</summary>
    public bool HasWork =>
        _plan is not null
        && (PredictedSize < CurrentSize || PredictedFootprint < CurrentFootprint);

    public bool CanOptimize => !IsBusy && HasWork;

    public string SavingSummary => CurrentSize == 0
        ? string.Empty
        : $"{Format.Bytes(CurrentSize)} → {Format.Bytes(PredictedSize)}  (−{SavingFraction:P0})";

    public long CurrentFootprint { get; private set; }
    public long PredictedFootprint { get; private set; }

    /// <summary>
    /// Video memory is not the same as file size: sharing duplicates shrinks the
    /// file, but each entry is still its own GPU resource.
    /// </summary>
    public string FootprintSummary => CurrentFootprint == 0
        ? string.Empty
        : $"GPU memory {Format.Bytes(CurrentFootprint)} → {Format.Bytes(PredictedFootprint)}";

    public async Task LoadAsync(string path)
    {
        IsBusy = true;
        Status = $"Analysing {Path.GetFileName(path)}...";
        Textures.Clear();
        Skipped.Clear();

        try
        {
            var cap = SizeCap.Value;
            var strategy = Strategy.Value;
            var mips = MipSelection.Value;
            var floor = StreamFloor.Value;
            var (plan, bundle) = await Task.Run(() =>
            {
                var loaded = Bundle.Load(path);
                if (!loaded.HasGpuResources)
                    throw new FileNotFoundException(
                        $"No .gpu_resources companion found next to {Path.GetFileName(path)}.");

                using var gpu = GpuResourceFile.Open(loaded.GpuResourcePath);
                return (OptimizationPlan.Build(loaded, gpu, strategy, CollapseSolidColours,
                                               maxDimension: cap, mipMode: mips,
                                               streamFloor: floor), loaded);
            });

            _bundle = bundle;
            BundlePath = path;
            CurrentSize = plan.CurrentGpuSize;

            foreach (var item in plan.Textures)
                Textures.Add(new TextureItemViewModel(item, RecalculateTotals));
            foreach (var skip in plan.Skipped)
                Skipped.Add(skip);

            _plan = plan;
            plan.Deduplicate = Deduplicate;
            DuplicateEntryCount = plan.DuplicateEntryCount;
            RedundantBytes = plan.RedundantBytes;
            CurrentFootprint = plan.CurrentGpuFootprint;
            RecalculateTotals();

            // HasWork, not CanOptimize: IsBusy is still true here and would make
            // every freshly loaded bundle look like it had nothing to do.
            Status = !HasWork
                ? "Nothing to do: this bundle is already optimised."
                : (Textures.Count, DuplicateEntryCount) switch
                {
                    (0, _) => $"Every texture is already compressed, but {DuplicateEntryCount} "
                            + "duplicate payloads can be shared.",
                    (_, 0) => $"{Textures.Count} texture(s) can be shrunk, {Skipped.Count} skipped.",
                    _ => $"{Textures.Count} texture(s) can be shrunk and {DuplicateEntryCount} "
                       + $"duplicate payloads shared, {Skipped.Count} skipped.",
                };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Status = $"Could not open: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(CanOptimize));
        }
    }

    private OptimizationPlan? _plan;

    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (_plan is null || _bundle is null) return;

        IsBusy = true;
        Progress = 0;
        var backupDirectory = Path.Combine(
            Path.GetDirectoryName(_bundle.Path) ?? ".", "backup");

        try
        {
            var result = await Task.Run(() =>
            {
                BundleOptimizer.CreateBackup(_bundle, backupDirectory);

                using var gpu = GpuResourceFile.Open(_bundle.GpuResourcePath);
                var reporter = new Progress<ApplyProgress>(p =>
                {
                    Progress = p.Total == 0 ? 0 : 100.0 * p.Completed / p.Total;
                    ProgressText = $"{p.Stage}: {p.Detail}";
                });

                return BundleOptimizer.Apply(
                    _plan, gpu, _bundle.Path, _bundle.GpuResourcePath,
                    new EncodeOptions
                    {
                        Quality = Quality.Value,
                        ThreadCount = ThreadCount,
                        Backend = Encoder.Value,
                    },
                    reporter, Deduplicate, outputStreamPath: _bundle.StreamPath);
            });

            var report = await Task.Run(() => BundleVerifier.Verify(
                _bundle.Path, Path.Combine(backupDirectory, Path.GetFileName(_bundle.Path))));

            Status = report.Passed
                ? $"Done: {Format.Bytes(result.OriginalGpuSize)} → {Format.Bytes(result.NewGpuSize)} "
                + $"(saved {Format.Bytes(result.Saved)}) in {result.Elapsed.TotalSeconds:F1}s. "
                + $"Verified; originals in {Path.GetFileName(backupDirectory)}/."
                : $"Written, but verification found {report.Issues.Count} issue(s): "
                + string.Join("; ", report.Issues.Take(3).Select(i => i.Detail));

            // The bundle on disk has changed, so the plan no longer describes it.
            Textures.Clear();
            Skipped.Clear();
            DuplicateEntryCount = 0;
            RedundantBytes = 0;
            CurrentFootprint = 0;
            _plan = null;
            _bundle = null;
            RecalculateTotals();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(CanOptimize));
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOptimize));
        OnPropertyChanged(nameof(CanChooseMipMode));
    }

    public void RecalculateTotals()
    {
        PredictedSize = _plan?.PredictedGpuSize ?? CurrentSize;
        OnPropertyChanged(nameof(PredictedSize));
        OnPropertyChanged(nameof(Saving));
        OnPropertyChanged(nameof(SavingFraction));
        OnPropertyChanged(nameof(SavingSummary));
        PredictedFootprint = _plan?.PredictedGpuFootprint ?? CurrentFootprint;
        OnPropertyChanged(nameof(FootprintSummary));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(ShowDuplicateInfo));
        OnPropertyChanged(nameof(DuplicateSummary));
        OnPropertyChanged(nameof(DuplicateEntryCount));
        OnPropertyChanged(nameof(HasWork));
        OnPropertyChanged(nameof(CanOptimize));
    }

    /// <summary>Re-runs analysis when the strategy changes, so the grid stays in sync.</summary>
    partial void OnStrategyChanged(StrategyChoice value)
    {
        if (BundlePath is not null && !IsBusy) _ = LoadAsync(BundlePath);
    }

    partial void OnCollapseSolidColoursChanged(bool value)
    {
        if (BundlePath is not null && !IsBusy) _ = LoadAsync(BundlePath);
    }

    partial void OnDeduplicateChanged(bool value)
    {
        if (_plan is not null) _plan.Deduplicate = value;
        RecalculateTotals();
    }

    partial void OnStreamFloorChanged(StreamFloor value)
    {
        OnPropertyChanged(nameof(CanChooseMipMode));
        OnPropertyChanged(nameof(MipModeHint));
        if (BundlePath is not null && !IsBusy) _ = LoadAsync(BundlePath);
    }

    partial void OnSizeCapChanged(SizeCap value)
    {
        if (BundlePath is not null && !IsBusy) _ = LoadAsync(BundlePath);
    }

    partial void OnMipSelectionChanged(MipModeChoice value)
    {
        if (BundlePath is not null && !IsBusy) _ = LoadAsync(BundlePath);
    }
}

/// <summary>What to do with a mip chain, with the label shown in the dropdown.</summary>
public sealed record MipModeChoice(MipMode Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An encoder backend, with the label shown in the dropdown.</summary>
public sealed record BackendChoice(EncoderBackend Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An optimisation strategy, with the label shown in the dropdown.</summary>
public sealed record StrategyChoice(OptimizationStrategy Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An encoder effort level, with the label shown in the dropdown.</summary>
public sealed record QualityChoice(EncodeQuality Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A maximum-dimension choice, with the label shown in the dropdown.</summary>
/// <summary>A resident floor choice for mip streaming.</summary>
public sealed record StreamFloor(int Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record SizeCap(int Value, string Label)
{
    public override string ToString() => Label;
}

internal static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }
}
