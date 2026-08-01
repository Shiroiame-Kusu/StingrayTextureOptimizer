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

    /// <summary>
    /// The prefix carries an id at offset 0 whose meaning is not known and which
    /// nothing in the file derives — so it can only be preserved, never rebuilt.
    /// Real streamed textures carry it, which means writing the streaming
    /// description must not take the rest of the prefix with it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertingKeepsTheUnknownIdAtTheStartOfThePrefix(bool generated)
    {
        using var fixture = (generated
                ? new SyntheticBundle().AddTexture(128, 128, (x, y) => ((byte)x, (byte)y, (byte)0, (byte)255))
                : new SyntheticBundle().AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        Assert.Equal(SyntheticBundle.PrefixId, ReadPrefixId(bundle));

        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, streamFloor: 32, generateMips: generated);
            Assert.True(Assert.Single(plan.Textures).IsStreamConversion);

            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        Assert.Equal(SyntheticBundle.PrefixId, ReadPrefixId(Bundle.Load(fixture.BundlePath)));
    }

    private static uint ReadPrefixId(Bundle bundle) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bundle.GetCpuPayload(bundle.Textures.Single()));

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

    /// <summary>
    /// A floor at or above the texture's own size would leave every level
    /// resident and still write a second copy into .stream: the whole disk cost
    /// for no saving at all.
    /// </summary>
    [Theory]
    [InlineData(2048)]   // larger than the texture
    [InlineData(256)]    // exactly the texture
    public void AFloorThatSavesNothingIsNotConverted(int floor)
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
        var plan = OptimizationPlan.Build(bundle, gpu, streamFloor: floor);

        Assert.Equal(0, plan.StreamConversionCount);
        Assert.Equal(0, plan.AddedStreamBytes);
        Assert.All(plan.Textures, t => Assert.False(t.IsStreamConversion));
    }

    /// <summary>A floor below the texture still converts, as the contrast.</summary>
    [Fact]
    public void AFloorThatSavesSomethingStillConverts()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
        Assert.Equal(1, OptimizationPlan.Build(bundle, gpu, streamFloor: 128).StreamConversionCount);
    }

    /// <summary>
    /// The size cap and the resident floor compose: the cap discards levels above
    /// it for good, and what survives goes to .stream with the floor resident.
    /// </summary>
    [Fact]
    public void TheSizeCapAndTheResidentFloorCompose()
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
            var plan = OptimizationPlan.Build(bundle, gpu, maxDimension: 64, streamFloor: 16);
            var item = Assert.Single(plan.Textures);

            Assert.True(item.IsStreamConversion);
            Assert.Equal(2, item.MipLevelsToDrop);       // 256, 128 discarded
            Assert.Equal(64, item.TargetWidth);
            Assert.Equal(7, item.TargetMipCount);        // 64 down to 1
            Assert.Equal(2, item.StreamResidentMip);     // 64 -> 32 -> 16

            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var after = rebuilt.Textures.Single();
        var chain = DxgiFormat.Bc7Unorm.MipChain(256, 256, 9);

        // .stream holds 64 and below, not the original 256 chain.
        Assert.Equal(chain.Skip(2).Sum(), after.StreamSize);
        using var stream = GpuResourceFile.Open(rebuilt.StreamPath);
        Assert.Equal(original.AsSpan((int)chain.Take(2).Sum()).ToArray(),
                     stream.Read(after.StreamOffset, after.StreamSize));

        // Resident is 16 and below.
        Assert.Equal(chain.Skip(4).Sum(), after.GpuSize);
        using var gpu2 = GpuResourceFile.Open(rebuilt.GpuResourcePath);
        Assert.Equal(original.AsSpan((int)chain.Take(4).Sum()).ToArray(),
                     gpu2.Read(after.GpuOffset, after.GpuSize));

        // The header describes the capped texture, not the original.
        var payload = rebuilt.GetCpuPayload(after);
        Assert.True(DdsHeader.TryRead(payload, out var dds));
        Assert.Equal(64, dds!.Width);
        Assert.Equal(7, dds.MipMapCount);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(
            payload[StingrayTexturePrefix.FirstResidentMipOffset..]));
        Assert.Equal((uint)chain.Skip(2).Sum(), BinaryPrimitives.ReadUInt32LittleEndian(
            payload[StingrayTexturePrefix.ChainSizeOffset..]));

        // The level table describes the capped chain, starting at 64.
        Assert.Equal(64, BinaryPrimitives.ReadUInt16LittleEndian(
            payload[StingrayTexturePrefix.LevelTableOffset..]));
        // And it now reads back as a streamed texture, so a second pass leaves it
        // alone rather than treating the resident tail as a whole surface.
        Assert.True(TextureResource.TryCreate(rebuilt, after, out var texture));
        Assert.True(texture!.IsStreamed);
        Assert.Equal(2u, texture.FirstResidentMip);
        Assert.Equal(64, texture.Width);
    }

    /// <summary>
    /// A floor at or above what survives the cap can never stream anything, which
    /// is why the GUI stops offering those.
    /// </summary>
    [Fact]
    public void AFloorAboveTheCapConvertsNothing()
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(256, 256, DxgiFormat.Bc7Unorm, 9)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);

        // Capped to 64, so a 128 floor is unreachable.
        Assert.Equal(0, OptimizationPlan.Build(bundle, gpu,
            maxDimension: 64, streamFloor: 128).StreamConversionCount);
        Assert.Equal(1, OptimizationPlan.Build(bundle, gpu,
            maxDimension: 64, streamFloor: 32).StreamConversionCount);
    }

    /// <summary>
    /// The exact combination that corrupted textures in game: cap and floor set in
    /// one pass, so the header is rewritten and the prefix must follow it. The
    /// level table describing different dimensions from the DDS header leaves a
    /// bundle that looks structurally sound but reads every level from the wrong
    /// offset.
    /// </summary>
    [Theory]
    [InlineData(4096, 13, 2048, 1024)]
    [InlineData(4096, 13, 1024, 256)]
    [InlineData(2048, 12, 1024, 512)]
    public void CappingAndStreamingInOnePassKeepsThePrefixAndHeaderInStep(
        int size, int mips, int cap, int floor)
    {
        using var fixture = new SyntheticBundle()
            .AddMippedTexture(size, size, DxgiFormat.Bc7Unorm, mips)
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, maxDimension: cap, streamFloor: floor);
            Assert.Equal(1, plan.StreamConversionCount);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        var payload = rebuilt.GetCpuPayload(entry);
        Assert.True(DdsHeader.TryRead(payload, out var dds));

        // The header must have been capped...
        Assert.Equal(cap, dds!.Width);

        // ...and the prefix table must describe that same texture, not one a
        // halving out of step with it.
        Assert.Equal(cap, BinaryPrimitives.ReadUInt16LittleEndian(
            payload[StingrayTexturePrefix.LevelTableOffset..]));

        var chain = DxgiFormat.Bc7Unorm.MipChain(dds.Width, dds.Height, dds.MipMapCount);
        Assert.Equal((uint)chain.Sum(), BinaryPrimitives.ReadUInt32LittleEndian(
            payload[StingrayTexturePrefix.ChainSizeOffset..]));
        Assert.Equal(chain.Sum(), entry.StreamSize);

        // And the verifier agrees, which is the check that would have caught it.
        var report = BundleVerifier.Verify(rebuilt.Path);
        Assert.True(report.Passed, string.Join("; ", report.Issues.Select(i => i.Detail)));
    }

    /// <summary>
    /// A prefix table that disagrees with the DDS header must fail verification.
    /// Corrupted deliberately, because the writer no longer produces this.
    /// </summary>
    [Fact]
    public void VerificationRejectsAPrefixTableThatContradictsTheHeader()
    {
        using var fixture = new SyntheticBundle()
            .AddStreamedChain(256, 256, DxgiFormat.Bc7Unorm, 9, firstResidentMip: 3)
            .Write();

        Assert.True(BundleVerifier.Verify(fixture.BundlePath).Passed);

        // Halve the dimensions the level table claims, exactly as the aliasing bug
        // did, and leave everything else alone.
        var image = File.ReadAllBytes(fixture.BundlePath);
        var entry = Bundle.Load(fixture.BundlePath).Textures.Single();
        var at = (int)entry.Offset + StingrayTexturePrefix.LevelTableOffset;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), 128);
        File.WriteAllBytes(fixture.BundlePath, image);

        var report = BundleVerifier.Verify(fixture.BundlePath);
        Assert.False(report.Passed);
        Assert.Contains(report.Issues, i => i.Category == "stream-table");
    }

    /// <summary>A streamed entry pointing past the end of .stream must be caught.</summary>
    [Fact]
    public void VerificationRejectsAStreamRegionPastTheEndOfTheFile()
    {
        using var fixture = new SyntheticBundle()
            .AddStreamedChain(256, 256, DxgiFormat.Bc7Unorm, 9, firstResidentMip: 3)
            .Write();

        var image = File.ReadAllBytes(fixture.BundlePath);
        var entry = Bundle.Load(fixture.BundlePath).Textures.Single();
        entry.WriteStreamFields(image, entry.StreamOffset + 1024 * 1024, entry.StreamSize);
        File.WriteAllBytes(fixture.BundlePath, image);

        var report = BundleVerifier.Verify(fixture.BundlePath);
        Assert.False(report.Passed);
        Assert.Contains(report.Issues, i => i.Category is "stream-bounds" or "stream-alignment");
    }
}
