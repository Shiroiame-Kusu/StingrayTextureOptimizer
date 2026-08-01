# Stingray bundle format

Notes on the container used by Autodesk Stingray / Bitsquid games — Helldivers 2,
Darktide, Vermintide 2. Recovered by inspecting real bundles; everything here was
confirmed against actual files, and anything unconfirmed is called out as such.

Two independent sources sit behind this. The layouts were worked out here by
inspection, and later checked against the
[HD2 SDK](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition), a Blender
addon that reads and writes the same files. It agreed on the file table, the
type table and the texture prefix, supplied the `DSAR` container layout that
made the shipped game data readable at all, and got one field the wrong way
round — see the mip table below. Where the two disagreed, the shipped data was
asked and it decided.

A bundle is up to three files that share a base name:

| File | Contents |
| --- | --- |
| `<hash>` or `<hash>.patch_N` | Header, tables, and all CPU-side payloads |
| `<hash>.patch_N.gpu_resources` | GPU payloads: texture surfaces, mesh buffers |
| `<hash>.patch_N.stream` | Streamed payloads (often zero bytes) |

All integers are little-endian.

## Header — 0x48 bytes

| Offset | Type | Field |
| --- | --- | --- |
| 0x00 | u32 | Magic, `0xF0000011` |
| 0x04 | u32 | Type count |
| 0x08 | u64 | File count |
| 0x10 | u32 | Unknown |
| 0x14 | u32 | Unknown (0 in every sample) |
| 0x18 | u64 | Unknown |
| 0x20 | u64 | Unknown, size-like |
| 0x28 | u64 | Unknown, size-like |
| 0x30 | 0x18 | Zero padding |

The two size-like fields at `0x20` and `0x28` match **no** sum of bundle contents
in any sample checked — not total GPU size, not per-type totals, not the largest
resource, not the CPU section. They also appear nowhere else in the file. This
tool round-trips them unchanged rather than recomputing them. If you work out
what they mean, please open an issue.

## Type table — 32 bytes per entry, at 0x48

| Offset | Type | Field |
| --- | --- | --- |
| 0x00 | u64 | Unknown (0 in every sample) |
| 0x08 | u64 | Type id (murmur64 of the type name) |
| 0x10 | u64 | Number of files of this type |
| 0x18 | u32 | Alignment (16 observed) |
| 0x1C | u32 | Unknown (64 observed) |

The per-type counts must sum to the header's file count; the parser rejects the
bundle if they do not, because a wrong stride corrupts everything downstream.

### Confirmed type ids

| Id | Name |
| --- | --- |
| `0xEAC0B497876ADEDF` | `material` |
| `0xCD4238C6A0C69E32` | `texture` |
| `0xE0A48D0BE9A7453F` | `unit` |
| `0x18DEAD01056B72E9` | `bones` |

Other hashes circulate in the modding community but are not reproduced here
unless verified — a wrong entry would silently mislabel assets.

## File table — 80 bytes per entry

Immediately follows the type table, at `0x48 + 32 * typeCount`.

| Offset | Type | Field |
| --- | --- | --- |
| 0x00 | u64 | File id |
| 0x08 | u64 | Type id |
| 0x10 | u64 | CPU payload offset, within the bundle file |
| 0x18 | u64 | Stream payload offset |
| 0x20 | u64 | GPU payload offset, within `.gpu_resources` |
| 0x28 | u64 | Unknown |
| 0x30 | u64 | Unknown |
| 0x38 | u32 | CPU payload size |
| 0x3C | u32 | Stream payload size |
| 0x40 | u32 | GPU payload size |
| 0x44 | u32 | Unknown (16 observed; the SDK defaults it to 16 too) |
| 0x48 | u32 | Unknown (64 observed, and 64 is the GPU payload alignment) |
| 0x4C | u32 | Entry index |

This table is confirmed field for field by the HD2 SDK's `TocEntry`, including
the two unknowns at 0x28 and 0x30, which it also leaves unnamed. The pair at
0x44/0x48 defaults to 16 and 64 there, and the same pair with the same defaults
appears in the type table — so they are plausibly alignments, one of which
matches the 64-byte GPU payload alignment. That reading is unconfirmed.

Entries are grouped by type, in the same order as the type table.

## GPU payload layout

Payloads are packed in file-table order with **64-byte alignment**. In the
samples examined, every payload starts on a 64-byte boundary and there is no
slack at the end of the file: the last payload ends exactly at EOF.

Writing at a coarser alignment is safe; a finer one is not.

## Shared payloads

Nothing in the format requires payloads to be disjoint: each entry carries its
own `(offset, size)`, so several entries can address the same bytes. No vanilla
bundle examined does this, but mod bundles frequently contain many byte-identical
payloads written out separately — in one real sample, 164 of 183 payloads were
duplicates, wasting 357 MiB of 522 MiB.

