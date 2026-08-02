// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace Stingray.Gui.Localization;

public enum AppLanguage
{
    English,
    ChineseSimplified,
}

/// <summary>
/// Which language the window is drawn in, and where that decision comes from.
/// </summary>
/// <remarks>
/// Not <see cref="System.Globalization.CultureInfo"/>, because it cannot work
/// here: the whole project builds with <c>InvariantGlobalization</c>, under which
/// <c>CurrentUICulture.Name</c> is the empty string and <c>new CultureInfo("zh-CN")</c>
/// throws <c>CultureNotFoundException</c>. Turning that off would mean carrying
/// ICU, which is a large payload for a tool whose Linux binaries currently link
/// nothing beyond libc and libm.
///
/// So the language is read from where the operating system actually publishes
/// it: the POSIX locale variables on Unix, and the user's UI language on Windows,
/// which has no <c>LANG</c> to read.
/// </remarks>
public static class Language
{
    /// <summary>
    /// Forces a language regardless of the system, for anyone whose desktop
    /// language is not the one they want to read. Also how the screenshots for
    /// both languages are produced.
    /// </summary>
    public const string OverrideVariable = "STINGRAY_LANG";

    /// <summary>The language every string is resolved against.</summary>
    public static AppLanguage Current { get; set; } = AppLanguage.English;

    /// <summary>Reads the system's language and adopts it.</summary>
    public static void Detect() => Current = FromSystem();

    internal static AppLanguage FromSystem()
    {
        var forced = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(forced)) return FromTag(forced);

        // POSIX order of precedence. LANGUAGE is a GNU extension and holds a
        // colon-separated preference list, so only its first entry is read.
        foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG", "LANGUAGE" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            var first = value.Split(':')[0];
            if (first is "C" or "POSIX") return AppLanguage.English;
            return FromTag(first);
        }

        return OperatingSystem.IsWindows() ? FromWindows() : AppLanguage.English;
    }

    /// <summary>
    /// Maps a locale tag to a language. Accepts everything these variables are
    /// found holding: <c>zh_CN.UTF-8</c>, <c>zh-Hans</c>, <c>zh_TW@radical</c>.
    /// </summary>
    /// <remarks>
    /// Every Chinese tag resolves to Simplified, including the Traditional ones,
    /// because Simplified is the only Chinese translation there is and it reads
    /// closer than English does. Better a Traditional reader sees Simplified than
    /// a language they may not read at all.
    /// </remarks>
    internal static AppLanguage FromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return AppLanguage.English;

        // Trim the encoding and modifier suffixes a POSIX locale can carry.
        var cleaned = tag.Split('.', '@')[0].Replace('_', '-').Trim();
        var primary = cleaned.Split('-')[0];

        return primary.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.ChineseSimplified
            : AppLanguage.English;
    }

    /// <summary>
    /// Windows publishes no <c>LANG</c>, and with InvariantGlobalization the
    /// managed culture APIs report nothing, so the UI language is asked for
    /// directly. The low 10 bits of a LANGID are the primary language, and
    /// <c>LANG_CHINESE</c> is 0x04.
    /// </summary>
    private static AppLanguage FromWindows()
    {
        try
        {
            return (GetUserDefaultUILanguage() & 0x3FF) == 0x04
                ? AppLanguage.ChineseSimplified
                : AppLanguage.English;
        }
        catch (DllNotFoundException)
        {
            return AppLanguage.English;
        }
        catch (EntryPointNotFoundException)
        {
            return AppLanguage.English;
        }
    }

    // DllImport rather than LibraryImport: the signature is blittable with no
    // marshalling to generate, and LibraryImport would mean turning unsafe code
    // on across the whole project for this one call.
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
