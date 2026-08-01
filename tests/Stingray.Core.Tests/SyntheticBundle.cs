// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using Stingray.Core.Dds;
using Stingray.Core.Format;

namespace Stingray.Core.Tests;

/// <summary>
/// Builds a valid bundle pair on disk from scratch.
/// </summary>
/// <remarks>
/// Tests must never depend on real game bundles: those are copyrighted assets and
/// cannot be committed. Synthesising one also lets tests assert on exact pixel
/// content, which is impossible with a sample file.
/// </remarks>
internal sealed class SyntheticBundle : IDisposable
{
    private const int StingrayPrefix = 192;
    private const int DdsLength = 148;
    private const int CpuPayloadSize = StingrayPrefix + DdsLength;

    private readonly List<(ulong TypeId, byte[] Cpu, byte[] Gpu, byte[] Stream)> _entries = [];

    public string Directory { get; } =
        Path.Combine(Path.GetTempPath(), "stingray-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public string BundlePath => Path.Combine(Directory, "test.patch_0");

    /// <summary>Adds an uncompressed RGBA8 texture whose pixels come from <paramref name="fill"/>.</summary>
    public SyntheticBundle AddTexture(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> fill)
    {
        var surface = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b, a) = fill(x, y);
            var i = (y * width + x) * 4;
            surface[i] = r; surface[i + 1] = g; surface[i + 2] = b; surface[i + 3] = a;
        }

