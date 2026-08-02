// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
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
