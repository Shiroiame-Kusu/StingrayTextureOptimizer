# Contributing

## Building

```sh
dotnet build
dotnet test
```

Requires the .NET 10 SDK. Warnings are errors — that is deliberate, and it has
already caught a real CVE in a transitive dependency.

## Licensing

This project is GPL-3.0-or-later. By contributing you agree your work is
released under those terms, and any code you bring in must be GPL-compatible —
MIT, BSD, Apache-2.0 and public-domain-equivalent licences are fine; another
copyleft licence generally is not.

## Ground rules

**Never commit game assets.** Bundles, `.gpu_resources`, `.stream` and `.dds`
files are copyrighted content and are gitignored. Tests build synthetic bundles
via `SyntheticBundle` instead; if you need a new scenario, extend that.

**Only add verified format knowledge.** The type-id table in `StingrayTypeIds`
and the notes in `docs/bundle-format.md` contain only values confirmed against
real files. Plenty of hashes circulate in the modding community; an incorrect one
would silently mislabel assets. If you add an entry, say in the PR how you
confirmed it. Unknown fields are round-tripped verbatim rather than guessed at —
please keep it that way.

**Anything that writes must be verified.** `BundleVerifier` re-reads output
independently of the writer, on purpose: shared code would mirror a writer bug
instead of catching it. New write paths need matching checks.

## Good first contributions

- **Identify the header fields at `0x20`/`0x28`.** They match no sum of bundle
  contents in any sample examined. See `docs/bundle-format.md`.
- **More type ids**, with evidence.
- **Mipmapped textures.** Currently skipped; handling them means generating and
  laying out a full mip chain, and for streamed textures splitting it across the
  `.stream` file.
- **Legacy FourCC DDS headers.** Skipped rather than converted to DX10.
- **A faster BC7 encoder.** BCnEncoder.Net is pure managed and dependency-free,
  which is why it was chosen. Anything faster must not require shipping
  per-platform native binaries.
- **Quality metrics in the GUI** — PSNR per texture after encoding. The
  pre-encode detail measurement already exists as
  `TextureAnalyzer.MeasureHalfResolutionPsnr`.
- **A perceptual metric** better than PSNR for the resize decision (SSIM or
  similar), especially for normal maps, where the current number under-weights
  how visible fine ribbing is.

## Documentation screenshots

The images in `docs/images/` are rendered by the app itself, not captured from a
desktop, so they are reproducible and contain nothing but the window:

```sh
dotnet build -c Release
./src/Stingray.Gui/bin/Release/net10.0/stingray-tex-gui \
    --screenshot docs/images/01-plan.png --bundle /path/to/some.patch_0
./src/Stingray.Gui/bin/Release/net10.0/stingray-tex-gui \
    --screenshot docs/images/02-resize.png --cap 2048 --bundle /path/to/some.patch_0
```

Regenerate them when the UI changes. Use your own bundle: game assets must not
be committed, and only the rendered window ends up in the PNG.

## Style

Match the surrounding code. Comments should explain *why*, not restate the code;
the existing ones flag non-obvious constraints (alignment rules, why a field is
preserved rather than recomputed). Public API gets XML docs.
