<!--
SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
SPDX-License-Identifier: GPL-3.0-or-later
-->

# Changelog

Notable changes to Stingray Texture Optimizer. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[semantic versioning](https://semver.org/spec/v2.0.0.html), loosely, while this
is still 0.x.

Figures quoted here are measured on real bundles, not estimated.

## [Unreleased]

### Changed

- **The streaming flag `--stream` writes now follows the prefix id** rather than
  the mip count. Reading the shipped game data settled that this field cannot be
  derived from the texture at all: textures identical in every header field —
  id, dimensions, format, level count, first resident level, the whole mip
  table — carry both of its values. Scored against 704 streamed textures from
  the shipped data, the new rule agrees 75.1% of the time against a 77.4%
  ceiling for anything id-based; the rule 0.1.1 shipped with scored 42.9%,
  worse than a coin toss.
- **Unticking *Generate mipmaps* clears a stream floor that can no longer reach
  anything.** On a bundle where no texture carries a chain, a floor has no
  levels to move and no tail to leave behind. Where chained textures do exist
  the floor stays, so "stream what already has a chain and re-encode nothing"
  is still something you can ask for.

### Documented

- **The shipped game data is readable.** Its `DSAR` containers are LZ4 block,
  not the GDeflate the neighbouring DirectStorage DLLs implied. Container and
  chunk-table layout, and the `bundles.nxa` index, are written up in
  `docs/bundle-format.md`. This turned 21 mod-bundle samples into 1,259
  engine-authored texture headers.
- **The mip table stores width before height.** The
  [HD2 SDK](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition) reads
  that pair the other way round. Across 310 non-square streamed textures there
  are 2,677 entries where the order is decidable and all 2,677 are width-first.
  No code change — the writer was already correct — but every sample it had
  previously been checked against was square, so the check had never run.
- **The id at prefix offset 0 is a small enumeration**: 14 distinct values
  across 704 streamed textures, shared by unrelated ones.
- **Getting the streaming flag wrong appears harmless.** It was inverted on all
  53 streamed textures across two mods, both directions covered, and the
  difference in game was reported as almost none. One test, not a guarantee.
- README screenshots retaken against the current window, plus a new one showing
  the streaming path: the same bundle as the first shot with one dropdown
  changed takes 64 MiB textures to 85.4 KiB resident.

## [0.1.1] — 2026-08-01

Two ways to cut video memory that cost no quality, and the fixes that made them
safe.

### Added

- **Mip streaming — `--stream N`, off by default.** Moves a texture's whole mip
  chain into `.stream` and keeps only level `N` and below permanently resident.
  Nothing is discarded and nothing is re-encoded. On one mod, video memory fell
  205.6 → 99.4 MiB. Because the engine caps its streaming pool at 1536 MB while
  resident textures are unbounded, this converts an unbounded cost into a
  bounded one.
- **Mip generation — `--add-mips`, off by default.** Builds a chain for textures
  that shipped without one, which many mods do. Those shimmer when minified and
  cannot be streamed at all. Seven 4096² textures: 475.1 MiB untouched, 203.1
  MiB compressed, 123.5 MiB with a generated chain streamed at 256.
- **Mip modes — `--mips keep|single`** for textures that already have a chain.
- Streamed textures are now verified: the prefix chain size, every level's
  dimensions and offsets, the terminator, and both payload sizes.
- The GUI is published Native AOT, with everything it needs in the archive.
- Skipped textures report their dimensions alongside the reason.

### Fixed

- **Converting a texture to a streamed one wiped an id in its prefix** that
  nothing else in the file derives, and that 13 of 21 streamed textures in real
  mods carry. The verifier now compares it against the original, because losing
  it left a bundle that was structurally perfect.
- **A size cap and a stream floor applied together halved dimensions twice**,
  corrupting textures. Geometry is snapshotted rather than read back from the
  header being rewritten.
- **Mipmapped textures were written with headers that lied about them**: a
  format override wrote BC7 bytes under a BC1 header, and uncompressed chains
  were written raw under a block-compressed one.
- **The verifier skipped streamed and mipmapped textures entirely**, which is
  how the above shipped.
- `--mips` never reached `optimize`, so it only ever affected `analyze`.
- `--output` left the `.stream` file behind, so streamed textures in the output
  had no data.
- A stream floor at or above what survives the size cap kept the whole chain
  resident *and* duplicated it into `.stream`.
- The skipped-texture list was clipped with no way to scroll it; a real bundle
  skips 28 and showed seven and a half.
- `--screenshot` crashed on exit every run with an unhandled
  `TaskCanceledException` from the D-Bus teardown.
- `dwPitchOrLinearSize` was written as a surface size under `DDSD_PITCH`, which
  describes an uncompressed texture as one row tall.
- `.gpu_resources` is opened with `FileShare.Delete`, without which an in-place
  rewrite cannot rename its temporary over the original on Windows.

### Changed

- CI builds the native encoder shim before running the tests, so the
  Compressonator path is actually exercised rather than skipped.

## [0.1.0] — 2026-07-31

First release. Shrinks Stingray/Bitsquid bundles by analysing what their
textures actually contain — block compression, collapsing surfaces that turn out
to be a single colour, sharing byte-identical payloads, and an optional size cap
that reports its measured cost per texture. Ships a GUI and a Native AOT command
line tool, an optional native encoder built from AMD Compressonator's CMP_Core,
and an independent verification pass after every write.

Measured on two real mods:

| Mod | Disk | Video memory |
| --- | --- | --- |
| 475 MiB, uncompressed | 174.7 MiB | 203.1 MiB |
| 521 MiB, already BC7 | 114.9 MiB | 303.9 MiB |
| the same, with a 2048 cap | 54.9 MiB | 111.9 MiB |

[Unreleased]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/releases/tag/v0.1.0