A verifier must therefore distinguish *exact* aliases (same offset, same size —
intentional sharing) from partial overlap, which is always a bug.

## Streamed textures

A texture may keep its mip chain in the `.stream` file and leave only a small
resident tail in `.gpu_resources`. Such an entry has a non-zero stream size, a
mip count above 1, and a GPU size far smaller than its dimensions imply — one
real example is 512×512 BC7 with 10 mips, 349,552 stream bytes and 1,392 resident
bytes, with `dwPitchOrLinearSize` left at 0.

Do not assume `gpuSize` equals the full surface size. It only does so for
single-mip, non-streamed textures.

Note also that `dwPitchOrLinearSize` means *pitch* (bytes per row) for
uncompressed formats and *linear size* (total bytes) for block-compressed ones,
selected by the `DDSD_PITCH` / `DDSD_LINEARSIZE` flags.

## Texture CPU payload

A texture's CPU payload is a Stingray-specific header followed by a standard DDS
image header. The DDS magic is located by search rather than at a fixed offset,
since the prefix length varies.

```
[ Stingray texture header ][ "DDS " + DDS_HEADER (128) ][ DDS_HEADER_DXT10 (20) ]
```

In observed samples the prefix is 192 bytes, giving a 340-byte payload. The
pixel data itself lives in `.gpu_resources` at the entry's GPU offset, **not**
after the header.

### The Stingray texture prefix — 192 bytes

| Offset | Type | Field |
| --- | --- | --- |
| 0x00 | u32 | Unknown id. Repeats across unrelated textures and is 0 for many. **Preserved verbatim.** |
| 0x04 | u32 | Streaming flag. 0 when the texture is not streamed. |
| 0x08 | u32 | First resident mip level; `0xFFFFFFFF` when nothing is streamed. |
| 0x0C | u32 | Zero in every sample. |
| 0x10 | u32 | Total size of the whole mip chain. Equals the entry's stream size. |
| 0x14 | 12 × n | Per-level table, one entry per mip level. |

Each per-level entry:

| Offset | Type | Field |
| --- | --- | --- |
| 0x00 | u16 | Level width |
| 0x02 | u16 | Level height |
| 0x04 | u32 | Cumulative bytes through this level — i.e. the offset of the *next* one |
| 0x08 | u32 | Bytes remaining after this level |

The final level terminates with both u32s zero rather than `(total, 0)`.

For a non-streamed texture everything from 0x04 on is zero apart from the
`0xFFFFFFFF` at 0x08: no table is written at all.

The id at 0x00 is the one field here that has to be carried across rather than
rewritten. It is **a small enumeration, not a per-texture value**: only 14
distinct values occur across the 704 streamed textures in the shipped data, and
13 across the 555 non-streamed ones, shared freely between textures that have
nothing else in common. The same values turn up in mod bundles.

| id | streamed textures | flag 1 | flag 2 |
| --- | --- | --- | --- |
| `0x00000000` | 228 | 26 | 202 |
| `0xFCE7DA44` | 118 | 89 | 29 |
| `0xA5CCAFA7` | 91 | 60 | 31 |
| `0x0172E796` | 56 | 44 | 12 |
| `0x8C3BB092` | 55 | 27 | 28 |

It correlates with the streaming flag — strongly enough to be the best predictor
of it available — but does not determine it. Nothing observed would let it be
recomputed, so converting a texture to a streamed one writes the flag, the chain
size and the table while leaving 0x00 exactly as it was. Everything from 0x0C to
the end of the prefix is safe to rewrite: no non-streamed texture examined has a
non-zero byte there, and no streamed one has a non-zero byte past the end of its
level table.

Verified across 704 streamed textures from the shipped game data — every level's
dimensions, cumulative offset and remainder matched the chain reconstructed from
the DDS header, with no exceptions.

**Width comes before height.** This is worth stating explicitly because the two
are indistinguishable on a square texture, and most textures are square. The
[HD2 SDK](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition) reads the
pair the other way round, naming them `Height` then `Width` in
`stingray/texture.py`. The shipped data settles it: across 310 non-square
streamed textures there are 2,677 mip entries where the order is decidable, and
**2,677 of them are width-first with no counterexamples.** A 256×64 BC-format
texture, for instance, starts its table `256, 64`.

The same source frames the whole thing differently and more naturally, as a flat
array of 15 entries starting at 0x0C, each `u32 start, u32 bytesLeft, u16, u16`.
That accounts for the same 192 bytes and explains two things this document
described as quirks: the "always zero" u32 at 0x0C is simply the first level's
start offset, and the "terminator" after the last level is just the first unused
entry of a fixed-size array. Both framings read and write identical bytes.

Fourteen levels is the most seen in the shipped data, and at 12 bytes an entry
the table has room for exactly 15 (`0x0C + 15 × 12 = 0xC0`), which would cover
16384×16384.

