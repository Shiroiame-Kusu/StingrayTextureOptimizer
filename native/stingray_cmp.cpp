// SPDX-FileCopyrightText: 2026 Shiroiame-Kusu
// SPDX-License-Identifier: GPL-3.0-or-later
//
// A thin C shim over AMD Compressonator's CMP_Core block codecs.
//
// Two reasons this exists rather than calling CMP_Core directly:
//
//   * cmp_core.h has no `extern "C"`, so its symbols are C++-mangled and cannot
//     be bound portably from .NET.
//   * CMP_Core encodes one 4x4 block per call. A 4096x4096 surface is a million
//     blocks, and a million P/Invoke transitions would cost more than the
//     encoding. Looping here means one managed-to-native call per texture.
//
// CMP_Core is MIT licensed (see native/COMPRESSONATOR-LICENSE.txt).

#include <cstring>
#include <thread>
#include <vector>

#include "cmp_core.h"

// An ELF shared object exports every non-static symbol without being asked; a
// Windows DLL exports nothing at all unless each one says so. Without this the
// library builds and loads on Windows and has no entry points in it, which is
// what the loader then reports.
#if defined(_WIN32)
#  define STINGRAY_EXPORT __declspec(dllexport)
#else
#  define STINGRAY_EXPORT __attribute__((visibility("default")))
#endif

namespace
{
constexpr int FormatBc1 = 1;
constexpr int FormatBc3 = 3;
constexpr int FormatBc4 = 4;
constexpr int FormatBc5 = 5;
constexpr int FormatBc7 = 7;

int BlockBytes(int format)
{
    return (format == FormatBc1 || format == FormatBc4) ? 8 : 16;
}

// CMP_Core option objects hold scratch state, so each worker gets its own.
struct Options
{
    int format = 0;
    void* handle = nullptr;

    bool Create(int fmt, float quality, bool needsAlpha, int alphaThreshold)
    {
        format = fmt;
        int rc = 1;
        switch (fmt)
        {
        case FormatBc1:
            rc = CreateOptionsBC1(&handle);
            if (rc == 0 && handle)
            {
                SetQualityBC1(handle, quality);
                // Without a threshold BC1 is encoded opaque and a cutout mask
                // would come back fully solid.
                if (needsAlpha) SetAlphaThresholdBC1(handle, (unsigned char)alphaThreshold);
            }
            break;
        case FormatBc3:
            rc = CreateOptionsBC3(&handle);
            if (rc == 0 && handle) SetQualityBC3(handle, quality);
            break;
        case FormatBc4:
            rc = CreateOptionsBC4(&handle);
            if (rc == 0 && handle) SetQualityBC4(handle, quality);
            break;
        case FormatBc5:
            rc = CreateOptionsBC5(&handle);
            if (rc == 0 && handle) SetQualityBC5(handle, quality);
            break;
        case FormatBc7:
            rc = CreateOptionsBC7(&handle);
            if (rc == 0 && handle)
            {
                SetQualityBC7(handle, quality);
                SetAlphaOptionsBC7(handle, needsAlpha, false, false);
            }
            break;
        default:
            return false;
        }
        return rc == 0 && handle != nullptr;
    }

