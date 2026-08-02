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

### Added

- **Open folder…** scans a directory for mods instead of making you hunt for
  bundle files. Everything underneath is searched, one entry per bundle with
  what it costs, largest first; clicking one opens its plan. On a real mod
  manager's folder that is 213 bundles and 1.4 GiB of GPU data.
  - `backup` folders are skipped, including the ones this tool writes, so a mod
    already optimised does not appear twice — once as it is and once as it was.
    Verified against a real folder: 223 `.gpu_resources` files, 10 of them
    inside backups, 213 listed.
  - The list is the folder tree, so a mod's variants sit beneath the mod they
    belong to. A flat list could not say that "Long boi" and "Shorty" are two
    options of one mod. A mod holding a single bundle is shown as one line
    rather than a folder to open.
  - **Several bundles can be analysed and optimised together.** Tick them —
    ticking a mod ticks its variants — and the button becomes Analyse. Every
    ticked bundle is then analysed into one grid, with a Mod column saying which
    bundle each row came from and the totals covering the whole selection; only
    then does the button become Optimize. So a batch is reviewed before it is
    written, exactly as a single bundle is, and individual textures can still be
    unticked across any of them. Writing backs up and verifies each bundle in
    turn, and anything that would not shrink is counted and left alone rather
    than forced.
  - **Plans are kept, so choosing is never a reason to read anything twice.**
    Untick a mod and its rows leave the grid; tick it back and they return as
    they were, per-texture ticks included. Tick something new and the button
    offers to analyse only what is outstanding. On a real folder: four bundles
    take 2.9 s, after which adding a fifth costs 156 ms rather than another 2.9
    s, and unticking or reticking a mod costs 1–2 ms. Looking at a bundle on its
    own counts too — tick it afterwards and it is already done. Changing a
    setting is the one thing this does not survive, since a plan describes the
    settings it was built under.
  - The screen always shows what the button would write. Opening one bundle
    takes a batch off the grid rather than leaving its totals summed against
    that bundle's, and the half-ticked mark is something a mod is told it is by
    its options, never something a click can ask for — a bundle, having no
    options, is never in it at all.
  - **A bundle that cannot be read is named rather than dropped.** A folder that
    quietly lists fewer mods than it holds sends you looking for the wrong
    problem, and on Windows the usual cause is another process holding the file
    — the game, or a mod manager. A path too long for the platform lands here
    too, since that is an `IOException` like any other.

### Changed

- **The test suite runs on Windows as well as Linux.** Repacking renames a
  temporary over a `.gpu_resources` that is still open for reading, which Unix
  allows unconditionally and Windows allows only because the reader asks for
  `FileShare.Delete`. The tests already covered it — `DeduplicationTests` calls
  `Apply` inside the `using` that holds the reader — but the test job ran on
  Linux alone, so the coverage existed and never ran anywhere it could fail.
  Windows appeared in the build matrix only to publish binaries it had never run
  a test against. Paths past Windows' 260-character limit are now covered end to
  end as well: found, read, rewritten, verified.

## [0.1.2] — 2026-08-02

### Added

- **The GUI speaks Simplified Chinese**, and picks its language from the system
  at startup — the POSIX locale variables on Unix, the user's UI language on
  Windows. `STINGRAY_LANG=zh` or `=en` overrides it. Traditional Chinese locales
  get the Simplified translation, on the grounds that it reads closer than
  English does.
  - Not resources and satellite assemblies: this project builds with
    `InvariantGlobalization`, under which `CurrentUICulture.Name` is empty and
    `new CultureInfo("zh-CN")` throws. Translations sit inline instead, so a
    missing one is a compile error rather than a silent fallback.
  - The command line stays English whatever the system says, because its output
    is documented, diffed and scripted against.
  - CI renders the published binary in both languages, since the localised
    strings are reached only through `x:Static` and a trimming change could drop
    them without a warning.

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

[Unreleased]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/releases/tag/v0.1.0
