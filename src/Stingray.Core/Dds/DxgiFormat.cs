// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Stingray.Core.Dds;

/// <summary>Subset of DXGI_FORMAT relevant to bundle textures.</summary>
public enum DxgiFormat : uint
{
    Unknown = 0,
    R8G8Unorm = 49,
    R8Unorm = 61,
    R8G8B8A8Unorm = 28,
    R8G8B8A8UnormSrgb = 29,
    Bc1Unorm = 71,
    Bc1UnormSrgb = 72,
    Bc2Unorm = 74,
    Bc2UnormSrgb = 75,
    Bc3Unorm = 77,
    Bc3UnormSrgb = 78,
    Bc4Unorm = 80,
    Bc4Snorm = 81,
    Bc5Unorm = 83,
    Bc5Snorm = 84,
    B8G8R8A8Unorm = 87,
    B8G8R8A8UnormSrgb = 91,
    Bc6HUf16 = 95,
    Bc6HSf16 = 96,
    Bc7Unorm = 98,
    Bc7UnormSrgb = 99,
}

public static class DxgiFormatInfo
{
    /// <summary>True for BCn block-compressed formats.</summary>
    public static bool IsBlockCompressed(this DxgiFormat format) => format switch
    {
        DxgiFormat.Bc1Unorm or DxgiFormat.Bc1UnormSrgb
            or DxgiFormat.Bc2Unorm or DxgiFormat.Bc2UnormSrgb
            or DxgiFormat.Bc3Unorm or DxgiFormat.Bc3UnormSrgb
            or DxgiFormat.Bc4Unorm or DxgiFormat.Bc4Snorm
            or DxgiFormat.Bc5Unorm or DxgiFormat.Bc5Snorm
            or DxgiFormat.Bc6HUf16 or DxgiFormat.Bc6HSf16
            or DxgiFormat.Bc7Unorm or DxgiFormat.Bc7UnormSrgb => true,
        _ => false,
    };

    /// <summary>Bytes per 4x4 block for compressed formats; 0 otherwise.</summary>
    public static int BytesPerBlock(this DxgiFormat format) => format switch
    {
        DxgiFormat.Bc1Unorm or DxgiFormat.Bc1UnormSrgb
            or DxgiFormat.Bc4Unorm or DxgiFormat.Bc4Snorm => 8,
        DxgiFormat.Bc2Unorm or DxgiFormat.Bc2UnormSrgb
            or DxgiFormat.Bc3Unorm or DxgiFormat.Bc3UnormSrgb
            or DxgiFormat.Bc5Unorm or DxgiFormat.Bc5Snorm
            or DxgiFormat.Bc6HUf16 or DxgiFormat.Bc6HSf16
            or DxgiFormat.Bc7Unorm or DxgiFormat.Bc7UnormSrgb => 16,
        _ => 0,
    };

    /// <summary>Bytes per pixel for uncompressed formats; 0 otherwise.</summary>
    public static int BytesPerPixel(this DxgiFormat format) => format switch
    {
        DxgiFormat.R8G8B8A8Unorm or DxgiFormat.R8G8B8A8UnormSrgb
            or DxgiFormat.B8G8R8A8Unorm or DxgiFormat.B8G8R8A8UnormSrgb => 4,
        DxgiFormat.R8G8Unorm => 2,
        DxgiFormat.R8Unorm => 1,
        _ => 0,
    };

    /// <summary>Whether a surface size can be computed for this format at all.</summary>
    public static bool IsSizable(this DxgiFormat format) =>
        format.IsBlockCompressed() || format.BytesPerPixel() > 0;

    /// <summary>True for the 32-bit straight-channel layouts this tool can analyse.</summary>
    public static bool IsUncompressedRgba(this DxgiFormat format) => format.BytesPerPixel() == 4;

    /// <summary>True when the blue channel precedes red in memory.</summary>
    public static bool IsBgra(this DxgiFormat format) =>
        format is DxgiFormat.B8G8R8A8Unorm or DxgiFormat.B8G8R8A8UnormSrgb;

    /// <summary>Size in bytes of a single mip level at the given dimensions.</summary>
    public static long SurfaceSize(this DxgiFormat format, int width, int height)
    {
        if (format.IsBlockCompressed())
        {
            var bw = Math.Max(1, (width + 3) / 4);
            var bh = Math.Max(1, (height + 3) / 4);
            return (long)bw * bh * format.BytesPerBlock();
        }

        var bpp = format.BytesPerPixel();
        if (bpp == 0) throw new NotSupportedException($"Unsupported DXGI format {format}.");
        return (long)width * height * bpp;
    }

    /// <summary>Byte size of every level of a mip chain, largest first.</summary>
    public static long[] MipChain(this DxgiFormat format, int width, int height, int mipCount)
    {
        var levels = new long[Math.Max(1, mipCount)];
        for (var i = 0; i < levels.Length; i++)
            levels[i] = format.SurfaceSize(Math.Max(1, width >> i), Math.Max(1, height >> i));
        return levels;
    }

    /// <summary>
    /// How many top levels to drop so the largest remaining one fits
    /// <paramref name="maxDimension"/>, never dropping the whole chain.
    /// </summary>
    public static int LevelsToDrop(int width, int height, int mipCount, int maxDimension)
    {
        if (maxDimension <= 0 || mipCount <= 1) return 0;

        var drop = 0;
        while (drop < mipCount - 1
               && Math.Max(width >> drop, height >> drop) > maxDimension)
            drop++;

        return drop;
    }

    public static string DisplayName(this DxgiFormat format) => format switch
    {
        DxgiFormat.R8G8B8A8Unorm => "R8G8B8A8_UNORM",
        DxgiFormat.R8G8B8A8UnormSrgb => "R8G8B8A8_UNORM_SRGB",
        DxgiFormat.B8G8R8A8Unorm => "B8G8R8A8_UNORM",
        DxgiFormat.B8G8R8A8UnormSrgb => "B8G8R8A8_UNORM_SRGB",
        DxgiFormat.Bc1Unorm => "BC1_UNORM",
        DxgiFormat.Bc1UnormSrgb => "BC1_UNORM_SRGB",
        DxgiFormat.Bc3Unorm => "BC3_UNORM",
        DxgiFormat.Bc4Unorm => "BC4_UNORM",
        DxgiFormat.Bc5Unorm => "BC5_UNORM",
        DxgiFormat.Bc7Unorm => "BC7_UNORM",
        DxgiFormat.Bc7UnormSrgb => "BC7_UNORM_SRGB",
        DxgiFormat.R8Unorm => "R8_UNORM",
        DxgiFormat.R8G8Unorm => "R8G8_UNORM",
        _ => $"DXGI_{(uint)format}",
    };
}
