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

- **`stingray_cmp.dll` carries a version resource** naming what it is, who wrote
  it and what licence it is under. An unsigned native library with no identity
  at all is what a dropped payload looks like, and this one had none.
  - Found by diffing the Windows payload of 0.1.1, which nothing objected to,
    against 0.1.3, which one engine did. Exactly one thing is materially
    different: until the export marker was fixed the DLL exported nothing, so
    the linker stripped it to 60 KB of padding — entropy 1.8, a hollow file.
    Fixing that made it 208 KB of vector code at entropy 5.4, with `.text` 28
    times larger. The executables are unchanged either side of it: entropy
    6.53–6.55 in all three releases, and `stingray-tex.exe` is the same size in
    every one. So the flagged thing is a library every scanner is meeting for
    the first time.
  - This removes one signal. It is not a guarantee, and the reliable remedy is
    a false-positive submission; both READMEs now say so, and note that the
    library is optional — delete it and the managed encoder takes over.

## [0.1.3-1] — 2026-08-03

A rebuild of 0.1.3. Nothing here changes what the tool does; it closes the
remaining half of the encoder race by construction rather than by timing, and
makes the archives checkable.

### Fixed

- **The BC7 lookup tables are now built once per process behind `call_once`**,
  rather than once per encode call before the workers start. The previous fix
  removed the case that actually corrupts — several workers inside one call,
  which is what a 4096² texture does every time — but it left the
  initialisation unsynchronised, so what remained was a race being won on
  timing rather than a guarantee: a second caller has to allocate and spawn
  threads before it encodes, and that happens to outlast the table fill. Two
  callers can encode at once, though; the test suite runs its classes in
  parallel and nothing stops the GUI doing the same later. The single-worker
  path skipped the warm-up entirely.
  - Measured across 1,350 forced-simultaneous trials at one, two and four
    workers: none corrupt, before or after. This one is a correctness argument,
    not an observed failure — the observed one was the pre-fix build, which
    corrupts 142 times in 150 under the same harness.

### Added

- **Release archives carry GitHub build provenance**, signed during the release
  run and tied to the commit that produced it, so anyone can check that what
  they downloaded came from this source rather than from someone who reuploaded
  it: `gh attestation verify <archive> --repo Shiroiame-Kusu/StingrayTextureOptimizer`.
  SHA-256 sums for every archive go in the release notes.
  - Prompted by an antivirus flagging the Windows build as
    `Trojan.Malware.300983.susgen` — a generic heuristic verdict from one engine.
    Provenance does not stop a scanner guessing, but it does answer the question
    the guess raises. Both READMEs now record what the binaries import: no
    networking API of any kind, nothing that starts a process, nothing that
    writes to the registry, and SHA-256 for finding byte-identical payloads.

## [0.1.3] — 2026-08-03

Optimising a whole mod folder at once, and three ways the encoder was wrong that
only running the tests on Windows was ever going to find.

### Added

- **Open folder…** scans a directory for mods instead of making you hunt for
  bundle files. Everything underneath is searched, one entry per bundle with
  what it costs; clicking one opens its plan. On a real mod manager's folder
  that is 213 bundles and 1.4 GiB of GPU data.
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
- `--version` also reports which encoder is in use, which is the first thing
  worth knowing when a build is slower than expected, and needs no bundle to
  ask.

### Fixed

- **Rewriting a bundle in place failed on Windows**, every time, with "access
  denied" at the moment of commit. Windows will not rename over a file while any
  handle to it is open, and the repack reads its payloads from the very file it
  then replaces. `FileShare.Delete` was believed to cover this and does not —
  it permits unlinking, not replacing. The source is now closed before the
  commit, where every read from it is long finished.

  This shipped in 0.1.0. The tool has never been able to optimise a bundle on
  Windows.
- **The first BC7 texture of every run was encoded wrong** whenever the fast
  encoder ran on more than one thread, which is the default. CMP_Core builds a
  set of global lookup tables the first time BC7 options are created, behind a
  static flag it sets to true *before* filling them and with no synchronisation;
  the shim created options inside each worker, so every thread after the first
  was waved past that flag and encoded its blocks against tables still being
  written. The tables are now built once, on one thread, before any worker
  starts.
  - Measured on a 32×32 surface, first encode in a fresh process, 300 runs per
    configuration: at 2 threads 142 runs came out wrong, at 4 threads 294, at 8
    threads all 300 — around half the blocks in each. After the fix, none of 900.
  - Only the first texture in a run was affected: once the tables are built, the
    flag does what it looks like it does. The result was structurally valid, so
    verification passed it — the damage is wrong pixels in one texture, not a
    broken bundle.
  - This is why `--threads 1` would have produced different output from the
    default. It shipped in 0.1.0, wherever the fast encoder was available.
- **The fast encoder never worked on Windows.** The native shim built, shipped
  and loaded there with no entry points in it at all: a Windows DLL exports
  nothing unless each function says so, while an ELF shared object exports
  everything by default, so marking them was never needed until the shim reached
  Windows. Every Windows build has quietly fallen back to the managed encoder,
  which is several times slower — `Encoder: BCnEncoder.Net (native shim is
  missing an expected entry point)` in the corner of the window.
  - CI now asks the binary it is about to publish which encoder it found, and
    fails the build if a shim was bundled that the build cannot use. Nothing
    checked before: the tests skip the fast encoder when it is absent, so they
    passed either way.

### Changed

- **The test suite runs on Windows as well as Linux**, which is what caught the
  above: 60 tests failed on the first run, all of them the same line. The
  coverage had existed since 0.1.0 — `DeduplicationTests` calls `Apply` inside
  the `using` that holds the reader — but the test job ran on Linux alone, where
  a rename over an open file always works, so it could not fail. Windows
  appeared in the build matrix only to publish binaries it had never run a test
  against.
  - Paths past Windows' 260-character limit are covered end to end as well:
    found, read, rewritten, verified. They turned out to be fine — that test
    failed on the rename like all the others, not on the length.

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

[Unreleased]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.3-1...HEAD
[0.1.3-1]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.3...v0.1.3-1
[0.1.3]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Shiroiame-Kusu/StingrayTextureOptimizer/releases/tag/v0.1.0
