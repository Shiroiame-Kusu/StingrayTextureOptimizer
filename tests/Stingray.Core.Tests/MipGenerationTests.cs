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
/// Many mods ship textures with no mip chain at all, which shimmer when minified
/// and cannot be streamed. Building one is the only path here that produces mip
/// levels rather than moving them, so the whole texture is re-encoded.
/// </summary>
public class MipGenerationTests
{
    private static readonly EncodeOptions Fast =
        new() { Quality = EncodeQuality.Fast, ThreadCount = 2 };

    [Fact]
    public void GenerationIsOffUnlessAskedFor()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), 255))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);

        Assert.Equal(0, OptimizationPlan.Build(bundle, gpu).GeneratedChainCount);
        Assert.Equal(1, OptimizationPlan.Build(bundle, gpu, generateMips: true).GeneratedChainCount);
    }

    [Fact]
    public void AFullChainIsBuiltDownToOnePixel()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), (byte)(x ^ y), 255))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, generateMips: true);
            var item = Assert.Single(plan.Textures);

            Assert.True(item.GeneratesMips);
            Assert.Equal(7, item.TargetMipCount);       // 64,32,16,8,4,2,1
            Assert.Equal(64, item.TargetWidth);

            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        Assert.True(TextureResource.TryCreate(rebuilt, entry, out var texture));

        // The payload is exactly a chain in the recorded format, and the header
        // agrees — which is what the verifier then re-checks independently.
        Assert.Equal(7, texture!.MipMapCount);
        Assert.Equal(texture.SourceFormat.MipChain(64, 64, 7).Sum(), entry.GpuSize);
        Assert.Null(texture.Unsupported);
        Assert.True(BundleVerifier.Verify(rebuilt.Path).Passed);
    }

    /// <summary>
    /// dwCaps has to move with the level count. A surface claiming one level while
    /// still flagged MIPMAP, or a chain that is not flagged, is a header that does
    /// not describe its own payload.
    /// </summary>
    [Fact]
    public void TheMipmapCapsFollowTheLevelCount()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (x, y) => ((byte)x, (byte)y, 0, 255))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, generateMips: true);
            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        var payload = rebuilt.GetCpuPayload(entry);
        Assert.True(DdsHeader.TryRead(payload, out var dds));

        var caps = BinaryPrimitives.ReadUInt32LittleEndian(payload[(dds!.Offset + 0x6C)..]);
        Assert.Equal(DdsHeader.CapsMipMap, caps & DdsHeader.CapsMipMap);
        Assert.Equal(DdsHeader.CapsComplex, caps & DdsHeader.CapsComplex);
        Assert.Equal(DdsHeader.CapsTexture, caps & DdsHeader.CapsTexture);
    }

    /// <summary>
    /// Generating and streaming together is the combination worth having: the
    /// chain exists only so most of it can leave video memory again.
    /// </summary>
    [Fact]
    public void GeneratingAndStreamingTogetherLeavesOnlyTheTailResident()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(128, 128, (x, y) => ((byte)x, (byte)y, (byte)(x + y), 255))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        var before = bundle.Textures.Single().GpuSize;

        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, generateMips: true, streamFloor: 16);
            var item = Assert.Single(plan.Textures);

            Assert.True(item.GeneratesMips);
            Assert.True(item.IsStreamConversion);
            Assert.Equal(3, item.StreamResidentMip);    // 128 -> 64 -> 32 -> 16

            BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast,
                                  outputStreamPath: bundle.StreamPath);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        Assert.True(TextureResource.TryCreate(rebuilt, entry, out var texture));

        var chain = texture!.SourceFormat.MipChain(128, 128, texture.MipMapCount);
        Assert.Equal(chain.Sum(), entry.StreamSize);            // whole chain streamed
        Assert.Equal(chain.Skip(3).Sum(), entry.GpuSize);       // only 16 and below resident
        Assert.True(entry.GpuSize < before / 100);              // 128x128 RGBA8 -> a few hundred bytes

        // The resident payload is the tail of the stream region, as with any
        // streamed texture, and the whole thing verifies.
        using var stream = GpuResourceFile.Open(rebuilt.StreamPath);
        using var gpu2 = GpuResourceFile.Open(rebuilt.GpuResourcePath);
        Assert.Equal(gpu2.Read(entry.GpuOffset, entry.GpuSize),
                     stream.Read(entry.StreamOffset + entry.StreamSize - entry.GpuSize, entry.GpuSize));

        var report = BundleVerifier.Verify(rebuilt.Path);
        Assert.True(report.Passed, string.Join("; ", report.Issues.Select(i => i.Detail)));
    }

    /// <summary>
    /// Every level has to come back the size the chain layout says, including the
    /// 2x2 and 1x1 blocks that CMP_Core refuses to encode.
    /// </summary>
    [Theory]
    [InlineData(EncoderBackend.Auto)]
    [InlineData(EncoderBackend.Managed)]
    [InlineData(EncoderBackend.Compressonator)]
    public void EveryLevelIsProducedIncludingTheOnesBelowABlock(EncoderBackend backend)
    {
        const int size = 32;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 31 + 5);

        var options = new EncodeOptions
        {
            Quality = EncodeQuality.Fast, ThreadCount = 2, Backend = backend,
        };

        var levels = TextureEncoder.FullMipCount(size, size);
        Assert.Equal(6, levels);                                 // 32,16,8,4,2,1

        var built = TextureEncoder.EncodeChain(
            rgba, size, size, DxgiFormat.R8G8B8A8Unorm,
            size, size, DxgiFormat.Bc7Unorm, levels, options);

        Assert.Equal(DxgiFormat.Bc7Unorm.MipChain(size, size, levels).Sum(), built.LongLength);
    }

    [Theory]
    [InlineData(4096, 4096, 13)]
    [InlineData(1024, 512, 11)]
    [InlineData(16, 16, 5)]
    [InlineData(1, 1, 1)]
    public void AFullChainRunsDownToOnePixel(int width, int height, int expected) =>
        Assert.Equal(expected, TextureEncoder.FullMipCount(width, height));

    /// <summary>
    /// A solid colour collapses to 16x16 and samples the same at every level, so a
    /// chain for it would be pure overhead.
    /// </summary>
    [Fact]
    public void SolidColoursAreCollapsedRatherThanGivenAChain()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(64, 64, (_, _) => (10, 20, 30, 255))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        using var gpu = GpuResourceFile.Open(bundle.GpuResourcePath);
        var item = Assert.Single(OptimizationPlan.Build(bundle, gpu, generateMips: true).Textures);

        Assert.False(item.GeneratesMips);
        Assert.Equal(TextureAnalyzer.SolidColourExtent, item.TargetWidth);
    }

    /// <summary>
    /// The generated levels must actually be the image, progressively halved. A
    /// chain of the right size full of the wrong pixels would satisfy every
    /// structural check and still look wrong in game.
    /// </summary>
    [Fact]
    public void EachGeneratedLevelIsADownsampleOfTheImage()
    {
        // Box filtering preserves the mean, so every level of a correct chain has
        // the same average colour as the original — right down to the 1x1, which
        // is that average. Garbage would not.
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var i = (y * size + x) * 4;
            rgba[i] = (byte)(x * 4);
            rgba[i + 1] = (byte)(y * 4);
            rgba[i + 2] = 128;
            rgba[i + 3] = 255;
        }

        static double[] Means(ReadOnlySpan<byte> pixels)
        {
            var sums = new double[4];
            for (var i = 0; i < pixels.Length; i += 4)
                for (var c = 0; c < 4; c++) sums[c] += pixels[i + c];
            var n = pixels.Length / 4;
            for (var c = 0; c < 4; c++) sums[c] /= n;
            return sums;
        }

        var expected = Means(rgba);

        var levels = TextureEncoder.FullMipCount(size, size);
        var built = TextureEncoder.EncodeChain(
            rgba, size, size, DxgiFormat.R8G8B8A8Unorm,
            size, size, DxgiFormat.Bc7Unorm, levels, Fast);

        var chain = DxgiFormat.Bc7Unorm.MipChain(size, size, levels);
        long at = 0;
        for (var level = 0; level < levels; level++)
        {
            var w = Math.Max(1, size >> level);
            var decoded = TextureDecoder.Decode(
                built.AsSpan((int)at, (int)chain[level]), w, w, DxgiFormat.Bc7Unorm);
            at += chain[level];

            var actual = Means(decoded);
            for (var c = 0; c < 4; c++)
                Assert.InRange(actual[c], expected[c] - 6, expected[c] + 6);
        }
    }

    /// <summary>
    /// Level 0 of the generated chain is the same encode the texture would have got
    /// without mips, so adding a chain never changes the image you see up close.
    /// </summary>
    [Fact]
    public void LevelZeroMatchesTheEncodeWithoutAChain()
    {
        const int size = 32;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 17 + 3);

        var alone = TextureEncoder.Encode(rgba, size, size, DxgiFormat.R8G8B8A8Unorm,
                                          size, size, DxgiFormat.Bc7Unorm, Fast);
        var chained = TextureEncoder.EncodeChain(
            rgba, size, size, DxgiFormat.R8G8B8A8Unorm,
            size, size, DxgiFormat.Bc7Unorm, TextureEncoder.FullMipCount(size, size), Fast);

        Assert.Equal(alone, chained.AsSpan(0, alone.Length).ToArray());
    }
}
