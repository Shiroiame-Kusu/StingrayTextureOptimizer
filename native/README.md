<!--
  SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
  SPDX-License-Identifier: GPL-3.0-or-later
-->

# Native encoder shim

`stingray_cmp` is a small shared library wrapping AMD Compressonator's
**CMP_Core** block codecs. It is what makes the fast encoder path work.

It is not committed as a binary — CI builds it per platform and drops it into
the release archive. The tool runs fine without it and falls back to the managed
encoder.

## Why a shim rather than the Compressonator CLI

| | CLI | This shim |
| --- | --- | --- |
| Payload | ~137 MB (Qt, OpenCV, DirectX/Vulkan plugins) | **~500 KB** |
| Per texture | process launch + temporary DDS in and out | one function call |

`cmp_core.h` also has no `extern "C"`, so its symbols are C++-mangled and cannot
be bound portably from .NET. And CMP_Core encodes a single 4×4 block per call —
a 4096×4096 surface is a million blocks, so the loop belongs in C++ rather than
across a million interop transitions.

## Building it

Requires CMake and a C++17 compiler.

```sh
git clone --depth 1 https://github.com/GPUOpen-Tools/compressonator.git /tmp/compressonator
cmake -S native -B build/native -DCMAKE_BUILD_TYPE=Release \
      -DCOMPRESSONATOR_SOURCE=/tmp/compressonator
cmake --build build/native --config Release
```

Put the resulting `stingray_cmp.so` / `.dll` next to the executable, or in a
`tools/` folder beside it, or point `STINGRAY_CMP_LIBRARY` at it.

Only `cmp_core/` and `applications/_libs/cmp_math/` are needed from the
Compressonator checkout, so CI uses a sparse checkout.

## Licence

The shim is GPL-3.0-or-later like the rest of this project. CMP_Core is MIT, and
its notice is in [COMPRESSONATOR-LICENSE.txt](COMPRESSONATOR-LICENSE.txt), which
ships in every release archive that contains the library.
