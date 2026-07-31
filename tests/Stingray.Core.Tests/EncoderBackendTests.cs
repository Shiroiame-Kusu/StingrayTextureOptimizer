// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Textures;
using Xunit;

namespace Stingray.Core.Tests;

/// <summary>
/// The native backend is optional, so these assert behaviour that must hold
/// whether or not the shim is present on the machine running the tests.
/// </summary>
public class EncoderBackendTests
{
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
    public void ManagedBackendIsAlwaysAvailable() =>
        Assert.True(new ManagedEncoderBackend().IsAvailable);

    [Fact]
    public void AutoAlwaysResolvesToSomething()
    {
        var backend = TextureEncoder.Resolve(EncoderBackend.Auto);
        Assert.NotNull(backend);
        Assert.True(backend.IsAvailable);
    }

    [Fact]
    public void AutoNeverThrowsEvenWithoutTheNativeShim()
    {
        // Whatever Auto picks, encoding must succeed: that is the contract that
        // lets the tool ship the native library as an optional extra.
        var rgba = Surface(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), 255));
        var encoded = TextureEncoder.Encode(rgba, 64, 64, DxgiFormat.R8G8B8A8Unorm,
                                            64, 64, DxgiFormat.Bc7Unorm,
                                            new EncodeOptions { Backend = EncoderBackend.Auto, ThreadCount = 2 });
        Assert.Equal(DxgiFormat.Bc7Unorm.SurfaceSize(64, 64), encoded.Length);
    }

    [Fact]
    public void RequestingAnUnavailableBackendFailsLoudly()
    {
        // Auto silently falls back; an explicit request must not, or a user who
        // asked for the fast path would never learn they did not get it.
        var compressonator = new CompressonatorBackend();
        if (compressonator.IsAvailable)
        {
            Assert.NotNull(TextureEncoder.Resolve(EncoderBackend.Compressonator));
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => TextureEncoder.Resolve(EncoderBackend.Compressonator));
        Assert.Contains(CompressonatorBackend.UnavailableReason, ex.Message);
    }

    [Theory]
    [InlineData(DxgiFormat.Bc1Unorm)]
    [InlineData(DxgiFormat.Bc3Unorm)]
    [InlineData(DxgiFormat.Bc7Unorm)]
    public void EveryAvailableBackendAgreesOnOutputSize(DxgiFormat target)
    {
        var rgba = Surface(32, 32, (x, y) => ((byte)(x * 8), (byte)(y * 8), 0, 255));
        var expected = target.SurfaceSize(32, 32);

        foreach (var backend in new IBlockEncoderBackend[]
                 { new ManagedEncoderBackend(), new CompressonatorBackend() })
        {
            if (!backend.IsAvailable) continue;
            var encoded = backend.Encode(rgba, 32, 32, target, new EncodeOptions { ThreadCount = 2 });
            Assert.Equal(expected, encoded.LongLength);
        }
    }

    [Fact]
    public void BackendStatusExplainsItself() =>
        Assert.False(string.IsNullOrWhiteSpace(TextureEncoder.BackendStatus));
}
