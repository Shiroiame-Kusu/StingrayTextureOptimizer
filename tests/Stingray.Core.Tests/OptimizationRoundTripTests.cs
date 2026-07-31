// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

public class OptimizationRoundTripTests
{
    private static readonly EncodeOptions Fast =
        new() { Quality = EncodeQuality.Fast, ThreadCount = 2 };

    [Fact]
    public void ShrinksBundleAndPassesVerification()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(128, 128, (_, _) => (0, 0, 0, 255))                       // solid
            .AddTexture(64, 64, (x, _) => (127, 127, 255, (byte)(x % 2 == 0 ? 115 : 179)))
            .AddTexture(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), 255)) // opaque detail
            .AddOpaqueAsset(4096, seed: 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var originalGpuSize = new FileInfo(bundle.GpuResourcePath).Length;

        var backup = Path.Combine(fixture.Directory, "backup");
        BundleOptimizer.CreateBackup(bundle, backup);

        OptimizationResult result;
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            Assert.Equal(3, plan.Textures.Count);
            result = BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        Assert.True(result.NewGpuSize < originalGpuSize,
            $"expected shrink, got {originalGpuSize} -> {result.NewGpuSize}");

        var report = BundleVerifier.Verify(
            fixture.BundlePath, Path.Combine(backup, Path.GetFileName(fixture.BundlePath)));

        Assert.True(report.Passed,
            "verification failed: " + string.Join("; ", report.Issues.Select(i => i.Detail)));
        Assert.Equal(1, report.PayloadsCompared);
    }

    [Fact]
    public void NonTextureDataSurvivesByteForByte()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (_, _) => (5, 5, 5, 255))
            .AddOpaqueAsset(3000, seed: 42)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var unit = bundle.Files.Single(f => !f.IsTexture);

        byte[] before;
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            before = gpu.Read(unit.GpuOffset, unit.GpuSize);
            var plan = OptimizationPlan.Build(bundle, gpu);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var rebuiltUnit = rebuilt.Files.Single(f => !f.IsTexture);
        using var newGpu = GpuResourceFile.Open(rebuilt.GpuResourcePath);

        Assert.Equal(before, newGpu.Read(rebuiltUnit.GpuOffset, rebuiltUnit.GpuSize));
    }

    [Fact]
    public void RewritesDdsHeadersToMatchTheNewPayload()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x + y), (byte)(x * 3)))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        Assert.True(TextureResource.TryCreate(rebuilt, entry, out var texture));

        Assert.Equal(DxgiFormat.Bc7Unorm, texture!.SourceFormat);
        Assert.Equal(entry.GpuSize, texture.Header.PitchOrLinearSize);
        Assert.NotEqual(0u, texture.Header.Flags & DdsHeader.FlagLinearSize);
        Assert.Equal(0u, texture.Header.Flags & DdsHeader.FlagPitch);
        Assert.Equal(texture.SourceFormat.SurfaceSize(64, 64), entry.GpuSize);
    }

    [Fact]
    public void CpuPayloadOffsetsAndSizesAreUnchanged()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (_, _) => (9, 9, 9, 255))
            .AddOpaqueAsset(512, seed: 3)
            .Write();

        var before = Bundle.Load(fixture.BundlePath).Files
            .Select(f => (f.FileId, f.Offset, f.Size)).ToList();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var after = Bundle.Load(fixture.BundlePath).Files
            .Select(f => (f.FileId, f.Offset, f.Size)).ToList();

        Assert.Equal(before, after);
    }

    [Fact]
    public void ExcludedTexturesAreLeftAlone()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x + y), 255))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var originalSize = bundle.Textures.Single().GpuSize;

        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            plan.Textures[0].Include = false;
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        Assert.Equal(originalSize, rebuilt.Textures.Single().GpuSize);
    }

    [Fact]
    public void BackupRefusesToOverwriteAnExistingOne()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(32, 32, (_, _) => (1, 1, 1, 255))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var backup = Path.Combine(fixture.Directory, "backup");

        BundleOptimizer.CreateBackup(bundle, backup);
        Assert.Throws<IOException>(() => BundleOptimizer.CreateBackup(bundle, backup));
    }

    [Fact]
    public void OptimizingTwiceIsIdempotent()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x + y), (byte)x))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        // A second pass with no resize requested must find nothing worth doing,
        // rather than re-encoding BC7 into BC7 and compounding generation loss.
        var rebuilt = Bundle.Load(fixture.BundlePath);
        using var newGpu = GpuResourceFile.Open(rebuilt.GpuResourcePath);
        var second = OptimizationPlan.Build(rebuilt, newGpu);

        Assert.Empty(second.Textures);
        Assert.Contains(second.Skipped, s => s.Reason.Contains("already efficient"));
    }
}
