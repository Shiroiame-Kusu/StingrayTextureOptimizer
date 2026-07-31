# Stingray Texture Optimizer

Shrinks Stingray/Bitsquid game bundles by block-compressing the textures inside
them. Built for mod authors whose `.gpu_resources` file has ballooned to hundreds
of megabytes.

Works on bundles from Helldivers 2, Darktide and Vermintide 2 — the
`<hash>.patch_N` + `.gpu_resources` pair.

## Why

Textures exported from an image editor land in a bundle as uncompressed RGBA8
with no mipmaps: 4 bytes per pixel, so one 4096×4096 texture is exactly 64 MiB.
Block compression cuts that 4–8×.

The bigger win is content-aware. Mods regularly ship textures whose channels are
entirely constant — an unused normal map slot filled with (127,127,255), or a
mask that is solid black. This tool detects those and collapses them.

A real example, a 475 MiB mod:

```
texture          dimensions         format           size ->        new  content
DDF165B6350D7A78 4096x4096          BC7_UNORM    64.0 MiB ->   16.0 MiB  flat RGB(127,127,255), alpha has 6 values
F0D0A596CB063EB5 2048x2048->16x16   BC1_UNORM    16.0 MiB ->      128 B  solid RGBA(0,0,0,255)
21B4DAC1794DC6CC 4096x4096          BC7_UNORM    64.0 MiB ->   16.0 MiB  full detail with alpha
85472186547F6415 4096x4096          BC7_UNORM    64.0 MiB ->   16.0 MiB  flat RGB(127,127,255), alpha has 5 values
BCB6E42B5DD10AB6 2048x2048->16x16   BC1_UNORM    16.0 MiB ->      128 B  solid RGBA(0,0,0,255)
692AC91F7776F3C2 4096x4096          BC7_UNORM    64.0 MiB ->   16.0 MiB  full detail with alpha
D5EA2AD0D36C69DC 4096x4096          BC7_UNORM    64.0 MiB ->   16.0 MiB  flat RGB(127,127,255), alpha has 5 values

total 475.1 MiB -> 203.1 MiB (saves 272.0 MiB)
```

Two of those seven textures held real artwork. Two were solid black at 2048².

### Already compressed? Still probably too big

Bundles whose textures are all properly BC7 can still be mostly waste, because
mods commonly ship the same payload under many ids. A second real example — 182
textures, every one already BC7, nothing to recompress:

```
duplicates: 164 entr(ies) repeat a payload another entry already has, wasting 357.4 MiB.
            These are stored once and shared.

total 521.6 MiB -> 164.3 MiB (saves 357.4 MiB)
```

120 of those entries were byte-identical copies of one 1024×1024 texture — and
that texture turned out to be solid black. Once the analyser decodes compressed
surfaces too, the same bundle goes to **114.9 MiB with nothing lost at all**:
sharing duplicates and collapsing solid-colour surfaces are both exact.

Allowing a resize on top (`--max-size 2048`) takes it to **54.9 MiB**.

## Install

### Prebuilt binaries

Grab an archive from [Releases](../../releases). Nothing else to install — not
even a .NET runtime.

| Platform | Archive |
| --- | --- |
| Linux x64 | `stingray-tex-<version>-linux-x64.tar.gz` |
| Windows x64 | `stingray-tex-<version>-win-x64.zip` |
| macOS Intel | `stingray-tex-<version>-osx-x64.tar.gz` |
| macOS Apple Silicon | `stingray-tex-<version>-osx-arm64.tar.gz` |

Each contains `stingray-tex` (the CLI) and `stingray-tex-gui`.

The CLI is compiled with **Native AOT**: a single ~2 MB native binary with no
runtime dependency and roughly 1 ms of startup. The GUI ships self-contained
instead — Avalonia's `DataGrid` is neither trim- nor AOT-clean, so forcing AOT
on it would produce a binary with silently broken bindings.

### From source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer.git
cd StingrayTextureOptimizer
dotnet build -c Release

