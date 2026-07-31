# SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the optional fast encoder. Run once; `dotnet build` then copies the
# result into every project's output automatically.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$src = if ($env:COMPRESSONATOR_SOURCE) { $env:COMPRESSONATOR_SOURCE } else { Join-Path $root 'external/compressonator' }

if (-not (Test-Path (Join-Path $src 'cmp_core/source/cmp_core.cpp'))) {
    Write-Host "Fetching Compressonator sources into $src ..."
    New-Item -ItemType Directory -Force -Path $src | Out-Null
    git -C $src init -q .
    git -C $src remote add origin https://github.com/GPUOpen-Tools/compressonator.git 2>$null
    git -C $src config core.sparseCheckout true
    "cmp_core/`napplications/_libs/cmp_math/" | Set-Content (Join-Path $src '.git/info/sparse-checkout')
    git -C $src fetch -q --depth 1 origin master
    git -C $src checkout -q FETCH_HEAD
}

cmake -S (Join-Path $root 'native') -B (Join-Path $root 'build/native') -DCMAKE_BUILD_TYPE=Release -DCOMPRESSONATOR_SOURCE="$src"
cmake --build (Join-Path $root 'build/native') --config Release

Write-Host ""
Write-Host "Built. Run 'dotnet build' and it will be copied into the app output."