### Streaming: the field at prefix offset 8

The u32 at offset 8 of the Stingray prefix is the **index of the first mip level
held resident in `.gpu_resources`**:

| Value | Meaning |
| --- | --- |
| `0xFFFFFFFF` | Nothing is streamed. The whole mip chain is in `.gpu_resources` and resident. |
| `N` | Levels `N..` are always resident in `.gpu_resources`. The **entire** chain also lives in `.stream`, and levels `0..N-1` load on demand. |

Confirmed by reconstructing each level's size from the DDS dimensions, format and
mip count, then finding which suffix of the chain sums to the entry's `gpuSize`:
across two real bundles that suffix's start index matched this field for every
texture (29 of 29, and 1 of 1). The only exceptions are 4×4 textures, where the
entire chain is 48 bytes and the question is degenerate.

### What `.stream` actually holds

A complete copy of the chain, not just the levels above the resident floor.
`streamSize` equals the full chain size in every streamed sample (21 of 21), and
the resident `.gpu_resources` payload is byte-for-byte identical to the last
`gpuSize` bytes of that texture's `.stream` region (21 of 21). The resident tail
is a duplicate, which is what lets the engine draw something immediately while
the higher levels are still loading.

`.stream` is packed exactly like `.gpu_resources`: 64-byte aligned, in file-table
order, ending precisely at the last payload with no slack.

### What the engine does with it

Helldivers 2 ships its streaming configuration in `data/settings.ini`, which
answers what the file layout alone cannot:

```
texture_streaming = {
    inv_texel_density              = 4
    initialize_at_max              = false
    max_frame_upload_bytes         = 16777216      -- 16 MiB
    max_frame_updates              = 32
    max_frame_copy_bytes           = 67108864      -- 64 MiB
    memory_budget_mb               = 1536
    memory_threshold_fast_shrink   = 33554432      -- 32 MiB
    minimum_unseen_delay           = 60
    minimum_shrink_delay           = 30
    minimum_unload_delay           = 2
    streaming_update_quick_drops   = false
}
```

Levels are chosen by **texel density** — texels per screen pixel — so what
matters is a texture's apparent size, not distance. `initialize_at_max = false`
means the engine starts low and climbs, rate-limited to 16 MiB and 32 texture
updates per frame, which is where the brief softness after something appears
comes from.

Streamed levels are **evicted**: the pool has a 1536 MB budget, textures unseen
for `minimum_unseen_delay` become candidates, levels are dropped after
`minimum_shrink_delay` and unloaded after `minimum_unload_delay`, and the engine
shrinks harder once within 32 MiB of the budget. The delays carry no units in
the file.

The consequence for anything rewriting bundles is that the two companion files
are governed differently:

| File | Allocation |
| --- | --- |
| `.gpu_resources` | always allocated, unbounded, against `d3d12.texture_heap_usage_limit` (5376 in the shipped config) |
| `.stream` | bounded by `memory_budget_mb`, with the engine evicting to stay under it |

So moving a chain into `.stream` does not merely lower the resident figure — it
moves that memory from an unbounded permanent allocation into a budgeted pool
whose peak the engine enforces.

### The field at prefix offset 4

A streaming flag. Measured over **1,259 distinct texture headers read out of the
shipped game data**:

| Value | Textures | Meaning |
| --- | --- | --- |
| 0 | 555 | Not streamed, without a single exception |
| 1 | 353 | Streamed |
| 2 | 351 | Streamed |

So zero versus non-zero is settled. **What separates 1 from 2 is not, and it
cannot be worked out from the texture at all.** Seventy-two distinct
`(width, height, levels, format)` shapes occur with both values, and five groups
are identical in *every* field the header has — the id at 0x00, the dimensions,
the format, the level count, the first resident level and the entire mip table —
and still differ here. Whatever selects it lives outside the texture.

An earlier revision of this document blamed sample poverty for that. It was not
sample poverty. With sixty times the data the answer is that the field is not a
function of the texture.

Candidate rules scored against those 704 streamed textures:

| Rule | Correct |
| --- | --- |
| `id == 0 → 2, else 1` | **75.1%** |
| best any id-based rule could do | 77.4% |
| always 1 | 50.1% |
| always 2 | 49.9% |
| `levels >= 9 → 2` (what this tool shipped in 0.1.1) | 42.9% |

The id at 0x00 is the best predictor there is, which is unsurprising if both
fields come from the same piece of authoring metadata. So `--stream` now writes
`2` when the preserved id is zero and `1` otherwise. That is a better-founded
guess than the mip-count rule it replaces — which was worse than a coin toss —
but it is still a guess. It stays off by default and experimental, and it is the
first field to change if a converted texture fails to load.