# optional: a Native AOT CLI build (needs clang and zlib on Linux)
dotnet publish src/Stingray.Cli -c Release -r linux-x64
```

## Use

### GUI

```sh
dotnet run --project src/Stingray.Gui -c Release
```

Open a bundle, review the plan, override any per-texture format you disagree
with, then Optimize. Originals are copied to a `backup/` folder next to the
bundle before anything is written, and the result is verified automatically.

### CLI

```sh
# See what would happen
stingray-tex analyze mymod.patch_0

# Rewrite in place (backs up to ./backup first, then verifies)
stingray-tex optimize mymod.patch_0

# Write elsewhere instead of editing in place
stingray-tex optimize mymod.patch_0 --output ./out

# Check a bundle, optionally against its pre-optimisation original
stingray-tex verify mymod.patch_0 --original backup/mymod.patch_0
```

## Options

The same settings appear in the GUI toolbar and on the command line.

### Format — `--strategy` (GUI: *Format*)

Which block format each texture is encoded to. Affects both size and quality.

| Value | GUI label | Behaviour |
| --- | --- | --- |
| `balanced` *(default)* | Balanced | BC1 where alpha carries no information, BC7 where it does |
| `quality` | Best quality | BC7 everywhere; never BC1 for real image content |
| `smallest` | Smallest size | BC1 wherever alpha carries no information |

BC1 is half the size of BC7 but has 5:6:5 colour endpoints, so it bands on
gradients. BC7 reproduces any 8-bit RGBA nearly exactly.

### Effort — `--quality` (GUI: *Effort*)

`fast` | `balanced` *(default)* | `best`. How hard the compressor searches for
the best encoding. **This does not change the output size** — only how close the
result is to the source, and how long it takes.

### Threads — `--threads`

Encoder worker threads. Defaults to **CPU count − 4**, deliberately leaving cores
free: saturating every core with a BC7 encode makes a desktop session unusable.

### Max size — `--max-size N` (GUI: *Max size*)

Halve any texture larger than N until it fits. **Off by default — this is the
only genuinely lossy option.** Every affected texture reports its measured detail
cost, so you can untick the ones that matter. See
[Resizing](#resizing-and-whether-it-will-look-blurry).

### Collapse solid colours — `--no-collapse` disables (GUI: *Collapse solid colours*)

A texture whose every pixel is identical is rewritten at 16×16. Visually
lossless, because a constant texture samples the same at any resolution. Saves
**both** file size and video memory. Only disable it if a shader reads the
texture with `textureSize()` or absolute `texelFetch` coordinates.

### Share duplicates — `--no-dedup` disables (GUI: *Share duplicates*)

Byte-identical payloads are stored once and referenced by every entry that uses
them. Lossless, and usually the largest saving in an already-compressed bundle.
**Shrinks the file only** — see [File size is not video
memory](#file-size-is-not-video-memory).

### `--dry-run`

Analyse and report without writing anything.

## Deduplication

Every GPU payload is hashed. Where several entries have byte-identical content,
the payload is written once and each entry points at it — the format stores an
explicit `(offset, size)` per entry, so nothing requires them to be disjoint.

This is lossless and usually the single largest saving in an already-compressed
bundle. Disable with `--no-dedup` if you want a strictly one-payload-per-entry
layout.

## File size is not video memory

The tool reports two numbers, because they are not the same thing:

```
disk   521.6 MiB ->   54.9 MiB   (saves 466.7 MiB)
gpu    521.6 MiB ->  111.9 MiB   (saves 409.7 MiB)
```

Sharing a duplicate payload shrinks the **file**, but the engine still builds one
GPU resource per entry, so both entries are uploaded and video memory is
unchanged. Only actually shrinking a surface helps there:

| Optimisation | Smaller file | Less video memory |
| --- | --- | --- |
| Block compression | yes | yes |
| Collapsing a solid colour | yes | yes |
| Resizing | yes | yes |
| Sharing duplicates | yes | **no** |

So if you are chasing download size, deduplication is the big win. If you are
chasing VRAM, it does nothing and you want `--max-size`.

Two caveats on the GPU figure: it assumes every texture is resident at once,
which is the worst case, and it excludes anything the engine pulls in from the
`.stream` file on demand.

## Resizing, and whether it will look blurry

Resizing is the only genuinely lossy thing the tool does, so it is **off by
default** and every affected texture is measured rather than guessed at.

For each texture the analyser halves the surface, restores it, and reports the
PSNR. That number says how much detail genuinely needs full resolution:

| Measured | Meaning |
| --- | --- |
| ∞ | Solid colour — resizing changes nothing |
| ≥ 45 dB | No visible detail at full size; halving is effectively free |
| 36–45 dB | Slight softening |
| 30–36 dB | Visible softening |
| < 30 dB | Real detail lost |

From one real bundle's eight distinct 4096² textures: three were a single colour,
three scored 47–93 dB, one scored 38 dB, and only one scored 30 dB — a normal map
whose fine ribbing did visibly soften. So "should I halve my 4K textures?" has a
different answer per texture, which is why the plan shows the number and lets you
tick each one individually.

Already-compressed textures are only ever resized, never re-encoded into a
different format: that would compound generation loss for no size gain. sRGB
formats stay sRGB, since writing a linear format where the engine expects sRGB
shifts every colour on screen.

## How it decides

| Content | Target | Lossless |
| --- | --- | --- |
| Every pixel identical | BC1 (or BC7 if the colour is not exact in 5:6:5), collapsed to 16×16 | yes |
| Alpha uniformly opaque | BC1 — or BC7 under `--strategy quality` | no |
| Alpha carries detail | BC7 | no |

A constant texture samples identically at any resolution, so collapsing it is
visually lossless. BC1 endpoints are 5:6:5, so a colour like (127,127,255) does
*not* survive it; the analyser checks and falls back to BC7, which reproduces any
8-bit RGBA exactly.

Already-compressed textures are decoded and analysed too — that is how a
4096×4096 BC7 surface holding one colour gets found. Textures with a legacy
FourCC header, mipmaps, BC6H content, or dimensions that are not a multiple of 4
are reported and skipped.

## Safety

Rewriting a bundle only touches two things: DDS header fields, and each entry's
GPU offset and size. CPU payload lengths never change, so every offset in the
file table stays valid, and non-texture assets are copied through byte for byte.

Every write is followed by an independent verification pass that re-reads the
result and checks alignment, overlap, bounds, header/table agreement, and — when
given the original — that all passthrough payloads are unchanged.

One caveat specific to deduplication: sharing a payload between entries is
consistent with the format, but no vanilla bundle examined actually does it, so
it is not a pattern the engine has been *observed* to rely on. It verifies
clean and every entry resolves to identical bytes — but this is the part most
worth confirming in-game before you ship. `--no-dedup` avoids it entirely.

That said: **this rewrites game files.** Keep the backups until you have loaded
the result in-game.

## Layout

```
src/Stingray.Core   format parsing, analysis, encoding, verification
src/Stingray.Cli    stingray-tex
src/Stingray.Gui    Avalonia desktop app
tests/              xUnit, builds synthetic bundles from scratch
docs/               reverse-engineered format notes
```

Tests never depend on real game bundles — those are copyrighted and cannot be
committed — so `SyntheticBundle` constructs valid ones in memory.

## Format

See [`docs/bundle-format.md`](docs/bundle-format.md) for the container layout,
including which fields remain unidentified.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Additions to the type-id table are
especially welcome, provided you can say how you confirmed them.

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).

> This program is free software: you can redistribute it and/or modify it under
> the terms of the GNU General Public License as published by the Free Software
> Foundation, either version 3 of the License, or (at your option) any later
> version.
>
> This program is distributed in the hope that it will be useful, but WITHOUT ANY
> WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
> PARTICULAR PURPOSE. See the GNU General Public License for more details.

Dependencies are all permissively licensed and so may be combined with GPL code:
[BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) (MIT OR Unlicense),
[Avalonia](https://avaloniaui.net/) (MIT),
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT) and
[Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) (MIT). Their own terms
continue to apply to them.

Not affiliated with Autodesk, Arrowhead, Fatshark, or any game publisher.
