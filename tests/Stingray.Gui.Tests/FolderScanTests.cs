// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Format;
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

    private string AddMod(string modName, int width = 64)
    {
        var fixture = new SyntheticBundle()
            .AddTexture(width, width, (x, y) => ((byte)x, (byte)y, (byte)0, (byte)255))
            .Write();

        var target = Path.Combine(_root, modName);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(fixture.Directory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        fixture.Dispose();

        return Path.Combine(target, "test.patch_0");
    }

    /// <summary>Waits for the load a selection kicks off, which does not block.</summary>
    private static async Task SettleAsync(MainWindowViewModel vm)
    {
        for (var i = 0; i < 200 && (vm.IsBusy || vm.BundlePath is null); i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task ScanningListsWhatItFoundLargestFirst()
    {
        AddMod("Small", width: 16);
        AddMod("Large", width: 256);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        Assert.True(vm.HasMods);
        Assert.Equal(2, vm.Mods.Count);
        Assert.Equal("Large", vm.Mods[0].Name);
        Assert.Equal(1, vm.Mods[0].Number);
        Assert.Equal(2, vm.Mods[1].Number);
    }

    /// <summary>
    /// The originals this tool writes must not come back as mods of their own:
    /// every mod would appear twice, and optimising the backup would overwrite
    /// the only way back.
    /// </summary>
    [Fact]
    public async Task BackupFoldersDoNotAppearInTheList()
    {
        AddMod("Alpha");
        var backup = Path.Combine(_root, "Alpha", BundleDiscovery.BackupDirectoryName);
        Directory.CreateDirectory(backup);
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "Alpha")))
            File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        Assert.Single(vm.Mods);
        Assert.DoesNotContain(vm.Mods, m => m.Path.Contains(BundleDiscovery.BackupDirectoryName));
    }

    [Fact]
    public async Task ChoosingAModOpensItsBundle()
    {
        AddMod("Alpha");
        AddMod("Beta", width: 128);

        var vm = new MainWindowViewModel();
        await vm.ScanAsync(_root);

        vm.SelectedMod = vm.Mods.Single(m => m.Name == "Alpha");
        await SettleAsync(vm);

        Assert.Equal(vm.Mods.Single(m => m.Name == "Alpha").Path, vm.BundlePath);
        Assert.True(vm.Mods.Single(m => m.Name == "Alpha").IsOpen);
        Assert.False(vm.Mods.Single(m => m.Name == "Beta").IsOpen);
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

    /// <summary>A folder that is not there is a message, not a crash.</summary>
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
