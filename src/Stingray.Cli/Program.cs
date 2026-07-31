// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Dds;
using Stingray.Core.Format;
using Stingray.Core.Optimization;
using Stingray.Core.Textures;

namespace Stingray.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var options = CommandLine.Parse(args.Skip(1));
            return args[0] switch
            {
                "analyze" or "analyse" => Analyze(options),
                "optimize" or "optimise" => Optimize(options),
                "verify" => Verify(options),
                var unknown => Fail($"Unknown command '{unknown}'. Try --help."),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                      or NotSupportedException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Analyze(CommandLine options)
    {
        var bundle = Bundle.Load(options.RequirePath());
        using var gpu = OpenGpu(bundle);

        Console.WriteLine($"{Path.GetFileName(bundle.Path)}: {bundle.Files.Count} entries, "
                        + $"{bundle.Types.Count} types, gpu_resources {Human(gpu.Length)}");
        foreach (var type in bundle.Types)
            Console.WriteLine($"  {type.Count,4} x {type.TypeName}");

        var plan = OptimizationPlan.Build(bundle, gpu, options.Strategy, !options.NoCollapse,
                                          maxDimension: options.MaxDimension);
        plan.Deduplicate = !options.NoDedup;
        PrintPlan(plan);
        return 0;
    }

    private static int Optimize(CommandLine options)
    {
        var bundle = Bundle.Load(options.RequirePath());
        using var gpu = OpenGpu(bundle);

        var plan = OptimizationPlan.Build(bundle, gpu, options.Strategy, !options.NoCollapse,
                                          maxDimension: options.MaxDimension);
        plan.Deduplicate = !options.NoDedup;
        PrintPlan(plan);

        // Deduplication alone can be a large saving even when every texture is
        // already compressed, so an empty texture list is not "nothing to do".
        if (plan.Textures.Count == 0 && (options.NoDedup || plan.DuplicateEntryCount == 0))
        {
            Console.WriteLine("\nNothing to do.");
            return 0;
        }

        if (options.DryRun)
        {
            Console.WriteLine("\nDry run: no files written.");
            return 0;
        }

        var outputBundle = options.Output is null
            ? bundle.Path
            : Path.Combine(options.Output, Path.GetFileName(bundle.Path));
        var outputGpu = outputBundle + ".gpu_resources";

        if (options.Output is not null) Directory.CreateDirectory(options.Output);

        string? backupDirectory = null;
        if (options.Output is null)
        {
            backupDirectory = options.Backup
                ?? Path.Combine(Path.GetDirectoryName(bundle.Path) ?? ".", "backup");
            var created = BundleOptimizer.CreateBackup(bundle, backupDirectory);
            Console.WriteLine($"\nBacked up {created.Count} file(s) to {backupDirectory}");
        }

        var encodeOptions = new EncodeOptions
        {
            Quality = options.Quality,
            ThreadCount = options.Threads ?? Math.Max(1, Environment.ProcessorCount - 4),
        };
        Console.WriteLine($"Encoding with {encodeOptions.ThreadCount} threads at {encodeOptions.Quality} quality...");

        var lastStage = "";
        var progress = new Progress<ApplyProgress>(p =>
        {
            if (p.Stage == lastStage) return;
            lastStage = p.Stage;
            Console.WriteLine($"  {p.Stage}...");
        });

        var result = BundleOptimizer.Apply(plan, gpu, outputBundle, outputGpu, encodeOptions,
                                           progress, plan.Deduplicate);

        Console.WriteLine($"\ngpu_resources {Human(result.OriginalGpuSize)} -> {Human(result.NewGpuSize)} "
                        + $"({result.Ratio:P1}, saved {Human(result.Saved)}) in {result.Elapsed.TotalSeconds:F1}s");
        if (result.PayloadsDeduplicated > 0)
            Console.WriteLine($"  {result.PayloadsDeduplicated} duplicate payload(s) shared instead of "
                            + $"rewritten, saving {Human(result.DeduplicatedBytes)}");

        var report = BundleVerifier.Verify(outputBundle,
            backupDirectory is null ? null : Path.Combine(backupDirectory, Path.GetFileName(bundle.Path)));
        PrintVerification(report);
        return report.Passed ? 0 : 2;
    }

    private static int Verify(CommandLine options)
    {
        var report = BundleVerifier.Verify(options.RequirePath(), options.Original);
        PrintVerification(report);
        return report.Passed ? 0 : 2;
    }

    private static void PrintPlan(OptimizationPlan plan)
    {
        if (plan.Textures.Count > 0)
        {
            Console.WriteLine($"\n{"texture",-16} {"dimensions",-18} {"format",-10} {"size",10} -> {"new",10}  content");
            foreach (var item in plan.Textures)
            {
                var dims = item.TargetWidth == item.Texture.Width && item.TargetHeight == item.Texture.Height
                    ? $"{item.Texture.Width}x{item.Texture.Height}"
                    : $"{item.Texture.Width}x{item.Texture.Height}->{item.TargetWidth}x{item.TargetHeight}";
                Console.WriteLine($"{item.Texture.Name,-16} {dims,-18} {item.TargetFormat.DisplayName(),-10} "
                                + $"{Human(item.CurrentSize),10} -> {Human(item.PredictedSize),10}  {Detail(item)}");
            }
        }

        foreach (var skip in plan.Skipped)
            Console.WriteLine($"{skip.Name,-16} skipped: {skip.Reason}");

        if (plan.DuplicateEntryCount > 0)
            Console.WriteLine($"\nduplicates: {plan.DuplicateEntryCount} entr(ies) repeat a payload another "
                            + $"entry already has, wasting {Human(plan.RedundantBytes)}. "
                            + "These are stored once and shared.");

        Console.WriteLine($"\ntotal {Human(plan.CurrentGpuSize)} -> {Human(plan.PredictedGpuSize)} "
                        + $"(saves {Human(plan.PredictedSaving)})");
    }

    /// <summary>Content summary, plus the resize cost when one is planned.</summary>
    private static string Detail(TexturePlanItem item)
    {
        // A solid colour is already described by the summary; the resize verdict
        // only adds information when real detail is at stake.
        var resized = item.TargetWidth != item.Texture.Width && !item.Analysis.IsSolidColour;
        return resized
            ? $"{item.Analysis.Summary}; {item.Analysis.DetailVerdict}"
            : item.Analysis.Summary;
    }

    private static void PrintVerification(VerificationReport report)
    {
        Console.WriteLine($"\nverify: {report.SegmentsChecked} distinct payloads, "
                        + $"{report.AliasedEntries} shared reference(s), "
                        + $"{report.PayloadsCompared} passthrough payloads compared, "
                        + $"{report.PaddingBytes} padding bytes");
        foreach (var issue in report.Issues)
            Console.WriteLine($"  [{issue.Category}] {issue.Detail}");
        Console.WriteLine(report.Passed ? "  all checks passed" : $"  {report.Issues.Count} issue(s)");
    }

    private static GpuResourceFile OpenGpu(Bundle bundle)
    {
        if (!bundle.HasGpuResources)
            throw new FileNotFoundException(
                $"No companion file at {bundle.GpuResourcePath}; this bundle has no GPU payloads to optimise.");
        return GpuResourceFile.Open(bundle.GpuResourcePath);
    }

    private static string Human(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        stingray-tex - shrink Stingray/Bitsquid bundles by block-compressing their textures

        Copyright (C) 2026 Shiroiame-Kusu. This program comes
        with ABSOLUTELY NO WARRANTY. It is free software, and you are welcome to
        redistribute it under the terms of the GNU GPL v3 or later; see LICENSE.

        USAGE
          stingray-tex analyze  <bundle> [options]
          stingray-tex optimize <bundle> [options]
          stingray-tex verify   <bundle> [--original <bundle>]

        The <bundle> argument is the file itself (for example 9ba626afa44a3aa3.patch_24);
        its .gpu_resources companion is found alongside it.

        OPTIONS
          --strategy <name>   balanced (default) | quality | smallest
          --quality <name>    fast | balanced (default) | best
          --threads <n>       encoder threads (default: CPU count - 4)
          --output <dir>      write results here instead of editing in place
          --backup <dir>      backup directory for in-place edits (default: ./backup)
          --no-collapse       keep solid-colour textures at their original dimensions
          --no-dedup          write duplicate payloads separately instead of sharing them
          --max-size <n>      cap texture dimensions at n (power of two); larger ones are
                              halved until they fit. The plan reports the measured
                              detail cost per texture before you commit.
          --dry-run           analyse and report without writing
          --original <path>   for verify: the pre-optimisation bundle to compare against

        EXAMPLES
          stingray-tex analyze  mymod.patch_0
          stingray-tex optimize mymod.patch_0 --dry-run
          stingray-tex optimize mymod.patch_0 --strategy smallest
        """);
}
