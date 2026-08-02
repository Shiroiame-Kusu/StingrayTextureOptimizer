// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace Stingray.Core.Format;

/// <summary>One bundle found on disk, with what can be known without opening it.</summary>
public sealed record DiscoveredBundle
{
    /// <summary>Path of the bundle file itself, ready to hand to <see cref="Bundle.Load"/>.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The folder the bundle sits in, which is what mod managers name after the
    /// mod. Falls back to the file name when the bundle is loose in the folder
    /// that was scanned.
    /// </summary>
    public required string ModName { get; init; }

    /// <summary>File name of the bundle, which distinguishes several in one mod.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Size of the <c>.gpu_resources</c> companion — what there is to save.</summary>
    public required long GpuSize { get; init; }

    /// <summary>Size of the <c>.stream</c> companion, zero when there is none.</summary>
    public required long StreamSize { get; init; }
}

/// <summary>
/// Finds Stingray bundles under a directory, the way a mod manager's folder is
/// laid out: one folder per mod, each holding a bundle and its companions.
/// </summary>
public static class BundleDiscovery
{
    /// <summary>
    /// Directories this tool creates for the originals it replaces. Scanning
    /// into one would offer the same mod twice — once as it is and once as it
    /// was — and optimising the backup would destroy the only way back.
    /// </summary>
    public const string BackupDirectoryName = "backup";

    /// <summary>
    /// Walks <paramref name="root"/> and returns every bundle under it, largest
    /// first, since size is what decides whether a mod is worth touching.
    /// </summary>
    /// <remarks>
    /// A file counts as a bundle when it has a <c>.gpu_resources</c> sibling and
    /// begins with the bundle magic. The magic check is what keeps a stray file
    /// out; the companion check is what makes it cheap, since only a handful of
    /// candidates are ever opened.
    /// </remarks>
    public static IReadOnlyList<DiscoveredBundle> Scan(
        string root,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such directory: {root}");

        var found = new List<DiscoveredBundle>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            progress?.Report(directory);

            // Enumerated separately so one unreadable directory does not abandon
            // the whole walk, which AllDirectories would.
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (System.IO.Path.GetFileName(child)
                              .Equals(BackupDirectoryName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Push(child);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Describe(file, root) is { } bundle) found.Add(bundle);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Nothing readable here; the directories above still are.
            }
        }

        return [.. found.OrderByDescending(b => b.GpuSize)
                        .ThenBy(b => b.ModName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(b => b.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>True when a path looks like a bundle rather than one of its companions.</summary>
    internal static bool LooksLikeABundle(string path) =>
        !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path + ".gpu_resources");

    private static DiscoveredBundle? Describe(string path, string root)
    {
        if (!LooksLikeABundle(path)) return null;

        try
        {
            if (!HasBundleMagic(path)) return null;

            var gpu = new FileInfo(path + ".gpu_resources");
            var stream = new FileInfo(path + ".stream");
            var directory = System.IO.Path.GetDirectoryName(path);

            // A bundle sitting directly in the scanned folder has no folder of
            // its own to be named after, so it is named after itself.
            var modName = directory is null
                       || System.IO.Path.GetFullPath(directory) == System.IO.Path.GetFullPath(root)
                ? System.IO.Path.GetFileName(path)
                : System.IO.Path.GetFileName(directory);

            return new DiscoveredBundle
            {
                Path = path,
                ModName = modName,
                GpuSize = gpu.Exists ? gpu.Length : 0,
                StreamSize = stream.Exists ? stream.Length : 0,
            };
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool HasBundleMagic(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
            && BinaryPrimitives.ReadUInt32LittleEndian(head) == BundleFormat.Magic;
    }
}
