// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

/// <summary>
/// Converting a texture to a streamed one moves its chain into .stream and
/// leaves a small resident tail behind. Nothing is discarded and nothing is
/// re-encoded, so every byte must be traceable back to the original.
/// </summary>
public class StreamConversionTests
{
    private static readonly EncodeOptions Fast =
        new() { Quality = EncodeQuality.Fast, ThreadCount = 2 };

    [Fact]
    public void StreamingIsOffUnlessAskedFor()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);

        var off = OptimizationPlan.Build(bundle, gpu);
        Assert.All(off.Textures, t => Assert.False(t.IsStreamConversion));
        Assert.Equal(0, off.StreamConversionCount);

        var on = OptimizationPlan.Build(bundle, gpu, streamFloor: 32);
        Assert.Equal(1, on.StreamConversionCount);
    }

    [Fact]
    public void TheChainMovesToStreamAndOnlyTheTailStaysResident()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var entry = bundle.Textures.Single();

        byte[] original;
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            original = gpu.Read(entry.GpuOffset, entry.GpuSize);
            var plan = OptimizationPlan.Build(bundle, gpu, streamFloor: 32);
            var item = Assert.Single(plan.Textures);

            Assert.True(item.IsStreamConversion);
            Assert.Equal(3, item.StreamResidentMip);   // 256 -> 128 -> 64 -> 32
            Assert.True(plan.AddedStreamBytes > 0);

            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var after = rebuilt.Textures.Single();
        var chain = DxgiFormat.Bc7Unorm.MipChain(256, 256, 9);

        // .stream holds the whole chain, byte for byte.
        Assert.Equal(chain.Sum(), after.StreamSize);
        using var stream = GpuResourceFile.Open(rebuilt.StreamPath);
        Assert.Equal(original, stream.Read(after.StreamOffset, after.StreamSize));

        // .gpu_resources holds only the tail, and it is the tail of the stream
        // region — exactly the relationship real streamed textures have.
        Assert.Equal(chain.Skip(3).Sum(), after.GpuSize);
        using var gpu2 = GpuResourceFile.Open(rebuilt.GpuResourcePath);
        var resident = gpu2.Read(after.GpuOffset, after.GpuSize);
        Assert.Equal(original.AsSpan((int)chain.Take(3).Sum()).ToArray(), resident);
        Assert.Equal(resident,
            stream.Read(after.StreamOffset + after.StreamSize - after.GpuSize, after.GpuSize));
    }

    [Fact]
    public void TheHeaderDescribesTheChainAndWhereResidencyStarts()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, streamFloor: 32);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        var payload = rebuilt.GetCpuPayload(entry);

        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(
            payload[StingrayTexturePrefix.StreamingFlagOffset..]));         // 9 levels
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(
            payload[StingrayTexturePrefix.FirstResidentMipOffset..]));

        var chain = DxgiFormat.Bc7Unorm.MipChain(256, 256, 9);
        Assert.Equal((uint)chain.Sum(), BinaryPrimitives.ReadUInt32LittleEndian(
            payload[StingrayTexturePrefix.ChainSizeOffset..]));

        // Every level entry: dimensions, the offset of the next level, and what
        // remains after it — with the last terminating on zeros.
        long running = 0;
        for (var level = 0; level < 9; level++)
        {
            var e = payload[(StingrayTexturePrefix.LevelTableOffset
                             + level * StingrayTexturePrefix.LevelEntrySize)..];
            Assert.Equal(256 >> level, BinaryPrimitives.ReadUInt16LittleEndian(e));
            Assert.Equal(256 >> level, BinaryPrimitives.ReadUInt16LittleEndian(e[2..]));

            running += chain[level];
            var expectedNext = level == 8 ? 0 : running;
            var expectedRest = level == 8 ? 0 : chain.Sum() - running;
            Assert.Equal((uint)expectedNext, BinaryPrimitives.ReadUInt32LittleEndian(e[4..]));
            Assert.Equal((uint)expectedRest, BinaryPrimitives.ReadUInt32LittleEndian(e[8..]));
        }

        // Dimensions, format and level count are untouched: nothing was lost.
        Assert.True(DdsHeader.TryRead(payload, out var dds));
        Assert.Equal(256, dds!.Width);
        Assert.Equal(9, dds.MipMapCount);
        Assert.Equal(DxgiFormat.Bc7Unorm, dds.DxgiFormat);

        // Streamed textures clear the linear-size flag and zero the size field.
        Assert.Equal(0u, dds.Flags & DdsHeader.FlagLinearSize);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload[(dds.Offset + 0x14)..]));

        // And the tool recognises its own output as a streamed texture.
        Assert.True(TextureResource.TryCreate(rebuilt, entry, out var texture));
        Assert.True(texture!.IsStreamed);
        Assert.Equal(3u, texture.FirstResidentMip);
    }

    [Fact]
    public void ExistingStreamedPayloadsAreCarriedThrough()
    {
        // One texture already streamed, one to convert. The first must survive
        // untouched while the second is appended.
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .AddStreamedChain(128, 128, DxgiFormat.Bc7Unorm, 8, firstResidentMip: 3)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var streamed = bundle.Textures.Single(t => t.HasStreamData);
        byte[] before;
        using (var source = GpuResourceFile.Open(bundle.StreamPath))
            before = source.Read(streamed.StreamOffset, streamed.StreamSize);

        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, streamFloor: 32);
            Assert.Equal(1, plan.StreamConversionCount);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        using var after = GpuResourceFile.Open(rebuilt.StreamPath);
        var kept = rebuilt.Files.Single(f => f.FileId == streamed.FileId);
        Assert.Equal(streamed.StreamSize, kept.StreamSize);
        Assert.Equal(before, after.Read(kept.StreamOffset, kept.StreamSize));

        // Every stream payload is 64-byte aligned and the file ends at the last one.
        foreach (var f in rebuilt.StreamBackedFiles)
            Assert.Equal(0UL, f.StreamOffset % (ulong)BundleFormat.GpuAlignment);
        Assert.Equal(after.Length,
            rebuilt.StreamBackedFiles.Max(f => (long)f.StreamOffset + f.StreamSize));
    }

    [Theory]
    [InlineData(3, 1u)]
    [InlineData(8, 1u)]
    [InlineData(9, 2u)]
    [InlineData(10, 2u)]
    [InlineData(13, 2u)]
    public void TheStreamingFlagFollowsTheObservedSplit(int mipCount, uint expected) =>
        Assert.Equal(expected, StingrayTexturePrefix.StreamingFlagFor(mipCount));

    [Fact]
    public void AChainTooLongToDescribeIsLeftAlone()
    {
        // The table has to fit in the prefix ahead of the DDS magic.
        Assert.True(StingrayTexturePrefix.CanDescribe(192, 14));
        Assert.False(StingrayTexturePrefix.CanDescribe(192, 15));
    }

    /// <summary>
    /// Writing to another directory must take the stream file along. The table
    /// keeps pointing into it, so leaving it behind produces a bundle whose
    /// streamed textures have no data at all.
    /// </summary>
    [Fact]
    public void WritingElsewhereCarriesTheStreamFileAlong()
    {
        using var fixture = new SyntheticBundle()
            .AddStreamedChain(128, 128, DxgiFormat.Bc7Unorm, 8, firstResidentMip: 3)
            .AddTexture(64, 64, (_, _) => (10, 20, 30, 255))
            .Write();

        var output = Path.Combine(fixture.Directory, "out");
        Directory.CreateDirectory(output);
        var outBundle = Path.Combine(output, Path.GetFileName(fixture.BundlePath));

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu);      // streaming off
            BundleOptimizer.Apply(plan, gpu, outBundle, outBundle + ".gpu_resources", Fast,
                                  outputStreamPath: outBundle + ".stream");
        }

        Assert.True(File.Exists(outBundle + ".stream"),
            "the stream file must travel with the bundle that references it");
        Assert.Equal(new FileInfo(bundle.StreamPath).Length,
                     new FileInfo(outBundle + ".stream").Length);

        var rebuilt = Bundle.Load(outBundle);
        var streamed = rebuilt.Files.Single(f => f.HasStreamData);
        using var before = GpuResourceFile.Open(bundle.StreamPath);
        using var after = GpuResourceFile.Open(rebuilt.StreamPath);
        Assert.Equal(before.Read(streamed.StreamOffset, streamed.StreamSize),
                     after.Read(streamed.StreamOffset, streamed.StreamSize));
    }
}
