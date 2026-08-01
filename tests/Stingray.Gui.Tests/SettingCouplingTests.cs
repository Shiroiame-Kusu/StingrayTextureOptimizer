// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Tests;
using Stingray.Core.Textures;
using Stingray.Gui.ViewModels;
using Xunit;

namespace Stingray.Gui.Tests;

/// <summary>
/// Several settings in the window pull others along with them, because on their
/// own they would be settings that quietly do nothing. Those couplings are the
/// part a person cannot see working, so they are checked here rather than left
/// to be discovered when a bundle comes out unchanged.
/// </summary>
/// <remarks>
/// Most of these open no bundle, so nothing starts an analysis pass: the view
/// model only replans when a path is loaded, which leaves exactly the coupling
/// visible. The last two do open one, because whether a floor still has anything
/// to reach is a question only a bundle can answer.
/// </remarks>
public class SettingCouplingTests
{
    private static StreamFloor Floor(MainWindowViewModel vm, int value) =>
        vm.StreamFloors.Single(f => f.Value == value);

    /// <summary>
    /// A texture with no mip chain cannot be streamed — there is no tail to leave
    /// resident — and mods ship full of those. Picking a floor without building
    /// chains would be a setting that does nothing on exactly those bundles.
    /// </summary>
    [Fact]
    public void PickingAStreamFloorTurnsMipGenerationOn()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.AddMips);

        vm.StreamFloor = Floor(vm, 512);

        Assert.True(vm.AddMips);
    }

    /// <summary>
    /// Generation on its own costs video memory rather than saving it, so it must
    /// not be left behind when the reason for it goes away.
    /// </summary>
    [Fact]
    public void ClearingTheStreamFloorTurnsGenerationBackOff()
    {
        var vm = new MainWindowViewModel();

        vm.StreamFloor = Floor(vm, 512);
        vm.StreamFloor = Floor(vm, 0);

        Assert.False(vm.AddMips);
    }

    [Fact]
    public void GenerationAskedForDirectlySurvivesTheFloorBeingCleared()
    {
        var vm = new MainWindowViewModel { AddMips = true };

        vm.StreamFloor = Floor(vm, 512);
        vm.StreamFloor = Floor(vm, 0);

        Assert.True(vm.AddMips);
    }

    /// <summary>
    /// Turning the checkbox off after the floor turned it on is an explicit
    /// choice, and dropping the floor afterwards must not undo it.
    /// </summary>
    [Fact]
    public void TurningGenerationOffByHandOverridesTheCoupling()
    {
        var vm = new MainWindowViewModel();

        vm.StreamFloor = Floor(vm, 512);
        vm.AddMips = false;
        vm.StreamFloor = Floor(vm, 0);

        Assert.False(vm.AddMips);
    }

    /// <summary>
    /// Both of the floor's couplings have to happen, not just whichever is tested
    /// first: a streamed texture keeps its chain, so "keep one level only" has to
    /// stop showing at the same time generation starts.
    /// </summary>
    [Fact]
    public void TheFloorResetsTheMipModeAndTurnsGenerationOnTogether()
    {
        var vm = new MainWindowViewModel
        {
            MipSelection = new MipModeChoice(MipMode.SingleLevel, "Keep one level only"),
        };

        vm.StreamFloor = Floor(vm, 512);

        Assert.Equal(MipMode.KeepChain, vm.MipSelection.Value);
        Assert.True(vm.AddMips);
        Assert.False(vm.CanChooseMipMode);
    }

    /// <summary>
    /// A floor at or above the size cap is unreachable, so lowering the cap past
    /// the current floor has to move it — and that move carries the generation
    /// coupling with it when it lands on Off.
    /// </summary>
    [Fact]
    public void LoweringTheCapPullsAStrandedFloorDown()
    {
        var vm = new MainWindowViewModel();
        vm.StreamFloor = Floor(vm, 2048);

        vm.SizeCap = vm.SizeCaps.Single(c => c.Value == 1024);

        Assert.True(vm.StreamFloor.Value < 1024);
        Assert.True(vm.StreamFloor.Value > 0);
        Assert.DoesNotContain(vm.StreamFloors, f => f.Value >= 1024 && f.Value != 0);
        Assert.True(vm.AddMips);
    }

    /// <summary>
    /// Unticking generation on a bundle where nothing has a chain leaves the
    /// floor with no levels to move and no tail to leave behind, so it goes too.
    /// </summary>
    [Fact]
    public async Task UntickingGenerationClearsAFloorThatCanNoLongerReachAnything()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(128, 128, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)255))
            .Write();

        var vm = new MainWindowViewModel();
        await vm.LoadAsync(fixture.BundlePath);

        vm.StreamFloor = Floor(vm, 64);
        Assert.True(vm.AddMips);

        vm.AddMips = false;

        Assert.Equal(0, vm.StreamFloor.Value);
    }

    /// <summary>
    /// Where textures do carry chains the floor still reaches them without
    /// generating anything, so "stream what is already there, re-encode nothing"
    /// has to stay sayable — and unticking is how it gets said.
    /// </summary>
    [Fact]
    public async Task UntickingGenerationKeepsAFloorThatStillReachesRealChains()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var vm = new MainWindowViewModel();
        await vm.LoadAsync(fixture.BundlePath);

        vm.StreamFloor = Floor(vm, 64);
        vm.AddMips = false;

        Assert.Equal(64, vm.StreamFloor.Value);
        Assert.False(vm.AddMips);
    }

    /// <summary>
    /// Off is always offered: it is the only way back to leaving residency alone.
    /// </summary>
    [Fact]
    public void OffIsOfferedAtEveryCap()
    {
        var vm = new MainWindowViewModel();
        foreach (var cap in vm.SizeCaps)
        {
            vm.SizeCap = cap;
            Assert.Contains(vm.StreamFloors, f => f.Value == 0);
        }
    }
}
