// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

/// <summary>
/// Mods routinely ship the same texture under many ids. The format stores an
/// explicit (offset, size) per entry, so identical payloads can be written once
/// and shared.
/// </summary>
public class DeduplicationTests
{
    private static readonly EncodeOptions Fast =
        new() { Quality = EncodeQuality.Fast, ThreadCount = 2 };

    [Fact]
    public void IdenticalPayloadsAreWrittenOnceAndShared()
    {
        using var fixture = new SyntheticBundle()
            .AddOpaqueAsset(8192, seed: 5)
            .AddOpaqueAsset(8192, seed: 5)   // byte-identical
            .AddOpaqueAsset(8192, seed: 6)   // different
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        OptimizationResult result;
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            Assert.Equal(1, plan.DuplicateEntryCount);
            Assert.Equal(8192, plan.RedundantBytes);
            result = BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        Assert.Equal(1, result.PayloadsDeduplicated);
        Assert.Equal(8192, result.DeduplicatedBytes);

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var offsets = rebuilt.GpuBackedFiles.Select(f => f.GpuOffset).ToList();
        Assert.Equal(2, offsets.Distinct().Count());   // three entries, two payloads
    }

    [Fact]
    public void EveryEntryStillResolvesToItsOriginalBytes()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (_, _) => (3, 3, 3, 255))
            .AddTexture(64, 64, (_, _) => (3, 3, 3, 255))
            .AddOpaqueAsset(2048, seed: 11)
            .AddOpaqueAsset(2048, seed: 11)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var before = new Dictionary<ulong, byte[]>();
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            foreach (var e in bundle.GpuBackedFiles)
                before[e.FileId] = gpu.Read(e.GpuOffset, e.GpuSize);
        }

        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        using var newGpu = GpuResourceFile.Open(rebuilt.GpuResourcePath);

        foreach (var e in rebuilt.GpuBackedFiles)
        {
            // Textures were re-encoded, so only compare the passthrough assets;
            // what matters is that a shared payload still serves both entries.
            if (e.IsTexture) continue;
            Assert.Equal(before[e.FileId], newGpu.Read(e.GpuOffset, e.GpuSize));
        }
    }

    [Fact]
    public void SharedPayloadsAreNotReportedAsOverlaps()
    {
        using var fixture = new SyntheticBundle()
            .AddOpaqueAsset(4096, seed: 2)
            .AddOpaqueAsset(4096, seed: 2)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var report = BundleVerifier.Verify(fixture.BundlePath);
        Assert.True(report.Passed,
            "verification failed: " + string.Join("; ", report.Issues.Select(i => i.Detail)));
        Assert.Equal(1, report.AliasedEntries);
        Assert.DoesNotContain(report.Issues, i => i.Category == "overlap");
    }

    [Fact]
    public void DeduplicationCanBeDisabled()
    {
        using var fixture = new SyntheticBundle()
            .AddOpaqueAsset(4096, seed: 8)
            .AddOpaqueAsset(4096, seed: 8)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        OptimizationResult result;
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            plan.Deduplicate = false;
            result = BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath,
                                           Fast, progress: null, deduplicate: false);
        }

        Assert.Equal(0, result.PayloadsDeduplicated);

        var rebuilt = Bundle.Load(fixture.BundlePath);
        Assert.Equal(2, rebuilt.GpuBackedFiles.Select(f => f.GpuOffset).Distinct().Count());
    }

    [Fact]
    public void PredictedSizeAccountsForSharing()
    {
        using var fixture = new SyntheticBundle()
            .AddOpaqueAsset(65536, seed: 4)
            .AddOpaqueAsset(65536, seed: 4)
            .AddOpaqueAsset(65536, seed: 4)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
        var plan = OptimizationPlan.Build(bundle, gpu);

        var deduped = plan.PredictedGpuSize;
        plan.Deduplicate = false;
        var plain = plan.PredictedGpuSize;

        Assert.Equal(65536, deduped);
        Assert.True(plain > deduped * 2, $"expected sharing to dominate, {plain} vs {deduped}");
    }

    [Fact]
    public void SharingShrinksTheFileButNotTheGpuFootprint()
    {
        // Each entry becomes its own GPU resource regardless of where its bytes
        // live, so sharing saves disk and download, never video memory.
        using var fixture = new SyntheticBundle()
            .AddOpaqueAsset(65536, seed: 3)
            .AddOpaqueAsset(65536, seed: 3)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
        var plan = OptimizationPlan.Build(bundle, gpu);

        Assert.Equal(65536, plan.PredictedGpuSize);          // one payload on disk
        Assert.Equal(131072, plan.PredictedGpuFootprint);    // two GPU resources
        Assert.Equal(0, plan.PredictedFootprintSaving);      // nothing was shrunk
    }

    [Fact]
    public void ShrinkingASurfaceDoesReduceTheGpuFootprint()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(128, 128, (_, _) => (0, 0, 0, 255))   // solid -> collapses
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
        var plan = OptimizationPlan.Build(bundle, gpu);

        Assert.True(plan.PredictedFootprintSaving > 0,
            "collapsing a solid colour must reduce what the GPU allocates");
        Assert.True(plan.PredictedGpuFootprint < plan.CurrentGpuFootprint);
    }

    [Fact]
    public void StreamedTexturesAreNotFlaggedForPartialResidency()
    {
        // A texture whose mip chain lives in the .stream file keeps only a tail in
        // gpu_resources, so its GPU size legitimately differs from the surface size.
        // This mirrors a real bundle: 512x512 BC7, 10 mips, 1392 resident bytes.
        using var fixture = new SyntheticBundle()
            .AddStreamedTexture(512, 512, mipCount: 10, residentTailSize: 1392, streamSize: 349552)
            .Write();

        var report = BundleVerifier.Verify(fixture.BundlePath);
        Assert.True(report.Passed,
            "verification failed: " + string.Join("; ", report.Issues.Select(i => i.Detail)));
    }

    [Fact]
    public void UncompressedTexturesAreNotJudgedByLinearSize()
    {
        // An uncompressed DDS stores a per-row pitch, not the payload size.
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (_, _) => (1, 2, 3, 255))
            .Write();

        var report = BundleVerifier.Verify(fixture.BundlePath);
        Assert.True(report.Passed,
            "verification failed: " + string.Join("; ", report.Issues.Select(i => i.Detail)));
    }
}
