// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

/// <summary>
/// Resizing is the one genuinely lossy option the tool offers, so the decision is
/// left to the user and backed by a measurement rather than a guess.
/// </summary>
public class DownscaleTests
{
    private static readonly EncodeOptions Fast =
        new() { Quality = EncodeQuality.Fast, ThreadCount = 2 };

    private static byte[] Surface(int w, int h, Func<int, int, (byte, byte, byte, byte)> fill)
    {
        var d = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b, a) = fill(x, y);
            var i = (y * w + x) * 4;
            d[i] = r; d[i + 1] = g; d[i + 2] = b; d[i + 3] = a;
        }
        return d;
    }

    [Fact]
    public void SmoothContentHasHighDetailHeadroom()
    {
        // A linear gradient survives halving almost exactly.
        var smooth = Surface(128, 128, (x, y) => ((byte)(x * 2), (byte)(y * 2), 128, 255));
        var psnr = TextureAnalyzer.MeasureHalfResolutionPsnr(smooth, 128, 128);
        Assert.True(psnr > 40, $"expected smooth content to score high, got {psnr:F1} dB");
    }

    [Fact]
    public void PixelNoiseHasLowDetailHeadroom()
    {
        // Per-pixel alternation is exactly the detail halving destroys.
        var noisy = Surface(128, 128, (x, y) => ((byte)((x + y) % 2 == 0 ? 0 : 255), 0, 0, 255));
        var psnr = TextureAnalyzer.MeasureHalfResolutionPsnr(noisy, 128, 128);
        Assert.True(psnr < 20, $"expected noise to score low, got {psnr:F1} dB");
    }

    [Fact]
    public void SolidColourReportsInfiniteHeadroom()
    {
        var analysis = TextureAnalyzer.Analyze(Surface(64, 64, (_, _) => (7, 7, 7, 255)), 64, 64);
        Assert.True(double.IsPositiveInfinity(analysis.DetailHeadroomDb));
        Assert.True(analysis.HalvingIsSafe);
    }

    [Theory]
    [InlineData(4096, 2048, 2048)]
    [InlineData(4096, 1024, 1024)]
    [InlineData(2048, 4096, 2048)]   // already within the cap
    [InlineData(512, 0, 512)]        // no cap requested
    public void DimensionsHalveUntilTheyFitTheCap(int source, int cap, int expected)
    {
        var analysis = TextureAnalyzer.Analyze(Surface(8, 8, (_, _) => (1, 2, 3, 255)), 8, 8);
        var (w, h, _) = TextureAnalyzer.ApplyDownscale(source, source, cap, analysis);
        Assert.Equal(expected, w);
        Assert.Equal(expected, h);
    }

    [Fact]
    public void CompressedTexturesKeepTheirFormatWhenResized()
    {
        var analysis = TextureAnalyzer.Analyze(
            Surface(64, 64, (x, y) => ((byte)x, (byte)y, 0, 255)), 64, 64);

        var result = TextureAnalyzer.Recommend(
            analysis, 4096, 4096, OptimizationStrategy.Balanced,
            collapseSolidColours: true, maxDimension: 2048,
            sourceFormat: DxgiFormat.Bc7Unorm);

        Assert.Equal(DxgiFormat.Bc7Unorm, result.Format);
        Assert.Equal(2048, result.Width);
    }

    [Fact]
    public void SrgbSourcesNeverBecomeLinear()
    {
        // Writing a linear format where the engine expects sRGB shifts every colour.
        var analysis = TextureAnalyzer.Analyze(
            Surface(64, 64, (x, y) => ((byte)x, (byte)y, 0, (byte)x)), 64, 64);

        var fromSrgbCompressed = TextureAnalyzer.Recommend(
            analysis, 4096, 4096, maxDimension: 2048, sourceFormat: DxgiFormat.Bc7UnormSrgb);
        Assert.Equal(DxgiFormat.Bc7UnormSrgb, fromSrgbCompressed.Format);

        var fromSrgbUncompressed = TextureAnalyzer.Recommend(
            analysis, 512, 512, sourceFormat: DxgiFormat.R8G8B8A8UnormSrgb);
        Assert.Equal(DxgiFormat.Bc7UnormSrgb, fromSrgbUncompressed.Format);

        var fromLinear = TextureAnalyzer.Recommend(
            analysis, 512, 512, sourceFormat: DxgiFormat.R8G8B8A8Unorm);
        Assert.Equal(DxgiFormat.Bc7Unorm, fromLinear.Format);
    }

    [Fact]
    public void SolidColourStoredCompressedIsStillCollapsed()
    {
        // Real bundles contain 4096x4096 BC7 surfaces holding one colour.
        var rgba = Surface(64, 64, (_, _) => (0, 0, 0, 255));
        var bc7 = TextureEncoder.Encode(rgba, 64, 64, DxgiFormat.R8G8B8A8Unorm,
                                        64, 64, DxgiFormat.Bc7Unorm, Fast);
        var decoded = TextureDecoder.Decode(bc7, 64, 64, DxgiFormat.Bc7Unorm);
        var analysis = TextureAnalyzer.Analyze(decoded, 64, 64);

        Assert.True(analysis.IsSolidColour);
    }

    [Fact]
    public void ResizingACompressedTextureRoundTripsThroughTheDecoder()
    {
        var rgba = Surface(64, 64, (x, y) => ((byte)(x * 4), (byte)(y * 4), 64, 255));
        var bc7 = TextureEncoder.Encode(rgba, 64, 64, DxgiFormat.R8G8B8A8Unorm,
                                        64, 64, DxgiFormat.Bc7Unorm, Fast);

        var halved = TextureEncoder.Encode(bc7, 64, 64, DxgiFormat.Bc7Unorm,
                                           32, 32, DxgiFormat.Bc7Unorm, Fast);

        Assert.Equal(DxgiFormat.Bc7Unorm.SurfaceSize(32, 32), halved.Length);

        // The halved surface must still resemble the original, not be garbage.
        var back = TextureDecoder.Decode(halved, 32, 32, DxgiFormat.Bc7Unorm);
        Assert.Equal(32 * 32 * 4, back.Length);
        Assert.InRange(back[0], 0, 40);
    }

    [Fact]
    public void MaxDimensionShrinksABundleEndToEnd()
    {
        using var fixture = new SyntheticBundle()
            .AddTexture(256, 256, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)x))
            .Write();

        var bundle = Bundle.Load(fixture.BundlePath);
        OptimizationResult result;
        using (var gpu = GpuResourceFile.Open(bundle.GpuResourcePath))
        {
            var plan = OptimizationPlan.Build(bundle, gpu, maxDimension: 128);
            var item = Assert.Single(plan.Textures);
            Assert.Equal(128, item.TargetWidth);
            result = BundleOptimizer.Apply(plan, gpu, bundle.Path, bundle.GpuResourcePath, Fast);
        }

        var rebuilt = Bundle.Load(fixture.BundlePath);
        var entry = rebuilt.Textures.Single();
        Assert.True(TextureResource.TryCreate(rebuilt, entry, out var texture));
        Assert.Equal(128, texture!.Width);
        Assert.Equal(texture.SourceFormat.SurfaceSize(128, 128), entry.GpuSize);

        var report = BundleVerifier.Verify(fixture.BundlePath);
        Assert.True(report.Passed,
            "verification failed: " + string.Join("; ", report.Issues.Select(i => i.Detail)));
        Assert.True(result.NewGpuSize < result.OriginalGpuSize);
    }
}
