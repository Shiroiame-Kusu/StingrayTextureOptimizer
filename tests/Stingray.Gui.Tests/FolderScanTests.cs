// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Format;
using Stingray.Core.Textures;
using Stingray.Core.Tests;
using Stingray.Gui.ViewModels;
using Xunit;

namespace Stingray.Gui.Tests;

/// <summary>
/// Scanning a mod manager's folder and choosing from what comes back — the path
/// someone takes who has fifteen mods installed and no idea which is the fat one.
/// </summary>
public class FolderScanTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "stingray-guiscan-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    /// <summary>Puts a bundle at root/<paramref name="folders"/>.</summary>
    private string AddMod(int width = 64, params string[] folders) =>
        AddMod(textures: 1, width, folders);

    /// <summary>Puts a bundle of <paramref name="textures"/> textures under the root.</summary>
    private string AddMod(int textures, int width, params string[] folders)
    {
        var builder = new SyntheticBundle();
        for (var i = 0; i < textures; i++)
            builder.AddTexture(width, width, (x, y) => ((byte)(x + i), (byte)y, (byte)0, (byte)255));
        var fixture = builder.Write();

        var target = Path.Combine([_root, .. folders]);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(fixture.Directory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        fixture.Dispose();

        return Path.Combine(target, "test.patch_0");
    }

    private static async Task SettleAsync(MainWindowViewModel vm)
    {
        for (var i = 0; i < 400 && vm.IsBusy; i++) await Task.Delay(10);
    }

    [Fact]
    public async Task ScanningListsWhatItFound()
    {
        AddMod(folders: "Alpha");
        AddMod(folders: "Beta");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        Assert.True(vm.HasMods);
        Assert.Equal(new[] { "Alpha", "Beta" }, vm.Mods.Select(m => m.Name));
    }

    /// <summary>
    /// The whole point of the tree: a mod's options are folders under it, and a
    /// flat list cannot say that "Shorty" and "Long boi" are the same mod.
    /// </summary>
    [Fact]
    public async Task AModsOptionsAppearUnderTheMod()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);
        AddMod(folders: "SFX");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        var mod = vm.Mods.Single(m => m.Name == "R-36");
        Assert.False(mod.IsBundle);
        Assert.Equal(new[] { "Long boi", "Shorty" }, mod.Children.Select(c => c.Name).Order());
        Assert.All(mod.Children, c => Assert.True(c.IsBundle));

        // And a mod with a single bundle is not nested for the sake of it.
        Assert.True(vm.Mods.Single(m => m.Name == "SFX").IsBundle);
    }

    /// <summary>Ticking a mod queues everything under it, which is the point of the box.</summary>
    [Fact]
    public async Task TickingAModTicksItsOptions()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        vm.Mods.Single(m => m.Name == "R-36").IsChecked = true;

        Assert.Equal(2, vm.CheckedBundles.Count);
        Assert.True(vm.HasCheckedBundles);
    }

    /// <summary>Some but not all is a state the box has to be able to show.</summary>
    [Fact]
    public async Task AModWithOneOptionTickedIsIndeterminate()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        var mod = vm.Mods.Single(m => m.Name == "R-36");
        mod.Children.First().IsChecked = true;

        Assert.Null(mod.IsChecked);
        Assert.Single(vm.CheckedBundles);
    }

    [Fact]
    public async Task BackupFoldersDoNotAppearInTheTree()
    {
        AddMod(folders: "Alpha");
        var backup = Path.Combine(_root, "Alpha", BundleDiscovery.BackupDirectoryName);
        Directory.CreateDirectory(backup);
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "Alpha")))
            File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        Assert.Single(vm.Mods);
        Assert.DoesNotContain(vm.Mods.SelectMany(m => m.Bundles),
                              b => b.Bundle!.Path.Contains(BundleDiscovery.BackupDirectoryName));
    }

    [Fact]
    public async Task ChoosingAModOpensItsBundle()
    {
        AddMod(folders: "Alpha");
        AddMod(width: 128, folders: "Beta");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        var alpha = vm.Mods.Single(m => m.Name == "Alpha");
        vm.SelectedMod = alpha;
        await SettleAsync(vm);

        Assert.Equal(alpha.Bundle!.Path, vm.BundlePath);
        Assert.True(alpha.IsOpen);
    }

    /// <summary>A folder is not a bundle, so selecting one must not try to open it.</summary>
    [Fact]
    public async Task SelectingAFolderOpensNothing()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        vm.SelectedMod = vm.Mods.Single(m => m.Name == "R-36");
        await SettleAsync(vm);

        Assert.Null(vm.BundlePath);
    }

    /// <summary>
    /// Ticking bundles does not analyse them, so the button offers that first —
    /// a batch gets reviewed before it is written, like a single bundle does.
    /// </summary>
    [Fact]
    public async Task TickedBundlesAreAnalysedBeforeTheyCanBeWritten()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);
        vm.Mods.Single(m => m.Name == "R-36").IsChecked = true;

        Assert.True(vm.WouldAnalyse);
        Assert.False(vm.HasAnalysedBatch);
        Assert.Empty(vm.Textures);

        await vm.AnalyseManyAsync(vm.CheckedBundles);

        // Both bundles' textures are on screen together, each attributed.
        Assert.True(vm.HasAnalysedBatch);
        Assert.False(vm.WouldAnalyse);
        Assert.NotEmpty(vm.Textures);
        Assert.Equal(new[] { "Long boi", "Shorty" },
                     vm.Textures.Select(t => t.ModName).Distinct().Order());
        Assert.True(vm.PredictedSize < vm.CurrentSize);
    }

    /// <summary>
    /// Several at once, each backed up and verified. The bundles here are all
    /// uncompressed RGBA8, so every one of them has something to save.
    /// </summary>
    [Fact]
    public async Task OptimisingSeveralRewritesEachOfThem()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);
        AddMod(folders: "SFX");

        var vm = new MainWindowViewModel { Quality = new QualityChoice(EncodeQuality.Fast, "fast") };
        await vm.ScanAsync(_root);

        var before = vm.Mods.SelectMany(m => m.Bundles)
                           .ToDictionary(b => b.Bundle!.Path, b => b.Bundle!.GpuSize);

        foreach (var node in vm.Mods) node.IsChecked = true;
        Assert.Equal(3, vm.CheckedBundles.Count);

        await vm.AnalyseManyAsync(vm.CheckedBundles);
        await vm.OptimizeBatchAsync();

        foreach (var (path, originalSize) in before)
        {
            Assert.True(new FileInfo(path + ".gpu_resources").Length < originalSize,
                        $"{path} was not shrunk");
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(path)!, "backup", Path.GetFileName(path))),
                        $"{path} has no backup");
        }

        // Everything it did is reported, and the ticks are cleared so a second
        // press cannot repeat the run by accident.
        Assert.Contains("3", vm.Status);
        Assert.False(vm.HasCheckedBundles);
    }

    /// <summary>
    /// Unticking Generate mipmaps is allowed, because streaming textures that
    /// already have chains without re-encoding anything is a real request. But
    /// where nothing has a chain, the floor cannot reach anything, and after a
    /// batch analysis that is knowable — so it gets said rather than left to be
    /// discovered when the run saves nothing.
    /// </summary>
    [Fact]
    public async Task AStreamFloorThatReachesNothingIsCalledOut()
    {
        // Synthetic bundles are single-level, so nothing here carries a chain.
        AddMod(folders: "Alpha");
        AddMod(folders: "Beta");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        vm.StreamFloor = vm.StreamFloors.First(f => f.Value == 64);
        Assert.True(vm.AddMips);          // the floor turned it on
        vm.AddMips = false;               // and it stays off if that is asked for

        foreach (var node in vm.Mods) node.IsChecked = true;
        await vm.AnalyseManyAsync(vm.CheckedBundles);

        Assert.Equal(64, vm.StreamFloor.Value);
        Assert.Contains("Generate mipmaps", vm.Status);
    }

    /// <summary>
    /// A mod's box says what its options say. There is no half-ticked bundle,
    /// because there is nothing under one to be half of — and a bundle left
    /// there would look chosen while counting as nothing.
    /// </summary>
    [Fact]
    public async Task ABundleCannotBeLeftHalfTicked()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: "SFX");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        var bundle = vm.Mods.Single(m => m.Name == "SFX");
        bundle.IsChecked = null;

        Assert.False(bundle.IsChecked);
        Assert.Empty(vm.CheckedBundles);

        // A folder still reports it, since a folder really can be part-chosen.
        var mod = vm.Mods.Single(m => m.Name == "R-36");
        Assert.False(mod.IsBundle);
    }

    /// <summary>
    /// The analysis belongs to the bundles it was run over. Untick one and it
    /// no longer describes what is chosen — so it goes, rather than sitting
    /// there while the button offers to write bundles nobody has ticked.
    /// </summary>
    [Fact]
    public async Task UntickingAnAnalysedBundleTakesItsPlanOffScreen()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);
        AddMod(folders: "SFX");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);
        vm.Mods.Single(m => m.Name == "R-36").IsChecked = true;
        await vm.AnalyseManyAsync(vm.CheckedBundles);

        Assert.True(vm.HasAnalysedBatch);
        var analysed = vm.Status;

        vm.Mods.Single(m => m.Name == "R-36").Children.First().IsChecked = false;

        Assert.False(vm.HasAnalysedBatch);
        Assert.Empty(vm.Textures);
        Assert.Empty(vm.Skipped);
        Assert.Equal(0, vm.CurrentSize);
        Assert.NotEqual(analysed, vm.Status);

        // And the button offers the analysis the remaining tick now needs.
        Assert.True(vm.WouldAnalyse);
        Assert.Single(vm.CheckedBundles);
    }

    /// <summary>Ticking more is the same problem from the other side.</summary>
    [Fact]
    public async Task TickingAnotherBundleAfterAnAnalysisRetiresIt()
    {
        AddMod(folders: "Alpha");
        AddMod(folders: "Beta");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);
        vm.Mods.Single(m => m.Name == "Alpha").IsChecked = true;
        await vm.AnalyseManyAsync(vm.CheckedBundles);
        Assert.True(vm.HasAnalysedBatch);

        vm.Mods.Single(m => m.Name == "Beta").IsChecked = true;

        Assert.False(vm.HasAnalysedBatch);
        Assert.True(vm.WouldAnalyse);
        Assert.Equal(2, vm.CheckedBundles.Count);
    }

    /// <summary>But leaving the ticks alone leaves the analysis alone.</summary>
    [Fact]
    public async Task AnAnalysisSurvivesTheTicksItWasBuiltFrom()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);
        vm.Mods.Single(m => m.Name == "R-36").IsChecked = true;
        await vm.AnalyseManyAsync(vm.CheckedBundles);

        vm.RefreshSelection();

        Assert.True(vm.HasAnalysedBatch);
        Assert.NotEmpty(vm.Textures);
    }

    /// <summary>
    /// Opening one bundle replaces the grid, so a batch cannot outlive it: the
    /// totals would sum the batch against this bundle's size, and the button
    /// would still write the batch while the screen showed something else.
    /// </summary>
    [Fact]
    public async Task OpeningOneBundleRetiresAnAnalysedBatch()
    {
        AddMod(folders: "Alpha");
        AddMod(width: 128, folders: "Beta");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);
        vm.Mods.Single(m => m.Name == "Alpha").IsChecked = true;
        await vm.AnalyseManyAsync(vm.CheckedBundles);
        Assert.True(vm.HasAnalysedBatch);

        var beta = vm.Mods.Single(m => m.Name == "Beta");
        vm.SelectedMod = beta;
        await SettleAsync(vm);

        Assert.False(vm.HasAnalysedBatch);
        Assert.Equal(beta.Bundle!.Path, vm.BundlePath);

        // The totals are this bundle's alone, not this bundle's against a batch.
        Assert.True(vm.CurrentSize > 0);
        Assert.True(vm.PredictedSize < vm.CurrentSize);
    }

    /// <summary>
    /// The confirmation counts bundles. The grid holds one row per texture and
    /// several of them can come from one bundle, so the two are not the same
    /// number and saying "optimize 4 bundles" when there are two is a lie about
    /// what is being rewritten.
    /// </summary>
    [Fact]
    public async Task TheBatchCountIsBundlesNotRows()
    {
        AddMod(textures: 3, width: 64, folders: "Alpha");
        AddMod(textures: 2, width: 64, folders: "Beta");

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);
        foreach (var node in vm.Mods) node.IsChecked = true;
        await vm.AnalyseManyAsync(vm.CheckedBundles);

        Assert.Equal(2, vm.AnalysedBundleCount);
        Assert.Equal(5, vm.Textures.Count);
    }

    /// <summary>
    /// The panel's sizes come from the scan, so after a write they would go on
    /// quoting what the bundles used to cost — the one number the exercise was
    /// about, left saying the thing that is no longer true.
    /// </summary>
    [Fact]
    public async Task TheTreeQuotesWhatTheFilesCostAfterAWrite()
    {
        AddMod(folders: ["R-36", "Shorty"]);
        AddMod(folders: ["R-36", "Long boi"]);

        var vm = new MainWindowViewModel { Quality = new QualityChoice(EncodeQuality.Fast, "fast") };
        await vm.ScanAsync(_root);

        var mod = vm.Mods.Single(m => m.Name == "R-36");
        var before = mod.GpuSize;

        mod.IsChecked = true;
        await vm.AnalyseManyAsync(vm.CheckedBundles);
        await vm.OptimizeBatchAsync();

        Assert.True(mod.GpuSize < before, $"{mod.GpuSize} is not below {before}");
        foreach (var bundle in mod.Bundles)
            Assert.Equal(new FileInfo(bundle.Bundle!.Path + ".gpu_resources").Length,
                         bundle.GpuSize);
    }

    [Fact]
    public async Task AFolderWithNoModsSaysSoRatherThanLookingEmpty()
    {
        Directory.CreateDirectory(_root);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        Assert.False(vm.HasMods);
        Assert.Contains(_root, vm.Status);
    }

    [Fact]
    public async Task ScanningSomewhereMissingReportsItAndCarriesOn()
    {
        var vm = new MainWindowViewModel();
        await vm.ScanAsync(Path.Combine(_root, "nowhere"));

        Assert.False(vm.HasMods);
        Assert.False(vm.IsBusy);
        Assert.NotEmpty(vm.Status);
    }
}
