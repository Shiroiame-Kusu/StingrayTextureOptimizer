// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace Stingray.Core;

/// <summary>Version of the running build, for display.</summary>
public static class BuildInfo
{
    /// <summary>
    /// Set from <c>&lt;Version&gt;</c> at build time. CI overrides it with the
    /// git tag, or <c>0.0.0-&lt;sha&gt;</c> for untagged builds, so a binary can
    /// always be traced back to a commit.
    /// </summary>
    public static string Version { get; } = Read();

    /// <summary>e.g. "Stingray Texture Optimizer 1.2.0".</summary>
    public static string ProductAndVersion => $"Stingray Texture Optimizer {Version}";

    private static string Read()
    {
        var assembly = typeof(BuildInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink appends "+<sha>", which is noise on a title bar.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
