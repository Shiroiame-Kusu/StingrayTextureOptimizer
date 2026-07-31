// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Stingray.Core.Format;

/// <summary>
/// Stingray hashes the asset type name (murmur64) to produce the type id stored
/// in the bundle.
/// </summary>
/// <remarks>
/// This table deliberately contains only ids confirmed against real bundle data.
/// Plenty of other type hashes circulate in the modding community, but an
/// incorrect entry here would silently mislabel assets, so unverified values are
/// left out and rendered as hex instead. Contributions adding a hash should say
/// how it was confirmed.
/// </remarks>
public static class StingrayTypeIds
{
    public const ulong Material = 0xEAC0_B497_876A_DEDF;
    public const ulong Texture = 0xCD42_38C6_A0C6_9E32;
    public const ulong Unit = 0xE0A4_8D0B_E9A7_453F;
    public const ulong Bones = 0x18DE_AD01_056B_72E9;

    private static readonly Dictionary<ulong, string> Names = new()
    {
        [Material] = "material",
        [Texture] = "texture",
        [Unit] = "unit",
        [Bones] = "bones",
    };

    /// <summary>Known name for a type id, or its hex form when unrecognised.</summary>
    public static string NameOf(ulong typeId) =>
        Names.TryGetValue(typeId, out var name) ? name : $"0x{typeId:X16}";

    public static bool IsKnown(ulong typeId) => Names.ContainsKey(typeId);
}
