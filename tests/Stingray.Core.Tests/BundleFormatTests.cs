// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Format;
using Xunit;

namespace Stingray.Core.Tests;

public class BundleFormatTests
{
    [Fact]
    public void ParsesHeaderTypeTableAndEntries()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (_, _) => (10, 20, 30, 255))
            .AddOpaqueAsset(1024, seed: 7)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);

        Assert.Equal(BundleFormat.Magic, bundle.Header.Magic);
        Assert.Equal(2ul, bundle.Header.FileCount);
        Assert.Equal(2, bundle.Types.Count);
        Assert.Equal(2, bundle.Files.Count);

        var texture = Assert.Single(bundle.Textures);
        Assert.Equal("texture", texture.TypeName);
        Assert.Equal(64u * 64 * 4, texture.GpuSize);
    }

    [Fact]
    public void EveryGpuPayloadIsAlignedAndInBounds()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(32, 32, (_, _) => (1, 2, 3, 255))
            .AddOpaqueAsset(100, seed: 1)      // deliberately not a multiple of 64
            .AddOpaqueAsset(300, seed: 2)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);

        foreach (var entry in bundle.GpuBackedFiles)
        {
            Assert.Equal(0ul, entry.GpuOffset % BundleFormat.GpuAlignment);
            Assert.True((long)entry.GpuOffset + entry.GpuSize <= gpu.Length);
        }
    }

    [Fact]
    public void RejectsFilesThatAreNotBundles()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[256]);
            var ex = Assert.Throws<InvalidDataException>(() => Bundle.Load(path));
            Assert.Contains("Not a Stingray bundle", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownTypeIdsRenderAsHex()
    {
        Assert.Equal("texture", StingrayTypeIds.NameOf(StingrayTypeIds.Texture));
        Assert.Equal("0xDEADBEEFDEADBEEF", StingrayTypeIds.NameOf(0xDEAD_BEEF_DEAD_BEEF));
    }
}