        _entries.Add((StingrayTypeIds.Texture, BuildTextureCpuPayload(width, height), surface, []));
        return this;
    }

    /// <summary>Adds a non-texture payload that the optimiser must copy through untouched.</summary>
    public SyntheticBundle AddOpaqueAsset(int gpuSize, byte seed)
    {
        var gpu = new byte[gpuSize];
        for (var i = 0; i < gpu.Length; i++) gpu[i] = (byte)(seed + i);
        _entries.Add((StingrayTypeIds.Unit, new byte[64], gpu, []));
        return this;
    }

    /// <summary>
    /// Adds a streamed texture shaped the way real ones are: the whole chain in
    /// <c>.stream</c>, a duplicate of the tail resident in <c>.gpu_resources</c>,
    /// and a prefix carrying the level table.
    /// </summary>
    public SyntheticBundle AddStreamedChain(int width, int height, DxgiFormat format,
                                            int mipCount, int firstResidentMip)
    {
        var chain = format.MipChain(width, height, mipCount);
        var full = new byte[chain.Sum()];
        for (var i = 0; i < full.Length; i++) full[i] = (byte)(i * 13 + 3);

        var resident = full.AsSpan((int)chain.Take(firstResidentMip).Sum()).ToArray();

        var cpu = BuildTextureCpuPayload(width, height, mipCount, (uint)format, linearSize: 0);
        Stingray.Core.Textures.StingrayTexturePrefix.WriteStreaming(
            cpu, StingrayPrefix, format, width, height, mipCount, firstResidentMip);

        _entries.Add((StingrayTypeIds.Texture, cpu, resident, full));
        return this;
    }

    /// <summary>
    /// Adds an already-compressed texture carrying a full mip chain, the shape
    /// that mip dropping operates on.
    /// </summary>
    public SyntheticBundle AddMippedTexture(int width, int height, DxgiFormat format, int mipCount)
    {
        var chain = format.MipChain(width, height, mipCount);
        var gpu = new byte[chain.Sum()];
        for (var i = 0; i < gpu.Length; i++) gpu[i] = (byte)(i * 31 + 7);

        var cpu = BuildTextureCpuPayload(width, height, mipCount, (uint)format,
                                         (int)format.SurfaceSize(width, height));
        _entries.Add((StingrayTypeIds.Texture, cpu, gpu, []));
        return this;
    }

    public SyntheticBundle Write()
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Group by index rather than by value: two identical entries must stay
        // distinct, which GroupBy over the tuples themselves would not guarantee.
        var typeGroups = _entries
            .Select((e, i) => (e.TypeId, Index: i))
            .GroupBy(x => x.TypeId)
            .ToList();

        var tableEnd = BundleFormat.HeaderSize
                     + typeGroups.Count * BundleFormat.TypeEntrySize
                     + _entries.Count * BundleFormat.FileEntrySize;

        // Lay out CPU payloads after the tables, and GPU payloads 64-byte aligned.
        var cpuOffsets = new int[_entries.Count];
        var cursor = tableEnd;
        for (var i = 0; i < _entries.Count; i++)
        {
            cpuOffsets[i] = cursor;
            cursor += _entries[i].Cpu.Length;
        }

        var image = new byte[cursor];
        BinaryPrimitives.WriteUInt32LittleEndian(image, BundleFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x04), (uint)typeGroups.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x08), (ulong)_entries.Count);

        var p = BundleFormat.HeaderSize;
        foreach (var group in typeGroups)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p + 0x08), group.Key);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p + 0x10), (ulong)group.Count());
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x18), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x1C), 64);
            p += BundleFormat.TypeEntrySize;
        }

        using var gpuStream = new FileStream(BundlePath + ".gpu_resources", FileMode.Create);
        using var streamFile = new FileStream(BundlePath + ".stream", FileMode.Create);
        long gpuCursor = 0;
        long streamCursor = 0;

        // Entries must be grouped by type, matching how real bundles are written.
        var ordered = typeGroups.SelectMany(g => g.Select(x => x.Index)).ToList();
        var slot = 0;
        foreach (var index in ordered)
        {
            var (typeId, cpu, gpu, stream) = _entries[index];

            var pad = (int)(OptimizationAlign(gpuCursor) - gpuCursor);
            if (pad > 0) { gpuStream.Write(new byte[pad]); gpuCursor += pad; }
            gpuStream.Write(gpu);

            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p), 0x1000UL + (ulong)index);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p + 0x08), typeId);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p + 0x10), (ulong)cpuOffsets[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p + 0x20), (ulong)gpuCursor);
            if (stream.Length > 0)
            {
                // Real .stream files align payloads the same way .gpu_resources does.
                var streamPad = (int)(OptimizationAlign(streamCursor) - streamCursor);
                if (streamPad > 0) { streamFile.Write(new byte[streamPad]); streamCursor += streamPad; }

                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p + 0x18), (ulong)streamCursor);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x3C), (uint)stream.Length);
                streamFile.Write(stream);
                streamCursor += stream.Length;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x38), (uint)cpu.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x40), (uint)gpu.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x44), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x48), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p + 0x4C), (uint)slot);

            cpu.CopyTo(image.AsSpan(cpuOffsets[index]));
            gpuCursor += gpu.Length;
            p += BundleFormat.FileEntrySize;
            slot++;
        }

        File.WriteAllBytes(BundlePath, image);
        return this;
    }

    private static long OptimizationAlign(long value)
    {
        var rem = value % BundleFormat.GpuAlignment;
        return rem == 0 ? value : value + (BundleFormat.GpuAlignment - rem);
    }

    private static byte[] BuildTextureCpuPayload(int width, int height,
                                                 int mipCount = 1, uint dxgiFormat = 28,
                                                 int? linearSize = null)
    {
        var payload = new byte[CpuPayloadSize];

        // Offset 8 of the Stingray prefix is the first resident mip level;
        // 0xFFFFFFFF means nothing is streamed, which is what real non-streamed
        // textures carry.
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0xFFFF_FFFF);

        var dds = payload.AsSpan(StingrayPrefix);

        BinaryPrimitives.WriteUInt32LittleEndian(dds, 0x2053_4444);            // "DDS "
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x04..], 124);            // dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x08..], 0x0002_100F);    // caps|h|w|pitch|pf|mips
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x0C..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x10..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x14..], (uint)(linearSize ?? width * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x18..], 1);              // depth
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x1C..], (uint)mipCount); // mip count
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x4C..], 32);             // ddspf.dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x50..], 0x4);            // DDPF_FOURCC
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x54..], 0x3031_5844);    // "DX10"
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x6C..], 0x1000);         // DDSCAPS_TEXTURE
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x80..], dxgiFormat);     // dxgiFormat
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x84..], 3);              // TEXTURE2D
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x8C..], 1);              // arraySize
        return payload;
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }
}
