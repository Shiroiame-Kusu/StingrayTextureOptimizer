// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using Stingray.Core.Textures;

namespace Stingray.Cli;

/// <summary>
/// Minimal argument parser. Hand-rolled on purpose: the CLI has a handful of
/// flags and this keeps the tool dependency-free.
/// </summary>
internal sealed class CommandLine
{
    public string? Path { get; private init; }
    public string? Output { get; private init; }
    public string? Backup { get; private init; }
    public string? Original { get; private init; }
    public int? Threads { get; private init; }
    public bool DryRun { get; private init; }
    public bool NoCollapse { get; private init; }
    public bool NoDedup { get; private init; }
    public int MaxDimension { get; private init; }
    public EncoderBackend Backend { get; private init; } = EncoderBackend.Auto;
    public MipMode MipMode { get; private init; } = MipMode.KeepChain;
    public OptimizationStrategy Strategy { get; private init; } = OptimizationStrategy.Balanced;
    public EncodeQuality Quality { get; private init; } = EncodeQuality.Balanced;

    public string RequirePath() =>
        Path ?? throw new ArgumentException("A bundle path is required.");

    public static CommandLine Parse(IEnumerable<string> args)
    {
        string? path = null, output = null, backup = null, original = null;
        int? threads = null;
        var maxDimension = 0;
        var backend = EncoderBackend.Auto;
        var mipMode = MipMode.KeepChain;
        bool dryRun = false, noCollapse = false, noDedup = false;
        var strategy = OptimizationStrategy.Balanced;
        var quality = EncodeQuality.Balanced;

        using var it = args.GetEnumerator();
        while (it.MoveNext())
        {
            var arg = it.Current;
            switch (arg)
            {
                case "--dry-run": dryRun = true; break;
                case "--no-collapse": noCollapse = true; break;
                case "--no-dedup": noDedup = true; break;
                case "--output": output = Next(it, arg); break;
                case "--backup": backup = Next(it, arg); break;
                case "--original": original = Next(it, arg); break;
                case "--threads": threads = ParseThreads(Next(it, arg)); break;
                case "--max-size": maxDimension = ParseMaxSize(Next(it, arg)); break;
                case "--encoder": backend = ParseBackend(Next(it, arg)); break;
                case "--mips": mipMode = ParseMipMode(Next(it, arg)); break;
                case "--strategy": strategy = ParseStrategy(Next(it, arg)); break;
                case "--quality": quality = ParseQuality(Next(it, arg)); break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option '{arg}'.");
                    if (path is not null)
                        throw new ArgumentException("Only one bundle path may be given.");
                    path = arg;
                    break;
            }
        }

        return new CommandLine
        {
            Path = path, Output = output, Backup = backup, Original = original,
            Threads = threads, DryRun = dryRun, NoCollapse = noCollapse, NoDedup = noDedup,
            Strategy = strategy, Quality = quality, MaxDimension = maxDimension,
            Backend = backend, MipMode = mipMode,
        };
    }

    private static string Next(IEnumerator<string> it, string option) =>
        it.MoveNext() ? it.Current : throw new ArgumentException($"{option} requires a value.");

    private static MipMode ParseMipMode(string value) => value.ToLowerInvariant() switch
    {
        "keep" or "chain" or "keep-chain" => MipMode.KeepChain,
        "single" or "one" or "drop-chain" => MipMode.SingleLevel,
        _ => throw new ArgumentException($"Unknown mip mode '{value}'. Use keep or single."),
    };

    private static EncoderBackend ParseBackend(string value) => value.ToLowerInvariant() switch
    {
        "auto" => EncoderBackend.Auto,
        "managed" or "bcnencoder" => EncoderBackend.Managed,
        "compressonator" or "hpc" => EncoderBackend.Compressonator,
        _ => throw new ArgumentException(
            $"Unknown encoder '{value}'. Use auto, managed or compressonator."),
    };

    private static int ParseMaxSize(string value)
    {
        if (!int.TryParse(value, out var n) || n < 4 || (n & (n - 1)) != 0)
            throw new ArgumentException(
                $"--max-size needs a power of two of at least 4, got '{value}'.");
        return n;
    }

    private static int ParseThreads(string value) =>
        int.TryParse(value, out var n) && n > 0
            ? n
            : throw new ArgumentException($"--threads needs a positive integer, got '{value}'.");

    private static OptimizationStrategy ParseStrategy(string value) => value.ToLowerInvariant() switch
    {
        "balanced" => OptimizationStrategy.Balanced,
        "quality" or "max" => OptimizationStrategy.MaximumQuality,
        "smallest" or "small" => OptimizationStrategy.SmallestSize,
        _ => throw new ArgumentException($"Unknown strategy '{value}'. Use balanced, quality or smallest."),
    };

    private static EncodeQuality ParseQuality(string value) => value.ToLowerInvariant() switch
    {
        "fast" => EncodeQuality.Fast,
        "balanced" => EncodeQuality.Balanced,
        "best" => EncodeQuality.Best,
        _ => throw new ArgumentException($"Unknown quality '{value}'. Use fast, balanced or best."),
    };
}
