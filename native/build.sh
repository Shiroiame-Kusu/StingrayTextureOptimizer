#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the optional fast encoder. Run once; `dotnet build` then copies the
# result into every project's output automatically.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="${COMPRESSONATOR_SOURCE:-$root/external/compressonator}"

if [ ! -f "$src/cmp_core/source/cmp_core.cpp" ]; then
    echo "Fetching Compressonator sources into $src ..."
    mkdir -p "$src"
    git -C "$src" init -q .
    git -C "$src" remote add origin https://github.com/GPUOpen-Tools/compressonator.git 2>/dev/null || true
    git -C "$src" config core.sparseCheckout true
    printf 'cmp_core/\napplications/_libs/cmp_math/\n' > "$src/.git/info/sparse-checkout"
    git -C "$src" fetch -q --depth 1 origin master
    git -C "$src" checkout -q FETCH_HEAD
fi

cmake -S "$root/native" -B "$root/build/native" -DCMAKE_BUILD_TYPE=Release \
      -DCOMPRESSONATOR_SOURCE="$src"
cmake --build "$root/build/native" --config Release -j "$(nproc 2>/dev/null || echo 4)"

echo
echo "Built: $(find "$root/build/native" -name 'stingray_cmp.*' -type f | head -1)"
echo "Run 'dotnet build' and it will be copied into the app output."
