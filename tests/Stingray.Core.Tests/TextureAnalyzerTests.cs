// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

public class TextureAnalyzerTests
{
    private static byte[] Surface(int width, int height, Func<int, int, (byte, byte, byte, byte)> fill)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b, a) = fill(x, y);
            var i = (y * width + x) * 4;
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }
        return data;
    }

    [Fact]
    public void DetectsSolidColour()
    {
        var surface = Surface(32, 32, (_, _) => (0, 0, 0, 255));
        var analysis = TextureAnalyzer.Analyze(surface, 32, 32);

        Assert.True(analysis.IsSolidColour);
        Assert.False(analysis.HasAlphaDetail);
        Assert.Equal([0, 0, 0, 255], analysis.SolidColour);
        Assert.Contains("solid RGBA(0,0,0,255)", analysis.Summary);
    }

    [Fact]
    public void DetectsFlatColourWithAlphaDetail()
    {
        // The real-world case: an unused normal map whose alpha still carries a mask.
        var surface = Surface(64, 64, (x, _) => (127, 127, 255, (byte)(x % 2 == 0 ? 115 : 179)));
        var analysis = TextureAnalyzer.Analyze(surface, 64, 64);

        Assert.False(analysis.IsSolidColour);
        Assert.True(analysis.IsFlatColourWithAlphaDetail);
        Assert.True(analysis.HasAlphaDetail);
        Assert.Equal(2, analysis.Alpha.DistinctValues);
        Assert.True(analysis.Red.IsConstant);
    }

    [Fact]
    public void ReportsChannelsInRgbaOrderForBgraSources()
    {
        // Stored B,G,R,A — red must still be reported as 200 after normalisation.
        var stored = Surface(8, 8, (_, _) => (10, 50, 200, 255));
        var surface = TextureDecoder.ToRgba(stored, 8, 8, DxgiFormat.B8G8R8A8Unorm);
        var analysis = TextureAnalyzer.Analyze(surface, 8, 8);

        Assert.Equal(200, analysis.Red.Minimum);
        Assert.Equal(50, analysis.Green.Minimum);
        Assert.Equal(10, analysis.Blue.Minimum);
    }

    [Fact]
    public void SolidColourCollapsesToSmallSurface()
    {
        var analysis = TextureAnalyzer.Analyze(Surface(2048, 8, (_, _) => (0, 0, 0, 255)), 2048, 8);
        var result = TextureAnalyzer.Recommend(analysis, 2048, 2048);

        Assert.Equal(TextureAnalyzer.SolidColourExtent, result.Width);
        Assert.True(result.IsLossless);
        Assert.Equal(DxgiFormat.Bc1Unorm, result.Format);
    }

    [Fact]
    public void SolidColourNotRepresentableInBc1UsesBc7()
    {
        // 127 does not survive a 5-bit round trip, so BC1 would shift it.
        var analysis = TextureAnalyzer.Analyze(Surface(16, 16, (_, _) => (127, 127, 255, 255)), 16, 16);
        var result = TextureAnalyzer.Recommend(analysis, 512, 512);

        Assert.Equal(DxgiFormat.Bc7Unorm, result.Format);
        Assert.True(result.IsLossless);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]        // black is exact in 5:6:5
    [InlineData(255, 255, 255, true)]  // so is white
    [InlineData(127, 127, 255, false)] // 127 is not
    [InlineData(8, 4, 8, true)]        // aligned to the 5:6:5 grid
    public void Bc1ExactnessMatchesFiveSixFiveGrid(byte r, byte g, byte b, bool expected) =>
        Assert.Equal(expected, TextureAnalyzer.IsExactInBc1(r, g, b, 255));

    [Fact]
    public void TransparentPixelsAreNeverConsideredOpaque() =>
        Assert.False(TextureAnalyzer.IsExactInBc1(0, 0, 0, 128));

    [Fact]
    public void OpaqueContentPrefersBc1UnlessMaximumQuality()
    {
        var analysis = TextureAnalyzer.Analyze(
            Surface(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), 255)), 64, 64);

        Assert.Equal(DxgiFormat.Bc1Unorm,
            TextureAnalyzer.Recommend(analysis, 64, 64, OptimizationStrategy.Balanced).Format);
        Assert.Equal(DxgiFormat.Bc7Unorm,
            TextureAnalyzer.Recommend(analysis, 64, 64, OptimizationStrategy.MaximumQuality).Format);
    }

    [Fact]
    public void EveryStrategyProducesADistinctResult()
    {
        // A pure cutout mask: BC1 carries one alpha bit, so smallest-size can use
        // it where the other strategies must fall back to BC7. Without this the
        // Balanced and SmallestSize options were byte-for-byte identical.
        var cutout = Surface(64, 64,
            (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)(x < 32 ? 0 : 255)));
        var analysis = TextureAnalyzer.Analyze(cutout, 64, 64);

        Assert.True(analysis.HasBinaryAlpha);
        Assert.Equal(DxgiFormat.Bc7Unorm,
            TextureAnalyzer.Recommend(analysis, 64, 64, OptimizationStrategy.Balanced).Format);
        Assert.Equal(DxgiFormat.Bc7Unorm,
            TextureAnalyzer.Recommend(analysis, 64, 64, OptimizationStrategy.MaximumQuality).Format);
        Assert.Equal(DxgiFormat.Bc1Unorm,
            TextureAnalyzer.Recommend(analysis, 64, 64, OptimizationStrategy.SmallestSize).Format);
    }

    [Fact]
    public void GradedAlphaIsNeverForcedIntoBc1()
    {
        // Sixty-four alpha levels cannot survive a single alpha bit.
        var graded = Surface(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)(x * 4)));
        var analysis = TextureAnalyzer.Analyze(graded, 64, 64);

        Assert.False(analysis.HasBinaryAlpha);
        Assert.Equal(DxgiFormat.Bc7Unorm,
            TextureAnalyzer.Recommend(analysis, 64, 64, OptimizationStrategy.SmallestSize).Format);
    }

    [Fact]
    public void CutoutAlphaSurvivesBc1Encoding()
    {
        // BC1's alpha bit must actually be emitted, not silently dropped.
        var cutout = Surface(16, 16, (x, _) => (200, 40, 40, (byte)(x < 8 ? 0 : 255)));
        var bc1 = TextureEncoder.Encode(cutout, 16, 16, DxgiFormat.R8G8B8A8Unorm,
                                        16, 16, DxgiFormat.Bc1Unorm,
                                        new EncodeOptions { Quality = EncodeQuality.Best, ThreadCount = 2 });
        var back = TextureDecoder.Decode(bc1, 16, 16, DxgiFormat.Bc1Unorm);

        Assert.Equal(0, back[3]);            // first pixel transparent
        Assert.Equal(255, back[(8 * 4) + 3]); // ninth pixel opaque
    }

    [Fact]
    public void ContentWithAlphaAlwaysUsesAnAlphaCapableFormat()
    {
        var analysis = TextureAnalyzer.Analyze(
            Surface(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), (byte)(x * 4))), 64, 64);

        foreach (var strategy in Enum.GetValues<OptimizationStrategy>())
            Assert.Equal(DxgiFormat.Bc7Unorm,
                TextureAnalyzer.Recommend(analysis, 64, 64, strategy).Format);
    }
}