One thing does make the risk bounded: both values are attested for every texture
shape, so neither is obviously invalid for any particular texture.

**Swapping the two changes almost nothing in game.** Tested directly: the flag
was inverted on all 53 streamed textures across two mods — 46 going 2 → 1 and 7
going 1 → 2, so both directions were covered — and the result in game was
reported as almost no difference. Nothing failed to load and nothing rendered
wrong.

That is one test on two mods rather than a proof the field is inert, and "almost
no difference" is not "no difference": a streaming-priority effect would show up
as slightly different pop-in, which is exactly the kind of thing that phrase
covers. But it does mean picking the value wrong is not the kind of mistake that
breaks a texture, which is what the caution around this field was mostly about.

This matters for memory rather than disk. A 4096×4096 BC7 texture with a full
13-level chain is 21.3 MiB; if the field says `0xFFFFFFFF`, all of it is resident
at once. Mod tooling appears to write `0xFFFFFFFF` routinely, which defeats the
streaming the engine would otherwise do — in one real mod, every texture above
512 px was fully resident, and only six small untouched entries still had a
streamed tail.

Fields this tool rewrites, all in place so the payload length never changes:

- `dwFlags` — swaps `DDSD_PITCH` (0x8) for `DDSD_LINEARSIZE` (0x80000)
- `dwHeight`, `dwWidth`
- `dwPitchOrLinearSize` — the compressed surface size
- `DDS_HEADER_DXT10.dxgiFormat`

Because the header length is fixed, every `Offset` in the file table stays valid
and the CPU section never has to be rebuilt.

## Why mods end up enormous

Textures exported straight from an image editor land as
`DXGI_FORMAT_R8G8B8A8_UNORM` with a single mip level — 4 bytes per pixel, so a
4096×4096 texture is exactly 64 MiB. Block-compressing to BC7 gives 1 byte per
pixel (4×); BC1 gives 0.5 (8×).

The larger win is content-dependent. Real mods routinely contain textures where
whole channels are constant: an unused normal map slot filled with (127,127,255),
or a mask that is solid black everywhere. Those compress to almost nothing, and a
solid-colour surface can be collapsed to 16×16 with no visible change at all,
since sampling a constant texture gives the same result at any resolution.

## The shipped game data: `DSAR`

The game's own data is not in the layout above. It is in two other shapes:

- 30 `bundles.NN.nxa` archives, roughly 21 GB in total
- around a dozen loose hash-named files with large `.stream` siblings

Both begin `DSAR`. The name suggests "DirectStorage archive", and `bin/` does
ship `dstorage.dll` and `dstoragecore.dll` — which is why an earlier revision of
this document guessed GDeflate as the codec. **That guess was wrong.** The
chunks are plain **LZ4 block**, which the
[HD2 SDK](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition) decodes in
`utils/slim.py`, and which `LZ4_decompress_safe` from a stock liblz4 reproduces
byte for byte: every container in `data/` decompresses to a bundle whose first
four bytes are `0xF0000011`.

The container header decodes cleanly:

| Offset | Type | Field |
| --- | --- | --- |
| 0x00 | char[4] | `DSAR` |
| 0x04 | u32 | Version, `0x00010003` in every file seen |
| 0x08 | u32 | Chunk count |
| 0x0C | u32 | Offset of the first chunk's data, always `0x20 + 0x20 × chunkCount` |
| 0x10 | u64 | Total decompressed size |
| 0x18 | char[8] | `PADDING*` |
| 0x20 | 32 × n | Chunk table |

That arithmetic is exact on every sample (`90656 − 32 = 2832 × 32`,
`4768 − 32 = 148 × 32`, `64 − 32 = 1 × 32`).

Each 32-byte chunk table entry:

| Offset | Type | Field |
| --- | --- | --- |
| 0x00 | u64 | Offset of this chunk in the decompressed stream |
| 0x08 | u64 | Offset of this chunk's bytes in the container |
| 0x10 | u32 | Decompressed size |
| 0x14 | u32 | Compressed size |
| 0x18 | u8 | Compression: 0 = stored, 3 = LZ4 block |
| 0x19 | u8 | Chunk flags: 0x01 unknown, 0x02 starts a resource, 0x04 continues one |
| 0x1A | 6 | Padding |

A `bundles.NN.nxa` holds many bundles end to end in its decompressed stream.
`bundles.nxa` is the index that says where each one starts: bundle count at
0x0C, package count at 0x10, then 0x18-byte package records from 0x18 —
`u64 size`, `u32 name offset`, `u32 item count`, `u32 item offset` — and
0x10-byte items giving the archive offset, the offset within the decompressed
stream, and the container number at byte 0x0F.

This is what makes the shipped data readable, and everything below about the
streaming flag comes from reading it: 1,259 distinct texture headers, against
the 21 that mod bundles alone could offer.