    ~Options()
    {
        if (!handle) return;
        switch (format)
        {
        case FormatBc1: DestroyOptionsBC1(handle); break;
        case FormatBc3: DestroyOptionsBC3(handle); break;
        case FormatBc4: DestroyOptionsBC4(handle); break;
        case FormatBc5: DestroyOptionsBC5(handle); break;
        case FormatBc7: DestroyOptionsBC7(handle); break;
        default: break;
        }
    }
};

// BC4 and BC5 take single-channel planes, so those blocks are gathered out of
// the interleaved surface first. BC1/BC3/BC7 read it in place via the stride.
void GatherChannel(const unsigned char* rgba, int width, int bx, int by,
                   int channel, unsigned char out[16])
{
    for (int j = 0; j < 4; ++j)
        for (int i = 0; i < 4; ++i)
            out[j * 4 + i] = rgba[(((size_t)by * 4 + j) * width + (size_t)bx * 4 + i) * 4 + channel];
}

bool EncodeRows(int format, const unsigned char* rgba, int width,
                int blocksX, int firstRow, int lastRow,
                unsigned char* out, float quality, bool needsAlpha, int alphaThreshold)
{
    Options options;
    if (!options.Create(format, quality, needsAlpha, alphaThreshold)) return false;

    const int blockBytes = BlockBytes(format);
    const unsigned int stride = (unsigned int)width * 4;

    for (int by = firstRow; by < lastRow; ++by)
    {
        for (int bx = 0; bx < blocksX; ++bx)
        {
            unsigned char* dst = out + ((size_t)by * blocksX + bx) * blockBytes;
            const unsigned char* src =
                rgba + (((size_t)by * 4) * width + (size_t)bx * 4) * 4;

            int rc = 0;
            switch (format)
            {
            case FormatBc1: rc = CompressBlockBC1(src, stride, dst, options.handle); break;
            case FormatBc3: rc = CompressBlockBC3(src, stride, dst, options.handle); break;
            case FormatBc7: rc = CompressBlockBC7(src, stride, dst, options.handle); break;
            case FormatBc4:
            {
                unsigned char r[16];
                GatherChannel(rgba, width, bx, by, 0, r);
                rc = CompressBlockBC4(r, 4, dst, options.handle);
                break;
            }
            case FormatBc5:
            {
                unsigned char r[16], g[16];
                GatherChannel(rgba, width, bx, by, 0, r);
                GatherChannel(rgba, width, bx, by, 1, g);
                rc = CompressBlockBC5(r, 4, g, 4, dst, options.handle);
                break;
            }
            default: return false;
            }

            if (rc != 0) return false;
        }
    }

    return true;
}
}  // namespace

extern "C" {

/// Bumped when the ABI below changes, so a stale native library is detected
/// rather than silently misused.
STINGRAY_EXPORT int stingray_cmp_abi_version(void)
{
    return 1;
}

/// Encodes a straight-RGBA surface. Dimensions must be multiples of 4.
/// Returns 0 on success, negative on failure.
STINGRAY_EXPORT int stingray_cmp_encode(int format,
                        const unsigned char* rgba, int width, int height,
                        unsigned char* out, unsigned long long outSize,
                        float quality, int threads,
                        int needsAlpha, int alphaThreshold)
{
    if (!rgba || !out || width <= 0 || height <= 0) return -1;
    if ((width % 4) != 0 || (height % 4) != 0) return -2;

    const int blocksX = width / 4;
    const int blocksY = height / 4;
    const unsigned long long expected =
        (unsigned long long)blocksX * blocksY * BlockBytes(format);
    if (outSize != expected) return -3;

    int workers = threads < 1 ? 1 : threads;
    if (workers > blocksY) workers = blocksY;

    if (workers <= 1)
    {
        return EncodeRows(format, rgba, width, blocksX, 0, blocksY,
                          out, quality, needsAlpha != 0, alphaThreshold)
                   ? 0
                   : -4;
    }

    // CMP_Core builds a set of global lookup tables the first time BC7 options
    // are created, behind a plain static flag that it sets to true *before* it
    // fills them, with no synchronisation of any kind:
    //
    //     if (g_rampsInitialized == TRUE) return;
    //     g_rampsInitialized = TRUE;
    //     <fills the tables>
    //
    // Every worker below creates its own options, so without this the second
    // thread to arrive is waved straight past that flag and encodes its blocks
    // against tables the first thread is still writing. The damage is a few
    // wrong blocks, silently, depending on timing.
    //
    // Build them here, on one thread, before any worker exists. Starting a
    // thread orders everything before it, so the workers cannot see half of
    // this.
    {
        Options warmup;
        if (!warmup.Create(format, quality, needsAlpha != 0, alphaThreshold)) return -4;
    }

    std::vector<std::thread> pool;
    std::vector<char> ok((size_t)workers, 1);
    const int rowsPer = (blocksY + workers - 1) / workers;

    for (int w = 0; w < workers; ++w)
    {
        const int first = w * rowsPer;
        const int last = (first + rowsPer) < blocksY ? (first + rowsPer) : blocksY;
        if (first >= last) { ok[(size_t)w] = 1; continue; }

        pool.emplace_back([&, w, first, last] {
            ok[(size_t)w] = EncodeRows(format, rgba, width, blocksX, first, last,
                                       out, quality, needsAlpha != 0, alphaThreshold)
                                ? 1
                                : 0;
        });
    }

    for (auto& t : pool) t.join();
    for (char v : ok)
        if (!v) return -4;

    return 0;
}
}
