// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using Stingray.Core.Dds;

namespace Stingray.Core.Textures;

/// <summary>
/// The Stingray-specific header that precedes the DDS image in a texture's CPU
/// payload, and the streaming description it carries.
/// </summary>
/// <remarks>
/// Layout was recovered by inspection; see <c>docs/bundle-format.md</c>. A
/// non-streamed texture writes only the marker at <see cref="FirstResidentMipOffset"/>
/// and leaves the rest zero. A streamed one additionally carries the total chain
/// size and a per-level table.
/// </remarks>
public static class StingrayTexturePrefix
{
    /// <summary>Streaming flag. Zero when the texture is not streamed.</summary>
    public const int StreamingFlagOffset = 4;

    /// <summary>First mip level held resident in <c>.gpu_resources</c>.</summary>
    public const int FirstResidentMipOffset = 8;

    /// <summary>Total size of the whole mip chain.</summary>
    public const int ChainSizeOffset = 16;

    /// <summary>Start of the per-level table.</summary>
    public const int LevelTableOffset = 20;

    /// <summary>Bytes per level entry: width, height, next offset, remainder.</summary>
    public const int LevelEntrySize = 12;

    /// <summary>
    /// Streaming flag to write for a texture carrying the given prefix id.
    /// </summary>
    /// <remarks>
    /// Measured over 1,259 distinct texture headers read out of the shipped
    /// game data: 0 means not streamed, without exception across 555 of them,
    /// and every one of the 704 streamed textures carries 1 or 2 — split 353 to
    /// 351, which is as close to a coin toss as makes no difference.
    ///
    /// Nothing in the header decides which. Five groups are identical in every
    /// field there is — id, dimensions, format, level count, first resident
    /// level and the whole mip table — and still differ here, so the value comes
    /// from somewhere outside the texture. It can only be guessed at.
    ///
    /// The id is the best predictor available. Scored against those 704:
    /// this rule is right 75.1% of the time, against 77.4% for the best any
    /// id-based rule could manage, 50% for a constant, and 42.9% for the
    /// mip-count rule this shipped with before the shipped data could be read.
    /// It is still a guess. If a converted texture fails to load in game, this
    /// is what to change first — and both values are attested for every texture
    /// shape, so neither is obviously wrong for any particular texture.
    /// </remarks>
    public static uint StreamingFlagFor(uint prefixId) => prefixId == 0 ? 2u : 1u;

    /// <summary>Room for a level table before the DDS magic.</summary>
    public static bool CanDescribe(int prefixLength, int mipCount) =>
        LevelTableOffset + (mipCount * LevelEntrySize) <= prefixLength;

    /// <summary>Reads the first-resident-mip marker, or null when absent.</summary>
    public static uint ReadFirstResidentMip(ReadOnlySpan<byte> payload) =>
        payload.Length >= FirstResidentMipOffset + 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(payload[FirstResidentMipOffset..])
            : TextureResource.NotStreamed;

    /// <summary>
    /// Writes the streaming description for a texture whose whole chain now
    /// lives in <c>.stream</c> and whose levels from
    /// <paramref name="firstResidentMip"/> down stay in <c>.gpu_resources</c>.
    /// </summary>
    /// <remarks>
    /// The prefix length never changes, so every bundle offset stays valid.
    /// </remarks>
    public static void WriteStreaming(
        Span<byte> payload, int prefixLength, DxgiFormat format,
        int width, int height, int mipCount, int firstResidentMip)
    {
        if (mipCount < 1)
            throw new ArgumentOutOfRangeException(nameof(mipCount));
        if (firstResidentMip < 0 || firstResidentMip >= mipCount)
            throw new ArgumentOutOfRangeException(nameof(firstResidentMip));
        if (!CanDescribe(prefixLength, mipCount))
            throw new ArgumentOutOfRangeException(nameof(mipCount),
                $"A {mipCount}-level table does not fit in a {prefixLength}-byte prefix.");

        var chain = format.MipChain(width, height, mipCount);
        var total = chain.Sum();
        if (total > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(mipCount), "Mip chain exceeds 4 GiB.");

        // Read before clearing: the id decides what the streaming flag should be,
        // and it is about to be the only thing left of the old prefix.
        var id = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        // Clear from the flag on, so no stale table survives from whatever the
        // texture was before — but leave that id alone. Only 14 distinct values
        // occur across the 704 streamed textures in the shipped data, shared by
        // unrelated textures, and it tracks neither format nor dimensions nor
        // level count, so there is nothing to recompute it from. Whatever it
        // means, an engine-authored streamed texture carries it, and a converted
        // one has to as well.
        payload[StreamingFlagOffset..prefixLength].Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(
            payload[StreamingFlagOffset..], StreamingFlagFor(id));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload[FirstResidentMipOffset..], (uint)firstResidentMip);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[ChainSizeOffset..], (uint)total);

        long running = 0;
        for (var level = 0; level < mipCount; level++)
        {
            var entry = payload[(LevelTableOffset + (level * LevelEntrySize))..];
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)Math.Max(1, width >> level));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)Math.Max(1, height >> level));

            running += chain[level];

            // The last level terminates with two zeros rather than (total, 0).
            var next = level == mipCount - 1 ? 0 : running;
            var remaining = level == mipCount - 1 ? 0 : total - running;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)next);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)remaining);
        }
    }

    /// <summary>
    /// Applies the DDS header conventions a streamed texture uses: the resident
    /// payload is only a tail, so the linear-size flag is cleared and the size
    /// field carries a row pitch for uncompressed formats and nothing otherwise.
    /// </summary>
    public static void PatchDdsForStreaming(Span<byte> payload, DdsHeader header,
                                            DxgiFormat format, int width)
    {
        var dds = payload[header.Offset..];
        var flags = header.Flags & ~(DdsHeader.FlagLinearSize | DdsHeader.FlagPitch);
        var size = format.IsBlockCompressed() ? 0u : (uint)(width * format.BytesPerPixel());

        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x08..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(dds[0x14..], size);
    }
}
