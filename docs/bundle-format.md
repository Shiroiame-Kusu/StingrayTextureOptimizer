# Stingray bundle format

Notes on the container used by Autodesk Stingray / Bitsquid games — Helldivers 2,
Darktide, Vermintide 2. Recovered by inspecting real bundles; everything here was
confirmed against actual files, and anything unconfirmed is called out as such.

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
| 0x44 | u32 | Unknown (16 observed) |
| 0x48 | u32 | Unknown (64 observed) |
| 0x4C | u32 | Entry index |

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

### Streaming: the field at prefix offset 8

The u32 at offset 8 of the Stingray prefix is the **index of the first mip level
held resident in `.gpu_resources`**:

| Value | Meaning |
| --- | --- |
| `0xFFFFFFFF` | Nothing is streamed. The whole mip chain is in `.gpu_resources` and resident. |
| `N` | Levels `0..N-1` live in `.stream` and load on demand; levels `N..` are always resident. |

Confirmed by reconstructing each level's size from the DDS dimensions, format and
mip count, then finding which suffix of the chain sums to the entry's `gpuSize`:
across two real bundles that suffix's start index matched this field for every
texture (29 of 29, and 1 of 1). The only exceptions are 4×4 textures, where the
entire chain is 48 bytes and the question is degenerate.

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
