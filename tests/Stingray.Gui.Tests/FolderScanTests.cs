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
    private string AddMod(int width = 64, params string[] folders)
    {
        var fixture = new SyntheticBundle()
            .AddTexture(width, width, (x, y) => ((byte)x, (byte)y, (byte)0, (byte)255))
            .Write();

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
