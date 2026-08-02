// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

/// <summary>
/// Finding bundles under a mod manager's folder, which is one folder per mod
/// holding a bundle and its companions.
/// </summary>
public class BundleDiscoveryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "stingray-scan-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    /// <summary>Puts a real bundle in a folder under the scan root.</summary>
    private string AddMod(string modName, int width = 64, params string[] extraPath)
    {
        var fixture = new SyntheticBundle()
            .AddTexture(width, width, (x, y) => ((byte)x, (byte)y, (byte)0, (byte)255))
            .Write();

        var target = Path.Combine([_root, .. extraPath, modName]);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(fixture.Directory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        fixture.Dispose();

        return Path.Combine(target, "test.patch_0");
    }

    [Fact]
    public void EachModFolderBecomesOneEntry()
    {
        AddMod("Alpha");
        AddMod("Beta");

        var found = BundleDiscovery.Scan(_root);

        Assert.Equal(2, found.Count);
        Assert.Equal(new[] { "Alpha", "Beta" }, found.Select(f => f.ModName).OrderBy(n => n));
        Assert.All(found, f => Assert.Equal("test.patch_0", f.FileName));
        Assert.All(found, f => Assert.True(f.GpuSize > 0));
    }

    /// <summary>
    /// The tool writes its own originals into backup/. Offering those would list
    /// every mod twice, and optimising one would overwrite the only way back.
    /// </summary>
    [Fact]
    public void BackupFoldersAreSkipped()
    {
        AddMod("Alpha");
        AddMod("Alpha", extraPath: ["nested"]);

        // Exactly what CreateBackup leaves behind, inside a mod folder.
        var backup = Path.Combine(_root, "Alpha", BundleDiscovery.BackupDirectoryName);
        Directory.CreateDirectory(backup);
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "Alpha")))
            File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));

        var found = BundleDiscovery.Scan(_root);

        Assert.Equal(2, found.Count);
        Assert.DoesNotContain(found, f => f.Path.Contains(BundleDiscovery.BackupDirectoryName));
    }

    [Fact]
    public void TheScanReachesNestedFolders()
    {
        AddMod("Deep", extraPath: ["mods", "installed"]);

        var found = Assert.Single(BundleDiscovery.Scan(_root));

        Assert.Equal("Deep", found.ModName);
    }

    /// <summary>Companions are not bundles, however much they look like files.</summary>
    [Fact]
    public void CompanionFilesAreNotOfferedAsBundles()
    {
        AddMod("Alpha");

        var found = Assert.Single(BundleDiscovery.Scan(_root));

        Assert.DoesNotContain(".gpu_resources", found.Path);
        Assert.DoesNotContain(".stream", found.Path);
    }

    /// <summary>
    /// A file with a .gpu_resources beside it but no bundle magic is somebody
    /// else's file, and opening it would only produce an error later.
    /// </summary>
    [Fact]
    public void SomethingThatIsNotABundleIsIgnored()
    {
        var folder = Path.Combine(_root, "NotAMod");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(folder, "readme.txt.gpu_resources"), "not a bundle");

        Assert.Empty(BundleDiscovery.Scan(_root));
    }

    /// <summary>
    /// Ordered by where they sit, not by size: a mod's options have to stay with
    /// the mod, or a list of two hundred bundles cannot be read at all.
    /// </summary>
    [Fact]
    public void TheOrderFollowsTheFolderTree()
    {
        AddMod("Shorty", extraPath: ["R-36"]);
        AddMod("Long boi", extraPath: ["R-36"]);
        AddMod("Alpha");

        var found = BundleDiscovery.Scan(_root);

        Assert.Equal(new[] { "Alpha", "Long boi", "Shorty" },
                     found.Select(f => f.ModName));
    }

    /// <summary>
    /// A mod's variants live in folders under it, which is the only thing that
    /// says "Long boi" is an option of R-36 rather than a mod of its own.
    /// </summary>
    [Fact]
    public void NestedFoldersAreReported()
    {
        AddMod("Long boi", extraPath: ["R-36"]);
        AddMod("Loose");

        var found = BundleDiscovery.Scan(_root);

        Assert.Equal(new[] { "R-36", "Long boi" },
                     found.Single(f => f.ModName == "Long boi").RelativeFolders);
        Assert.Equal(new[] { "Loose" },
                     found.Single(f => f.ModName == "Loose").RelativeFolders);
    }

    /// <summary>A bundle loose in the scanned folder is named after itself.</summary>
    [Fact]
    public void ALooseBundleIsNamedAfterItsFile()
    {
        var fixture = new SyntheticBundle()
            .AddTexture(32, 32, (x, y) => ((byte)x, (byte)y, (byte)0, (byte)255))
            .Write();
        Directory.CreateDirectory(_root);
        foreach (var file in Directory.GetFiles(fixture.Directory))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        fixture.Dispose();

        var found = Assert.Single(BundleDiscovery.Scan(_root));
        Assert.Equal("test.patch_0", found.ModName);
        Assert.Empty(found.RelativeFolders);
    }

    /// <summary>
    /// A bundle that cannot be read is reported rather than dropped. On Windows
    /// the usual cause is another process holding it open — the game, or a mod
    /// manager — and a folder that quietly lists fewer mods than it holds sends
    /// you looking for the wrong problem.
    /// </summary>
    [Fact]
    public void ABundleThatCannotBeReadIsReportedRatherThanDropped()
    {
        AddMod("Readable");
        var locked = AddMod("Locked");

        // Two ways to be unopenable, one per platform: a share-exclusive handle
        // is what another process holding the file looks like on Windows, and
        // Unix has no such thing, so permissions stand in for it there.
        FileStream? hold = null;
        if (OperatingSystem.IsWindows())
            hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        else
            File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var found = BundleDiscovery.Scan(_root, out var unreadable);

            Assert.Equal("Readable", Assert.Single(found).ModName);
            Assert.Contains("Locked", Assert.Single(unreadable));
        }
        finally
        {
            hold?.Dispose();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// Windows only lets a process past 260 characters when it has opted in, and
    /// nothing here had ever been run against a path that long. The whole round
    /// trip: find it, read it, rewrite it, verify it.
    /// </summary>
    [Fact]
    public void ALongPathIsScannedAndRewrittenLikeAnyOther()
    {
        // Deep rather than one enormous name: individual components are capped
        // at 255 on both platforms, and it is the total that matters.
        var segment = new string('d', 60);
        var deep = _root;
        for (var i = 0; i < 5; i++) deep = Path.Combine(deep, $"{segment}{i}");

        try
        {
            Directory.CreateDirectory(deep);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The platform will not make a path this long, so there is nothing
            // here to hold the tool to.
            return;
        }

        Assert.True(deep.Length > 260, $"path is only {deep.Length} characters");

        var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (x, y) => ((byte)x, (byte)y, (byte)0, (byte)255))
            .Write();
        foreach (var file in Directory.GetFiles(fixture.Directory))
            File.Copy(file, Path.Combine(deep, Path.GetFileName(file)));
        fixture.Dispose();

        var found = BundleDiscovery.Scan(_root, out var unreadable);

        Assert.Empty(unreadable);
        var bundle = Assert.Single(found);
        Assert.True(bundle.GpuSize > 0);

        // And it can be opened and rewritten where it lies, which is the part a
        // path limit would break well after the listing looked fine.
        var loaded = Bundle.Load(bundle.Path);
        using var gpu = GpuResourceFile.Open(loaded.GpuResourcePath);
        var plan = OptimizationPlan.Build(loaded, gpu);
        var result = BundleOptimizer.Apply(plan, gpu, loaded.Path, loaded.GpuResourcePath,
                                           new EncodeOptions { Quality = EncodeQuality.Fast });

        Assert.True(result.NewGpuSize < bundle.GpuSize);
        Assert.True(BundleVerifier.Verify(loaded.Path).Passed);
    }

    [Fact]
    public void ScanningSomewhereThatDoesNotExistSaysSo() =>
        Assert.Throws<DirectoryNotFoundException>(
            () => BundleDiscovery.Scan(Path.Combine(_root, "nope")));

    [Fact]
    public void AnEmptyFolderFindsNothing()
    {
        Directory.CreateDirectory(_root);
        Assert.Empty(BundleDiscovery.Scan(_root));
    }
}
