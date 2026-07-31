// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

/// <summary>
/// Shrinking a mipmapped texture means discarding its top levels, not
/// re-encoding it. Every level that survives is the author's own data, so the
/// bytes must come through untouched.
/// </summary>
public class MipDropTests
{
    private static readonly EncodeOptions Fast =
        new() { Quality = EncodeQuality.Fast, ThreadCount = 2 };

    [Theory]
    [InlineData(4096, 13, 2048, 1)]
    [InlineData(4096, 13, 1024, 2)]
    [InlineData(4096, 13, 512, 3)]
    [InlineData(1024, 11, 2048, 0)]   // already within the cap
    [InlineData(1024, 11, 0, 0)]      // no cap requested
    public void LevelsDroppedMatchTheRequestedCap(int size, int mips, int cap, int expected) =>
        Assert.Equal(expected, DxgiFormatInfo.LevelsToDrop(size, size, mips, cap));

    [Fact]
    public void TheWholeChainIsNeverDropped()
    {
        // A cap smaller than the smallest level must still leave something.
        Assert.Equal(2, DxgiFormatInfo.LevelsToDrop(16, 16, 3, 1));
    }

    [Fact]
    public void SurvivingLevelsAreByteIdenticalToTheOriginal()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var entry = bundle.Textures.Single();

        byte[] before;
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            before = gpu.Read(entry.GpuOffset, entry.GpuSize);
            var plan = OptimizationPlan.Build(bundle, gpu, maxDimension: 64);
            var item = Assert.Single(plan.Textures);

            Assert.True(item.IsMipDrop);
            Assert.Equal(2, item.MipLevelsToDrop);
            Assert.Equal(64, item.TargetWidth);

            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var after = rebuilt.Textures.Single();
        using var newGpu = GpuResourceFile.Open(rebuilt.GpuResourcePath);

        // The payload must be exactly the tail of the original chain.
        var chain = DxgiFormat.Bc7Unorm.MipChain(256, 256, 9);
        var skipped = chain.Take(2).Sum();
        Assert.Equal(before.AsSpan((int)skipped).ToArray(),
                     newGpu.Read(after.GpuOffset, after.GpuSize));
    }

    [Fact]
    public void HeaderIsRewrittenToDescribeTheShorterChain()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, maxDimension: 64);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        Assert.True(TextureResource.TryCreate(rebuilt, entry, out var texture));

        Assert.Equal(64, texture!.Width);
        Assert.Equal(7, texture.Header.MipMapCount);          // 9 - 2
        Assert.Equal(DxgiFormat.Bc7Unorm, texture.SourceFormat);
        // Linear size describes level 0, while the payload is the whole chain.
        Assert.Equal(DxgiFormat.Bc7Unorm.SurfaceSize(64, 64), texture.Header.PitchOrLinearSize);
        Assert.Equal(DxgiFormat.Bc7Unorm.MipChain(64, 64, 7).Sum(), entry.GpuSize);

        var report = BundleVerifier.Verify(fixture.BundlePath);
        Assert.True(report.Passed,
            "verification failed: " + string.Join("; ", report.Issues.Select(i => i.Detail)));
    }

    [Fact]
    public void StreamedTexturesAreLeftAlone()
    {
        // Their top levels live in .stream, so slicing gpu_resources alone would
        // desynchronise the two files.
        using var fixture = new SyntheticBundle()
            .AddStreamedTexture(512, 512, mipCount: 10, residentTailSize: 1392, streamSize: 349552)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
        var plan = OptimizationPlan.Build(bundle, gpu, maxDimension: 128);

        Assert.Empty(plan.Textures);
        Assert.Contains(plan.Skipped, s => s.Reason.Contains("streamed"));
    }
}
