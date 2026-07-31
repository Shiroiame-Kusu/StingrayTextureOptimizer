// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

/// <summary>
/// Encodes through AMD Compressonator's CMP_Core codecs, via the small native
/// shim in <c>native/</c>.
/// </summary>
/// <remarks>
/// Several times faster than the managed encoder on BC7, which is what makes
/// whole-bundle passes practical. The shim is ~500 KB and contains only the
/// block codecs and their SIMD dispatch — the Compressonator *CLI* would have
/// meant ~137 MB of Qt, OpenCV and DirectX/Vulkan plugins for the same result,
/// plus a process launch and a temporary DDS per texture.
///
/// It is a native library, so it is built for Linux and Windows only. Anywhere
/// else — or if the file is simply absent — this reports unavailable and the
/// managed backend takes over.
/// </remarks>
public sealed partial class CompressonatorBackend : IBlockEncoderBackend
{
    private const string Library = "stingray_cmp";

    /// <summary>ABI the managed side expects; must match the shim.</summary>
    private const int ExpectedAbi = 1;

    /// <summary>Points at a shim to load instead of the bundled one.</summary>
    public const string PathVariable = "STINGRAY_CMP_LIBRARY";

    private static readonly Lazy<string?> Probe = new(ProbeLibrary);

    static CompressonatorBackend() =>
        NativeLibrary.SetDllImportResolver(typeof(CompressonatorBackend).Assembly, Resolve);

    public string Name => "Compressonator CMP_Core";

    public bool IsAvailable => Probe.Value is null;

    /// <summary>Why the backend is unusable, or "available".</summary>
    public static string UnavailableReason => Probe.Value ?? "available";

    [LibraryImport(Library)]
    private static partial int stingray_cmp_abi_version();

    [LibraryImport(Library)]
    private static unsafe partial int stingray_cmp_encode(
        int format, byte* rgba, int width, int height,
        byte* output, ulong outputSize,
        float quality, int threads, int needsAlpha, int alphaThreshold);

    /// <summary>
    /// Looks beside the app and in <c>tools/</c> before falling back to the
    /// default search, so a bundled shim is found without touching the
    /// environment.
    /// </summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return IntPtr.Zero;

        var overridden = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden)
            && NativeLibrary.TryLoad(overridden, out var custom))
            return custom;

        var file = OperatingSystem.IsWindows() ? "stingray_cmp.dll" : "stingray_cmp.so";
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "tools") })
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    /// <summary>Returns null when usable, otherwise the reason it is not.</summary>
    private static string? ProbeLibrary()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return "native shim is built for Linux and Windows only";

        try
        {
            var abi = stingray_cmp_abi_version();
            return abi == ExpectedAbi
                ? null
                : $"native shim reports ABI {abi}, expected {ExpectedAbi}";
        }
        catch (DllNotFoundException)
        {
            return "native shim not found";
        }
        catch (EntryPointNotFoundException)
        {
            return "native shim is missing an expected entry point";
        }
        catch (BadImageFormatException)
        {
            return "native shim was built for a different architecture";
        }
    }

    public unsafe byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height,
                                DxgiFormat target, EncodeOptions options)
    {
        if (!IsAvailable)
            throw new InvalidOperationException($"Compressonator is unusable: {UnavailableReason}.");

        var code = ToFormatCode(target);
        var expected = target.SurfaceSize(width, height);
        var output = new byte[expected];

        // BC1 needs an explicit threshold to keep its single alpha bit, and BC7
        // encodes alpha only when told the image has any.
        var needsAlpha = HasTransparency(rgba);

        int result;
        fixed (byte* src = rgba)
        fixed (byte* dst = output)
        {
            result = stingray_cmp_encode(
                code, src, width, height, dst, (ulong)output.LongLength,
                ToQuality(options.Quality), Math.Max(1, options.ThreadCount),
                needsAlpha ? 1 : 0, 128);
        }

        if (result != 0)
            throw new InvalidOperationException(
                $"CMP_Core failed to encode {width}x{height} {target.DisplayName()} (code {result}).");

        return output;
    }

    private static bool HasTransparency(ReadOnlySpan<byte> rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return true;
        return false;
    }

    /// <summary>Format codes understood by the shim.</summary>
    private static int ToFormatCode(DxgiFormat format) => format switch
    {
        DxgiFormat.Bc1Unorm or DxgiFormat.Bc1UnormSrgb => 1,
        DxgiFormat.Bc3Unorm or DxgiFormat.Bc3UnormSrgb => 3,
        DxgiFormat.Bc4Unorm => 4,
        DxgiFormat.Bc5Unorm => 5,
        DxgiFormat.Bc7Unorm or DxgiFormat.Bc7UnormSrgb => 7,
        // BC2 is not worth a code path: BC3 is the same size and strictly better.
        _ => throw new NotSupportedException(
            $"{format.DisplayName()} is not supported by the native encoder."),
    };

    // Measured on real textures: quality saturates around 0.9, so "best" gains
    // very little past it while "fast" is what makes iteration quick.
    private static float ToQuality(EncodeQuality quality) => quality switch
    {
        EncodeQuality.Fast => 0.05f,
        EncodeQuality.Best => 1.00f,
        _ => 0.80f,
    };
}
