// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stingray.Core;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Stingray.Gui.Localization;

namespace Stingray.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private Bundle? _bundle;

    public ObservableCollection<TextureItemViewModel> Textures { get; } = [];
    public ObservableCollection<SkippedTexture> Skipped { get; } = [];

    /// <summary>The folder tree a scan found, mirroring how the mods sit on disk.</summary>
    public ObservableCollection<ModNodeViewModel> Mods { get; } = [];

    public bool HasMods => Mods.Count > 0;

    /// <summary>Every bundle in the tree, in the order it is shown.</summary>
    private IEnumerable<ModNodeViewModel> AllBundles => Mods.SelectMany(m => m.Bundles);

    /// <summary>The bundles ticked for optimising.</summary>
    public IReadOnlyList<ModNodeViewModel> CheckedBundles =>
        [.. AllBundles.Where(b => b.IsChecked == true)];

    public bool HasCheckedBundles => CheckedBundles.Count > 0;

    /// <summary>
    /// The plans behind an analysed batch, kept so Optimize applies exactly what
    /// was reviewed rather than re-deciding on its own.
    /// </summary>
    private readonly List<(Bundle Bundle, OptimizationPlan Plan)> _batch = [];

    private bool HasBatch => _batch.Count > 0;

    /// <summary>
    /// Textures in whatever has been analysed that already carry a mip chain, or
    /// -1 when nothing has been analysed and the question cannot be answered.
    /// </summary>
    private int AnalysedChainedCount =>
        HasBatch ? _batch.Sum(b => b.Plan.ChainedCount) : _plan?.ChainedCount ?? -1;

    private int AnalysedChainlessCount =>
        HasBatch ? _batch.Sum(b => b.Plan.ChainlessCount) : _plan?.ChainlessCount ?? 0;

    /// <summary>
    /// A stream floor set with no generation, over textures none of which carry a
    /// chain, is a setting that cannot do anything. Worth saying, because nothing
    /// else on screen would show it.
    /// </summary>
    private string StreamWarning =>
        StreamFloor.Value != 0 && !AddMips && AnalysedChainedCount == 0
            ? S.StreamReachesNothing(AnalysedChainlessCount)
            : string.Empty;

    /// <summary>Whether a reviewed batch is waiting to be written.</summary>
    public bool HasAnalysedBatch => HasBatch;

    public string SelectionSummary => CheckedBundles is { Count: > 0 } checkedBundles
        ? S.SelectedForOptimising(checkedBundles.Count,
                                  Format.Bytes(checkedBundles.Sum(b => b.GpuSize)))
        : string.Empty;

    /// <summary>
    /// Ticking bundles does not analyse them, so the button offers that first and
    /// only becomes Optimize once there is a plan on screen to have looked at.
    /// </summary>
    public string OptimizeLabel =>
        HasBatch ? S.Optimize
        : CheckedBundles.Count > 0 ? S.AnalyseSelected(CheckedBundles.Count)
        : S.Optimize;

    /// <summary>Whether pressing the button would analyse rather than write.</summary>
    public bool WouldAnalyse => !HasBatch && CheckedBundles.Count > 0;

    /// <summary>Ticking anything changes what Optimize would do, so it has to know.</summary>
    public void RefreshSelection()
    {
        OnPropertyChanged(nameof(CheckedBundles));
        OnPropertyChanged(nameof(HasCheckedBundles));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(OptimizeLabel));
        OnPropertyChanged(nameof(WouldAnalyse));
        OnPropertyChanged(nameof(CanOptimize));
    }

    /// <summary>
    /// The line whose bundle is open. Selecting a folder shows nothing: there is
    /// no single plan for a folder, which is what the tick boxes are for.
    /// </summary>
    [ObservableProperty] private ModNodeViewModel? _selectedMod;

    partial void OnSelectedModChanged(ModNodeViewModel? value)
    {
        if (value is null || !value.IsBundle || IsBusy) return;
        foreach (var bundle in AllBundles) bundle.IsOpen = ReferenceEquals(bundle, value);
        _ = LoadAsync(value.Bundle!.Path);
    }

    /// <summary>
    /// Walks a folder for bundles and lists what it finds. Nothing is opened:
    /// a mod manager's folder can hold gigabytes, and the point is to choose.
    /// </summary>
    public async Task ScanAsync(string folder)
    {
        IsBusy = true;
        Status = S.Scanning(folder);
        Mods.Clear();
        OnPropertyChanged(nameof(HasMods));

        try
        {
            var found = await Task.Run(() => BundleDiscovery.Scan(folder));

            foreach (var node in ModNodeViewModel.Build(found)) Mods.Add(node);
            Track(Mods);

            Status = found.Count == 0
                ? S.FoundNothing(folder)
                : S.FoundMods(found.Count, Format.Bytes(found.Sum(f => f.GpuSize)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            Status = S.CouldNotOpen(ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasMods));
            RefreshSelection();
        }
    }

    /// <summary>Subscribes every node in the tree, so a tick anywhere is noticed.</summary>
    private void Track(IEnumerable<ModNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModNodeViewModel.IsChecked)) RefreshSelection();
            };
            Track(node.Children);
        }
    }

    public IReadOnlyList<StrategyChoice> Strategies { get; } =
    [
        new(OptimizationStrategy.Balanced, S.StrategyBalanced),
        new(OptimizationStrategy.MaximumQuality, S.StrategyQuality),
        new(OptimizationStrategy.SmallestSize, S.StrategySmallest),
    ];

    public IReadOnlyList<QualityChoice> Qualities { get; } =
    [
        new(EncodeQuality.Fast, S.QualityFast),
        new(EncodeQuality.Balanced, S.QualityBalanced),
        new(EncodeQuality.Best, S.QualityBest),
    ];

    public IReadOnlyList<MipModeChoice> MipModes { get; } =
    [
        new(MipMode.KeepChain, S.MipKeepChain),
        new(MipMode.SingleLevel, S.MipSingleLevel),
    ];

    public IReadOnlyList<BackendChoice> Backends { get; } =
    [
        new(EncoderBackend.Auto, S.BackendAuto),
        new(EncoderBackend.Compressonator, S.BackendFast),
        new(EncoderBackend.Managed, S.BackendPortable),
    ];

    /// <summary>What Auto resolves to right now, shown so the choice is not opaque.</summary>
    public string BackendStatus => S.EncoderStatus(TextureEncoder.BackendStatus);

    public IReadOnlyList<SizeCap> SizeCaps { get; } =
    [
        new(0, S.KeepOriginalSize),
        new(4096, S.SizeCapMax(4096)),
        new(2048, S.SizeCapMax(2048)),
        new(1024, S.SizeCapMax(1024)),
        new(512, S.SizeCapMax(512)),
    ];

    /// <summary>
    /// Every resident floor mip streaming could offer. Off by default: the
    /// conversion writes one field that cannot be derived from the texture, so it
    /// needs testing in game before anyone relies on it.
    /// </summary>
    private static readonly StreamFloor[] AllStreamFloors =
    [
        new(0, S.StreamOff),
        new(2048, S.StreamKeep(2048)),
        new(1024, S.StreamKeep(1024)),
        new(512, S.StreamKeepSuggested(512)),
        new(256, S.StreamKeep(256)),
        new(128, S.StreamKeep(128)),
        new(64, S.StreamKeep(64)),
    ];

    /// <summary>
    /// Floors that can actually do something. A floor at or above the size cap is
    /// unreachable — nothing survives the cap that is bigger than it — so offering
    /// it would only let you pick a setting with no effect.
    /// </summary>
    public IReadOnlyList<StreamFloor> StreamFloors =>
    [
        .. AllStreamFloors.Where(f => f.Value == 0
                                      || SizeCap.Value == 0
                                      || f.Value < SizeCap.Value),
    ];

    /// <summary>
    /// Mip levels only means anything while streaming is off. A streamed texture
    /// keeps whatever chain survives the size cap — that is the point — so there
    /// is nothing further to drop, and leaving the choice live would let it look
    /// as though it applied.
    /// </summary>
    public bool CanChooseMipMode => !IsBusy && StreamFloor.Value == 0;

    public string MipModeHint =>
        CanChooseMipMode ? S.MipModeHintActive : S.MipModeHintDisabled;

    /// <summary>The open bundle, or a standing invitation to open one.</summary>
    public string BundleLabel => BundlePath ?? S.NoBundleLoaded;

    [ObservableProperty] private string? _bundlePath;
    [ObservableProperty] private string _status = S.OpenABundleToBegin;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _collapseSolidColours = true;
    [ObservableProperty] private bool _deduplicate = true;

    /// <summary>
    /// Build a mip chain for textures that shipped without one. Off by default: it
    /// re-encodes, and on its own it costs video memory rather than saving it.
    /// </summary>
    [ObservableProperty] private bool _addMips;
    [ObservableProperty] private BackendChoice _encoder = new(EncoderBackend.Auto, S.BackendAuto);

    /// <summary>
    /// Resizing is the only genuinely lossy option here, so it is off by default
    /// and every affected texture reports its measured cost before you commit.
    /// </summary>
    [ObservableProperty] private SizeCap _sizeCap = new(0, S.KeepOriginalSize);
    [ObservableProperty] private StreamFloor _streamFloor = new(0, S.StreamOff);
    [ObservableProperty] private MipModeChoice _mipSelection = new(MipMode.KeepChain, S.MipKeepChain);
    [ObservableProperty] private StrategyChoice _strategy =
        new(OptimizationStrategy.Balanced, S.StrategyBalanced);
    [ObservableProperty] private QualityChoice _quality =
        new(EncodeQuality.Balanced, S.QualityBalanced);

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
        : S.DuplicateSummary(DuplicateEntryCount, Format.Bytes(RedundantBytes));

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
        (_plan is not null || HasBatch)
        && (PredictedSize < CurrentSize || PredictedFootprint < CurrentFootprint);

    /// <summary>
    /// Enabled to analyse a ticked batch, or to write one that turned out to
    /// have something to save. An analysed batch with nothing to do leaves it
    /// disabled rather than offering a press that would rewrite nothing.
    /// </summary>
    public bool CanOptimize => !IsBusy && (HasWork || WouldAnalyse);

    public string SavingSummary => CurrentSize == 0
        ? string.Empty
        : S.SavingSummary(Format.Bytes(CurrentSize), Format.Bytes(PredictedSize),
                          SavingFraction.ToString("P0"));

    public long CurrentFootprint { get; private set; }
    public long PredictedFootprint { get; private set; }

    /// <summary>
    /// Video memory is not the same as file size: sharing duplicates shrinks the
    /// file, but each entry is still its own GPU resource.
    /// </summary>
    public string FootprintSummary => CurrentFootprint == 0
        ? string.Empty
        : S.FootprintSummary(Format.Bytes(CurrentFootprint), Format.Bytes(PredictedFootprint));

    public async Task LoadAsync(string path)
    {
        IsBusy = true;
        Status = S.Analysing(Path.GetFileName(path));
        Textures.Clear();
        Skipped.Clear();

        try
        {
            var cap = SizeCap.Value;
            var strategy = Strategy.Value;
            var mips = MipSelection.Value;
            var floor = StreamFloor.Value;
            var addMips = AddMips;
            var (plan, bundle) = await Task.Run(() =>
            {
                var loaded = Bundle.Load(path);
                if (!loaded.HasGpuResources)
                    throw new FileNotFoundException(
                        $"No .gpu_resources companion found next to {Path.GetFileName(path)}.");

                using var gpu = GpuResourceFile.Open(loaded.GpuResourcePath);
                return (OptimizationPlan.Build(loaded, gpu, strategy, CollapseSolidColours,
                                               maxDimension: cap, mipMode: mips,
                                               streamFloor: floor,
                                               generateMips: addMips), loaded);
            });

            _bundle = bundle;
            BundlePath = path;
            OnPropertyChanged(nameof(BundleLabel));
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
                ? S.AlreadyOptimised
                : (Textures.Count, DuplicateEntryCount) switch
                {
                    (0, _) => S.OnlyDuplicates(DuplicateEntryCount),
                    (_, 0) => S.CanShrink(Textures.Count, Skipped.Count),
                    _ => S.CanShrinkAndShare(Textures.Count, DuplicateEntryCount, Skipped.Count),
                };
            Status += StreamWarning;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Status = S.CouldNotOpen(ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(CanOptimize));
        }
    }

    private OptimizationPlan? _plan;

    /// <summary>
    /// Analyses every ticked bundle and shows all of their textures together, so
    /// a batch is reviewed before it is written rather than taken on trust.
    /// </summary>
    public async Task AnalyseManyAsync(IReadOnlyList<ModNodeViewModel> targets)
    {
        IsBusy = true;
        Progress = 0;
        ClearBatch();
        Textures.Clear();
        Skipped.Clear();

        var strategy = Strategy.Value;
        var collapse = CollapseSolidColours;
        var dedup = Deduplicate;
        var cap = SizeCap.Value;
        var mipMode = MipSelection.Value;
        var floor = StreamFloor.Value;
        var addMips = AddMips;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                Progress = 100.0 * i / targets.Count;
                ProgressText = S.AnalysingMany(i + 1, targets.Count, target.Name);

                var analysed = await Task.Run<(Bundle Bundle, OptimizationPlan Plan)?>(() =>
                {
                    var bundle = Bundle.Load(target.Bundle!.Path);
                    if (!bundle.HasGpuResources) return null;

                    using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
                    var plan = OptimizationPlan.Build(bundle, gpu, strategy, collapse,
                                                      maxDimension: cap, mipMode: mipMode,
                                                      streamFloor: floor, generateMips: addMips);
                    plan.Deduplicate = dedup;
                    return (Bundle: bundle, Plan: plan);
                });

                if (analysed is not { } entry) continue;

                _batch.Add(entry);
                foreach (var item in entry.Plan.Textures)
                    Textures.Add(new TextureItemViewModel(item, RecalculateTotals, target.Name));
                foreach (var skip in entry.Plan.Skipped)
                    Skipped.Add(skip);
            }

            CurrentSize = _batch.Sum(b => b.Plan.CurrentGpuSize);
            CurrentFootprint = _batch.Sum(b => b.Plan.CurrentGpuFootprint);
            DuplicateEntryCount = _batch.Sum(b => b.Plan.DuplicateEntryCount);
            RedundantBytes = _batch.Sum(b => b.Plan.RedundantBytes);

            Status = (HasWork
                ? S.AnalysedMany(_batch.Count, Textures.Count, Skipped.Count)
                : S.AnalysedNothing(_batch.Count)) + StreamWarning;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or NotSupportedException or UnauthorizedAccessException)
        {
            ClearBatch();
            Status = S.CouldNotOpen(ex.Message);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            ProgressText = "";
            BundlePath = null;
            OnPropertyChanged(nameof(BundleLabel));
            RecalculateTotals();
            RefreshSelection();
        }
    }

    /// <summary>
    /// Writes the batch that was analysed — the same plans that are on screen,
    /// with whatever was unticked in the grid left out, each backed up and
    /// verified exactly as a single bundle is.
    /// </summary>
    public async Task OptimizeBatchAsync()
    {
        IsBusy = true;
        Progress = 0;

        var settings = new EncodeOptions
        {
            Quality = Quality.Value,
            ThreadCount = ThreadCount,
            Backend = Encoder.Value,
        };

        var optimised = 0;
        var skipped = 0;
        var failed = 0;
        long saved = 0;
        var work = _batch.ToList();

        try
        {
            for (var i = 0; i < work.Count; i++)
            {
                var (bundle, plan) = work[i];
                Progress = 100.0 * i / work.Count;
                ProgressText = S.BatchProgress(i + 1, work.Count, Path.GetFileName(bundle.Path));

                var outcome = await Task.Run(() =>
                {
                    if (plan.PredictedGpuSize >= plan.CurrentGpuSize
                        && plan.PredictedGpuFootprint >= plan.CurrentGpuFootprint)
                        return (Result: 0, Saved: 0L);

                    try
                    {
                        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
                        var backupDirectory = Path.Combine(
                            Path.GetDirectoryName(bundle.Path) ?? ".", "backup");
                        BundleOptimizer.CreateBackup(bundle, backupDirectory);

                        var result = BundleOptimizer.Apply(
                            plan, gpu, bundle.Path, bundle.GpuResourcePath, settings,
                            deduplicate: plan.Deduplicate, outputStreamPath: bundle.StreamPath);

                        var report = BundleVerifier.Verify(
                            bundle.Path,
                            Path.Combine(backupDirectory, Path.GetFileName(bundle.Path)));

                        return (Result: report.Passed ? 1 : -1, result.Saved);
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException
                                                 or NotSupportedException
                                                 or UnauthorizedAccessException)
                    {
                        // A backup already there is the usual reason, and it must
                        // not stop the bundles after this one.
                        return (Result: -1, Saved: 0L);
                    }
                });

                switch (outcome.Result)
                {
                    case 1: optimised++; saved += outcome.Saved; break;
                    case 0: skipped++; break;
                    default: failed++; break;
                }
            }

            Status = S.BatchDone(optimised, skipped, failed, Format.Bytes(saved));
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            ProgressText = "";

            ClearBatch();
            Textures.Clear();
            Skipped.Clear();
            foreach (var bundle in AllBundles) bundle.IsChecked = false;
            CurrentSize = 0;
            CurrentFootprint = 0;
            DuplicateEntryCount = 0;
            RedundantBytes = 0;
            RecalculateTotals();
            RefreshSelection();
        }
    }

    /// <summary>
    /// Drops an analysed batch. Anything that would change what a plan says —
    /// a setting, or which bundles are ticked — invalidates it, and the button
    /// goes back to offering an analysis rather than a write.
    /// </summary>
    private void ClearBatch()
    {
        if (_batch.Count == 0) return;
        _batch.Clear();
    }

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
                    ProgressText = S.Progress(p.Stage, p.Detail);
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
                ? S.Done(Format.Bytes(result.OriginalGpuSize), Format.Bytes(result.NewGpuSize),
                         Format.Bytes(result.Saved), result.Elapsed.TotalSeconds.ToString("F1"),
                         Path.GetFileName(backupDirectory))
                : S.WrittenButIssues(report.Issues.Count,
                                     string.Join("; ", report.Issues.Take(3).Select(i => i.Detail)));

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
            Status = S.Failed(ex.Message);
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
        // A batch has no single plan; its totals are the sum of the ones behind it.
        PredictedSize = HasBatch
            ? _batch.Sum(b => b.Plan.PredictedGpuSize)
            : _plan?.PredictedGpuSize ?? CurrentSize;
        OnPropertyChanged(nameof(PredictedSize));
        OnPropertyChanged(nameof(Saving));
        OnPropertyChanged(nameof(SavingFraction));
        OnPropertyChanged(nameof(SavingSummary));
        PredictedFootprint = HasBatch
            ? _batch.Sum(b => b.Plan.PredictedGpuFootprint)
            : _plan?.PredictedGpuFootprint ?? CurrentFootprint;
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
    partial void OnStrategyChanged(StrategyChoice value) => Replan();

    partial void OnAddMipsChanged(bool value)
    {
        if (_linking) { Replan(); return; }

        // An explicit tick or untick wins over the streaming coupling from here
        // on: whatever the box says now is what the user meant it to say.
        _addMipsFollowsStream = false;

        // Unticking can leave the floor with nothing to reach. In a bundle where
        // no texture carries a chain — which plenty of mods are — streaming has
        // no levels to move and no tail to leave behind, so the floor becomes a
        // setting that looks applied and is not, which is the thing this pair of
        // couplings exists to prevent. Clear it.
        //
        // A bundle that has chained textures keeps its floor: those stream
        // exactly as they are, and generating is only ever about the others. So
        // "stream what already has a chain, re-encode nothing" stays sayable,
        // which unticking is the natural way to say.
        if (!value && StreamFloor.Value != 0 && AnalysedChainedCount == 0)
        {
            Link(() => StreamFloor = StreamFloors.First(f => f.Value == 0));
            return;
        }

        Replan();
    }

    partial void OnCollapseSolidColoursChanged(bool value) => Replan();

    partial void OnDeduplicateChanged(bool value)
    {
        if (_plan is not null) _plan.Deduplicate = value;
        foreach (var (_, plan) in _batch) plan.Deduplicate = value;
        RecalculateTotals();
    }

    partial void OnStreamFloorChanged(StreamFloor value) => Link(() =>
    {
        OnPropertyChanged(nameof(CanChooseMipMode));
        OnPropertyChanged(nameof(MipModeHint));

        // Streaming always keeps the chain, so show that rather than leaving a
        // stale "keep one level only" greyed out as though it still applied.
        if (value.Value != 0 && MipSelection.Value != MipMode.KeepChain)
            MipSelection = MipModes[0];

        // A texture with no mip chain cannot be streamed: there is no tail to
        // leave resident and no levels to move out. Mods ship that way
        // constantly, so a floor on its own is a setting that does nothing on
        // exactly the bundles that need it most. Turn generation on with it —
        // and off again with it, because generation alone adds video memory
        // rather than removing it, and leaving it set would quietly invert what
        // the tool is for. Generation only ever touches a texture that has no
        // chain, so this cannot disturb one that already has its own.
        if (value.Value != 0 && !AddMips)
        {
            _addMipsFollowsStream = true;
            AddMips = true;
        }
        else if (value.Value == 0 && _addMipsFollowsStream)
        {
            _addMipsFollowsStream = false;
            AddMips = false;
        }
    });

    partial void OnSizeCapChanged(SizeCap value) => Link(() =>
    {
        OnPropertyChanged(nameof(StreamFloors));

        // Lowering the cap can strand the current floor above it, where it would
        // silently do nothing. Pull it down to the largest one that still works.
        if (StreamFloor.Value != 0 && value.Value != 0 && StreamFloor.Value >= value.Value)
            StreamFloor = StreamFloors.FirstOrDefault(f => f.Value != 0)
                          ?? AllStreamFloors[0];
    });

    partial void OnMipSelectionChanged(MipModeChoice value) => Replan();

    /// <summary>
    /// True while one setting is pulling others along with it, so a change can
    /// tell whether it came from the person at the window or from a sibling
    /// setting, and so the group replans once rather than once per member.
    /// </summary>
    private bool _linking;

    /// <summary>
    /// Set when picking a stream floor was what turned <see cref="AddMips"/> on,
    /// which is the only case where dropping the floor turns it off again.
    /// </summary>
    private bool _addMipsFollowsStream;

    /// <summary>
    /// Applies a group of coupled setting changes as one edit. Each of these
    /// settings would otherwise start its own analysis pass, so several would run
    /// over each other on a single click; the replan is held until the group has
    /// settled and then run once against the state it left behind.
    /// </summary>
    private void Link(Action changes)
    {
        if (_linking) { changes(); return; }

        _linking = true;
        try { changes(); }
        finally { _linking = false; }
        Replan();
    }

    /// <summary>Re-analyses the open bundle under the current settings.</summary>
    private void Replan()
    {
        if (_linking) return;

        // An analysed batch describes the settings it was analysed under, so a
        // change to those settings retires it and the button offers a fresh
        // analysis rather than writing something nobody looked at.
        if (HasBatch && !IsBusy)
        {
            ClearBatch();
            Textures.Clear();
            Skipped.Clear();
            CurrentSize = 0;
            CurrentFootprint = 0;
            DuplicateEntryCount = 0;
            RedundantBytes = 0;
            RecalculateTotals();
            RefreshSelection();
            return;
        }

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
